// =============================================================================
//  Rug.Core.CSharp.Tests — Subtask 1.4.4
//  Validates the managed facade over the Rug.Core C-ABI:
//    - RecognizeAsync with WinRT and every discovered PP-OCR model
//    - UTF-8 correctness (no U+FFFD mojibake) and sane bounding boxes
//    - 10 consecutive calls + explicit GC to exercise SafeHandle release
//    - optional template match when tmpl_search/tmpl_target are present
//
//  Requires the native Rug.Core.dll (x64, with Rug_LoadImageFile) to be built and
//  copied next to this exe (the csproj copies it from ..\..\x64\$(Configuration)).
// =============================================================================

using System.Text;
using System.Buffers.Binary;
using System.Diagnostics;

using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

namespace Rug.Core.CSharp.Tests;

internal static class Program
{
    private static int s_failures;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Contains("--visual-only", StringComparer.Ordinal))
        {
            await InMemoryTemplateMatchAsync();
            return s_failures == 0 ? 0 : 1;
        }

        string root = FindRepoRoot();
        string imagesDir = Path.Combine(root, "tests", "test_images");
        string modelsDir = Path.Combine(root, "models", "ocr");
        Console.WriteLine($"repo root : {root}");
        Console.WriteLine($"images dir: {imagesDir}");
        Console.WriteLine($"models dir: {modelsDir}\n");

        string? image = FindFirst(imagesDir, "ocr_test_1") ?? FindFirst(imagesDir, "ocr_");
        if (image is null)
        {
            Console.WriteLine($"[ERROR] no ocr_* image found in {imagesDir}");
            return 2;
        }
        Console.WriteLine($"image: {Path.GetFileName(image)}\n");

        var ocr = new OcrService(modelsDir);

        // --- WinRT OCR ---
        await RunOcrAsync(ocr, image, OcrEngineType.WinRt, null, "WinRT");

        // --- PP-OCR (every discovered model) ---
        IReadOnlyList<string> ids = ocr.ListPaddleModelIds();
        Console.WriteLine($"discovered paddle models: [{string.Join(", ", ids)}]\n");
        if (ids.Count == 0)
            Console.WriteLine("[WARN] no Paddle models discovered; skipping PP-OCR checks.\n");
        foreach (string id in ids)
            await RunOcrAsync(ocr, image, OcrEngineType.Paddle, id, id);

        // --- Stress + GC / SafeHandle release ---
        await StressAndGcAsync(ocr, image);

        // --- Template match (optional) ---
        await TemplateMatchAsync(imagesDir);

        // --- Input controller: trajectory cadence + SafeHandle lifecycle ---
        await InputControllerAsync();

        Console.WriteLine($"done (failures={s_failures})");
        return s_failures == 0 ? 0 : 1;
    }

    private static async Task RunOcrAsync(OcrService ocr, string image, OcrEngineType type, string? id, string label)
    {
        Console.WriteLine($"=== OCR [{label}] on {Path.GetFileName(image)} ===");
        try
        {
            IReadOnlyList<OcrTextBlock> blocks = await ocr.RecognizeAsync(image, type, id);
            Console.WriteLine($"  blocks: {blocks.Count}");

            int shown = 0;
            foreach (OcrTextBlock b in blocks)
            {
                if (b.Text.Contains('\uFFFD'))
                    Fail($"[{label}] mojibake (U+FFFD) in \"{b.Text}\"");
                if (b.BoundingBox.Width <= 0 || b.BoundingBox.Height <= 0 ||
                    b.BoundingBox.X < 0 || b.BoundingBox.Y < 0)
                    Fail($"[{label}] implausible box {b.BoundingBox} for \"{b.Text}\"");

                if (shown++ < 8)
                    Console.WriteLine($"   - \"{b.Text}\"  score={b.Score:F2}  " +
                                      $"box=({b.BoundingBox.X},{b.BoundingBox.Y},{b.BoundingBox.Width},{b.BoundingBox.Height})");
            }
            if (blocks.Count == 0)
                Console.WriteLine("  [WARN] no text recognized");
        }
        catch (Exception ex)
        {
            Fail($"[{label}] threw {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine();
    }

    private static async Task StressAndGcAsync(OcrService ocr, string image)
    {
        Console.WriteLine("=== stress: 10x WinRT + GC (SafeHandle release) ===");
        try
        {
            for (int i = 0; i < 10; i++)
            {
                IReadOnlyList<OcrTextBlock> blocks = await ocr.RecognizeAsync(image, OcrEngineType.WinRt);
                if (i == 0) Console.WriteLine($"  first pass blocks: {blocks.Count}");
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Console.WriteLine("  10 iterations + explicit GC completed without crash.");
        }
        catch (Exception ex)
        {
            Fail($"stress threw {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine();
    }

    private static async Task TemplateMatchAsync(string imagesDir)
    {
        Console.WriteLine("=== template match ===");
        string? search = FindFirst(imagesDir, "tmpl_search");
        string? target = FindFirst(imagesDir, "tmpl_target");
        if (search is null || target is null)
        {
            Console.WriteLine("  [SKIP] tmpl_search / tmpl_target not found\n");
            return;
        }
        try
        {
            var svc = new TemplateMatchService();
            IReadOnlyList<TemplateMatchResult> matches = await svc.MatchAsync(target, search, 0.8f);
            Console.WriteLine($"  matches (>=0.80): {matches.Count}");
            foreach (TemplateMatchResult m in matches)
                Console.WriteLine($"   - ({m.X},{m.Y}) score={m.Score:F4}");
        }
        catch (Exception ex)
        {
            Fail($"template match threw {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine();
    }

    private static async Task InMemoryTemplateMatchAsync()
    {
        Console.WriteLine("=== in-memory template match (Task 2.3) ===");
        string path = Path.Combine(Path.GetTempPath(), "rug-template-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            const int width = 16, height = 16, stride = width * 4;
            byte[] pixels = new byte[stride * height];
            for (int pixel = 0; pixel < width * height; pixel++)
            {
                pixels[pixel * 4] = 10;
                pixels[pixel * 4 + 1] = 20;
                pixels[pixel * 4 + 2] = 30;
                pixels[pixel * 4 + 3] = 255;
            }
            var bmpPixels = new byte[3 * 3 * 3];
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                {
                    int blue = 30 + x * 53 + y * 7;
                    int green = 35 + x * 17 + y * 39;
                    int red = 40 + x * 13 + y * 47;
                    int target = (7 + y) * stride + (5 + x) * 4;
                    pixels[target] = (byte)blue;
                    pixels[target + 1] = (byte)green;
                    pixels[target + 2] = (byte)red;
                    int template = (y * 3 + x) * 3;
                    bmpPixels[template] = (byte)blue;
                    bmpPixels[template + 1] = (byte)green;
                    bmpPixels[template + 2] = (byte)red;
                }
            await File.WriteAllBytesAsync(path, CreateBmp(bmpPixels));
            var frame = new CapturedFrame(pixels, width, height, stride);
            var matcher = new TemplateMatchService();
            IReadOnlyList<TemplateMatchResult> warmup = await matcher.MatchFrameAsync(frame, path, 0.9f);
            if (!warmup.Any(hit => hit.X == 5 && hit.Y == 7 && hit.Score > 0.99))
            {
                Fail("native MatchFrameAsync did not find the synthetic template at (5,7)");
                return;
            }
            using Process process = Process.GetCurrentProcess();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            long baselineBytes = process.PrivateMemorySize64;
            int baselineHandles = process.HandleCount;
            for (int i = 0; i < 1000; i++)
            {
                IReadOnlyList<TemplateMatchResult> hits = await matcher.MatchFrameAsync(frame, path, 0.9f);
                if (hits.Count == 0 || hits[0].X != 5 || hits[0].Y != 7)
                {
                    Fail($"native frame match changed at iteration {i}");
                    return;
                }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            long growth = process.PrivateMemorySize64 - baselineBytes;
            int handleGrowth = process.HandleCount - baselineHandles;
            Console.WriteLine($"  1,000 matches: private-memory delta={growth:N0} bytes, handle delta={handleGrowth}");
            if (growth > 32 * 1024 * 1024 || handleGrowth > 32)
                Fail("native frame matching retained excessive memory or handles");
        }
        catch (Exception ex)
        {
            Fail($"native frame match threw {ex.GetType().Name}: {ex.Message}");
        }
        finally { File.Delete(path); }
    }

    private static byte[] CreateBmp(byte[] bgr)
    {
        const int width = 3, height = 3, rowStride = 12, offset = 54;
        byte[] bmp = new byte[offset + rowStride * height];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), offset);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), rowStride * height);
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(bgr, (height - 1 - y) * width * 3,
                bmp, offset + y * rowStride, width * 3);
        return bmp;
    }

    private static async Task InputControllerAsync()
    {
        Console.WriteLine("=== input controller: trajectory cadence + SafeHandle lifecycle ===");

        // (1) Dynamic polling cadence via the dry-run planner (no real OS input).
        try
        {
            await using var input = new InputService(InputMode.Win32Software);
            IReadOnlyList<TrajectorySample> plan =
                await input.PlanTrajectoryAsync(0, 0, 800, 600, TrajectoryType.CubicBezier);

            if (plan.Count < 2)
            {
                Fail($"input: trajectory too short ({plan.Count} samples)");
            }
            else
            {
                float min = float.MaxValue, max = float.MinValue, sum = 0;
                int fast = 0, slow = 0;  // fast = mid-trajectory, slow = near the ends
                foreach (TrajectorySample s in plan)
                {
                    if (s.DelayMs < min) min = s.DelayMs;
                    if (s.DelayMs > max) max = s.DelayMs;
                    sum += s.DelayMs;
                    if (s.DelayMs <= 4.0f) fast++;
                    if (s.DelayMs >= 7.0f) slow++;

                    // Every delay must sit inside the documented dynamic band
                    // (1.5-3ms fast, 10-25ms slow) plus the +-0.5ms jitter margin.
                    if (s.DelayMs < 0.5f || s.DelayMs > 26.0f)
                        Fail($"input: sample delay {s.DelayMs:F2}ms outside [0.5,26] band");
                }
                float avg = sum / plan.Count;
                Console.WriteLine($"  samples: {plan.Count}  delay min={min:F2} avg={avg:F2} max={max:F2} ms");
                Console.WriteLine($"  fast(<=4ms): {fast}   slow(>=7ms): {slow}");

                // A fixed-interval mover would have fast==0 or slow==0. The humanizer
                // must produce BOTH: quick mid-flight samples and slow end samples.
                if (fast == 0) Fail("input: no fast (<=4ms) samples -> cadence is not dynamic");
                if (slow == 0) Fail("input: no slow (>=7ms) samples -> cadence is not dynamic");
                if (max - min < 3.0f) Fail($"input: delay spread too narrow ({min:F2}..{max:F2})");

                // The last sample must land exactly on the requested endpoint.
                TrajectorySample last = plan[plan.Count - 1];
                if (last.X != 800 || last.Y != 600)
                    Fail($"input: trajectory endpoint ({last.X},{last.Y}) != (800,600)");
            }
        }
        catch (Exception ex)
        {
            Fail($"input: planner threw {ex.GetType().Name}: {ex.Message}");
        }

        // (2) SafeHandle lifecycle: valid on create, closed after dispose.
        try
        {
            int rc = RugCoreNative.Rug_CreateInputController(
                RugInputModeNative.Win32Software, 0, out nint raw);
            if (rc != RugStatus.Ok || raw == 0)
            {
                Fail($"input: Rug_CreateInputController failed (status {rc})");
            }
            else
            {
                var handle = new InputControllerHandle(raw);
                if (handle.IsInvalid) Fail("input: fresh handle reported IsInvalid");
                if (handle.IsClosed) Fail("input: fresh handle reported IsClosed");
                handle.Dispose();
                if (!handle.IsClosed) Fail("input: handle not closed after Dispose");
            }
        }
        catch (Exception ex)
        {
            Fail($"input: lifecycle threw {ex.GetType().Name}: {ex.Message}");
        }

        // (3) 100 create/dispose cycles + explicit GC to shake out leaks/double-free.
        try
        {
            for (int i = 0; i < 100; i++)
            {
                await using var input = new InputService(InputMode.Win32Software);
                _ = await input.PlanTrajectoryAsync(0, 0, 100, 100, TrajectoryType.Straight);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Console.WriteLine("  100 create/dispose cycles + GC completed without crash.");
        }
        catch (Exception ex)
        {
            Fail($"input: stress threw {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
    }

    private static void Fail(string message)
    {
        s_failures++;
        Console.WriteLine($"  [FAIL] {message}");
    }

    private static string? FindFirst(string dir, string prefix)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".bmp" })
        {
            string p = Path.Combine(dir, prefix + ext);
            if (File.Exists(p)) return p;
        }
        return Directory.EnumerateFiles(dir, prefix + "*.*").FirstOrDefault(f =>
        {
            string e = Path.GetExtension(f).ToLowerInvariant();
            return e is ".png" or ".jpg" or ".jpeg" or ".bmp";
        });
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rug.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
