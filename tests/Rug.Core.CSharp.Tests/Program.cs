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

using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

namespace Rug.Core.CSharp.Tests;

internal static class Program
{
    private static int s_failures;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

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
