using System.Runtime.InteropServices;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

internal static class CaptureGeometryTests
{
    private static void Check(bool condition, string reason) => RecoveryTests.Check(condition, reason);
    public static void Run()
    {
        Check(Marshal.SizeOf<RugCaptureFacts>() == 136 && Marshal.OffsetOf<RugCaptureFacts>(nameof(RugCaptureFacts.SystemRelative100ns)).ToInt32() == 16,
            "native facts ABI layout");
        var t = new NativeFrameTime(10_000_000, 3_000_000, 6_000_000);
        Check(t.AgeAtNativeSample == TimeSpan.FromSeconds(1) && t.AgeAt(6_000_000, 3_000_000, "fake TimeProvider") is null &&
            t.AgeAt(6_000_000, 10_000_000, "Windows boot QPC") is null, "clock domains must not mix");
        Check(new NativeFrameTime(0, 1, 2).AgeAtNativeSample is null && new NativeFrameTime(99, 0, 0).AgeAtNativeSample is null &&
            new NativeFrameTime(99, 10_000_000, 98).AgeAtNativeSample is null, "unknown/future native time");
        Check(new NativeFrameTime(1, long.MaxValue, long.MaxValue).AgeAtNativeSample == TimeSpan.FromTicks(9_999_999), "QPC overflow/precision");
        var crop = new PixelTransform("image", "client", new(8, 31, 200, 100), new(0, 0, 200, 100), 1, 1, -8, -31);
        Check(crop.TryMapPixel(88, 101, out int x, out int y) && x == 80 && y == 70 &&
            !crop.TryMap(7, 40, out _, out _) && !crop.TryMap(208, 40, out _, out _), "non-client offset/bounds");
        var letterbox = new PixelTransform("client", "model", new(0, 0, 100, 50), new(0, 25, 200, 100), 2, 2, 0, 25).Inverse();
        Check(letterbox.TryMapPixel(80, 65, out x, out y) && x == 40 && y == 20 &&
            !letterbox.TryMap(80, 24, out _, out _) && !letterbox.TryMap(80, 125, out _, out _), "letterbox padding must reject");
        Check(!crop.TryMapPixel(207.9, 40, out _, out _) && !crop.TryMap(double.NaN, 40, out _, out _), "rounding cannot clamp out-of-bounds");
        var f = RugCaptureFacts.Create(); f.Flags = 7; f.GeometryStatus = 1; f.SystemRelative100ns = 1; f.QpcFrequency = 10_000_000;
        f.QpcSample = 100; f.GeometryBeforeQpc = 1; f.GeometryAfterQpc = 99;
        f.BufferWidth = f.ContentWidth = 220; f.BufferHeight = f.ContentHeight = 150;
        f.ClientWidth = 200; f.ClientHeight = 100; f.ImageClientX = 8; f.ImageClientY = 31;
        f.BoundWindow = f.CapturedWindow = 1; f.BoundLifetime = f.CapturedLifetime = 2; f.Dpi = 144;
        var frame = new RugFrame { Width = 220, Height = 150, Stride = 896, DataLength = 134400, Format = 1 };
        Check(CaptureService.ConvertFacts(f, frame).Geometry.ClientGeometryVerified, "supported exact DWM mapping");
        foreach (uint status in new uint[] { 0, 2, 3, 4, 5 })
        { f.GeometryStatus = status; Check(CaptureService.ConvertFacts(f, frame).Geometry.ImageToClient is null, "unverified geometry must not acquire identity mapping"); }
        f.GeometryStatus = 1; f.CapturedLifetime++;
        Check(!CaptureService.ConvertFacts(f, frame).Geometry.ClientGeometryVerified, "HWND reuse must invalidate lifetime");
        Console.WriteLine("Capture geometry PASS: QPC rational conversion/domain refusal, invalid time, crop/ROI/borders, scale/letterbox padding, rounding and window identity rejection");
    }

    public static async Task RunNativeAsync()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Rug.slnx"))) repo = repo.Parent;
        string config = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string dll = Path.Combine(repo!.FullName, "x64", config, "Rug.Core.dll");
        nint module = NativeLibrary.Load(dll, typeof(CaptureService).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies);
        NativeLibrary.SetDllImportResolver(typeof(CaptureService).Assembly, (name, _, _) => name is "Rug.Core" or "Rug.Core.dll" ? module : 0);
        using var target = new CaptureGeometryDiagnosticWindow();
        nint hwnd = await target.Ready;
        await using var capture = new CaptureService(exactBoundWindow: true);
        await capture.StartAsync(hwnd);
        async Task<CapturedFrame> Grab()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var c = await capture.GrabFrameAsync(deadline.Token);
                if (c?.CaptureFacts?.Geometry.ClientGeometryVerified == true) return c;
                if (c is not null) Console.WriteLine($"geometry pending status={c.CaptureFacts?.GeometryStatus} content={c.CaptureFacts?.ContentWidth}x{c.CaptureFacts?.ContentHeight} buffer={c.Width}x{c.Height} client={c.CaptureFacts?.Geometry.ClientWidth}x{c.CaptureFacts?.Geometry.ClientHeight}");
                await Task.Delay(30, deadline.Token);
            }
        }
        void Validate(CapturedFrame c)
        {
            int minX = int.MaxValue, minY = int.MaxValue, count = 0;
            for (int y = 0; y < c.Height; y++) for (int x = 0; x < c.Width; x++)
            {
                int i = y * c.Stride + x * 4;
                if (c.Pixels[i] < 15 && c.Pixels[i + 1] < 15 && c.Pixels[i + 2] > 240)
                { minX = Math.Min(minX, x); minY = Math.Min(minY, y); count++; }
            }
            var facts = c.CaptureFacts!;
            Check(count == 400 && facts.Geometry.ImageToClient!.TryMapPixel(minX, minY, out int x1, out int y1) && x1 == 80 && y1 == 70,
                $"real WGC marker mapping failed count={count} image={minX},{minY}");
            Check(facts.NativeTime?.AgeAtNativeSample is not null && facts.Windows.SameWindow, "real frame time/window missing");
            Console.WriteLine($"WGC marker PASS: image=({minX},{minY}) -> client=(80,70), content={facts.ContentWidth}x{facts.ContentHeight}, buffer={c.Width}x{c.Height}, client={facts.Geometry.ClientWidth}x{facts.Geometry.ClientHeight}, dpi={facts.Dpi}, compositor100ns={facts.NativeTime!.SystemRelative100ns}, nativeSampleAge={facts.NativeTime.AgeAtNativeSample}");
        }
        var first = await Grab(); Validate(first);
        target.Resize(800, 530);
        CapturedFrame resized;
        do { resized = await Grab(); } while (resized.Width == first.Width);
        Validate(resized);
        if (target.MoveToOtherMonitor())
        {
            var moved = await Grab(); Validate(moved);
            Console.WriteLine($"WGC cross-monitor PASS: dpi {resized.CaptureFacts!.Dpi} -> {moved.CaptureFacts!.Dpi}; " +
                (resized.CaptureFacts.Dpi != moved.CaptureFacts.Dpi ? "real DPI transition PASS" : "DPI transition NOT RUN (monitors have same DPI)"));
        }
        else Console.WriteLine("WGC cross-monitor/DPI transition NOT RUN: no second accessible monitor");
        using var other = new CaptureGeometryDiagnosticWindow(); await other.Ready; other.Resize(1000, 700);
        // New and old exports coexist on a real capturer, and validation failures allocate no pixels.
        var cfg = new RugCaptureConfig { TargetWindow = hwnd, OcrUpscale = 1 };
        Check(RugCoreNative.Rug_CreateCapturer(in cfg, out nint old) == 0, "old create ABI");
        try
        {
            Check(RugCoreNative.Rug_GrabFrame(old, out var pixels) == 0, "old frame ABI"); RugCoreNative.Rug_FreeBuffer(pixels.Data);
            var selected = RugCaptureFacts.Create();
            Check(RugCoreNative.Rug_GrabFrameWithFacts(old, out pixels, ref selected) == 0, "legacy facts capture");
            try { Check(selected.CapturedWindow != selected.BoundWindow && selected.GeometryStatus == 3 && (selected.Flags & 2) == 0,
                "same-process alternate window falsely mapped to bound client"); }
            finally { RugCoreNative.Rug_FreeBuffer(pixels.Data); }
            var bad = RugCaptureFacts.Create(); bad.Version = 2;
            Check(RugCoreNative.Rug_GrabFrameWithFacts(old, out pixels, ref bad) == -2 && pixels.Data == 0, "unsupported facts ABI version");
        }
        finally { RugCoreNative.Rug_DestroyCapturer(old); }
        target.Dispose();
        Check(await capture.GrabFrameAsync() is null, "destroyed bound HWND retained capture identity");
        await capture.StopAsync(); Check(await capture.GrabFrameAsync() is null, "stop retained native capture");
        Console.WriteLine("WGC geometry PASS: ordinary bordered window + resize, old/new C-ABI, alternate selection and destroyed lifetime");
    }
}
