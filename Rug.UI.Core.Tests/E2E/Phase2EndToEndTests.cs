using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using RugTaskScheduler = Rug.UI.Core.Services.TaskScheduler;

namespace Rug.UI.Core.Tests.E2E;

/// <summary>
/// Explicit, hardware-backed test. It never substitutes a fake capture, OCR, match,
/// input, scheduler or anomaly service. Run only with a visible Minecraft CN menu.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public static class Phase2EndToEndTests
{
    private const string PluginId = "minecraft-cn-phase2-e2e";
    private const string ExpectedReason = "阶段二端到端预期事故";
    private static readonly WindowSpyService Windows = new();

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            bool probeOnly = args.Contains("--probe", StringComparer.OrdinalIgnoreCase);
            bool preflightOnly = args.Contains("--preflight", StringComparer.OrdinalIgnoreCase);
            nint hwnd = ResolveWindow(args);
            Console.WriteLine($"Minecraft HWND: 0x{hwnd:X}, title: {Windows.ResolveWindow(hwnd)?.Title}");
            if (probeOnly) return 0;
            if (args.Contains("--escape", StringComparer.OrdinalIgnoreCase))
            {
                BringMinecraftToFront(hwnd);
                await using var input = new InputService();
                await input.SetTargetAsync(hwnd, background: false);
                await input.KeyPressAsync(27);
                Console.WriteLine("Sent Esc to Minecraft.");
                return 0;
            }
            await ExecuteAsync(hwnd, preflightOnly);
            if (preflightOnly) return 0;
            Console.WriteLine("PASS: Minecraft CN Phase 2 real-window E2E");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static async Task ExecuteAsync(nint hwnd, bool preflightOnly)
    {
        string? modelRoot = FindModelRoot();
        Check(modelRoot is not null, "PaddleOCR Medium model directory is missing.");
        var ocr = new OcrService(modelRoot);
        Check(ocr.ListPaddleModelIds().Contains("ppocr_v6_medium"),
            "PaddleOCR Medium model is not discoverable by the native engine.");

        using var preflight = new CaptureServiceScope(new CaptureService());
        await preflight.Capture.StartAsync(hwnd);
        CapturedFrame frame = await WaitForFrameAsync(preflight.Capture);
        Check(IsVisualFrame(frame), "WGC returned a black or near-uniform frame; make the game visible.");
        Console.WriteLine($"WGC frame: {frame.Width} x {frame.Height}");

        OcrTextBlock options = await LocateMenuAndMeasureOcrAsync(ocr, frame);
        string root = Path.Combine(AppContext.BaseDirectory, "e2e-run", Guid.NewGuid().ToString("N"));
        string pluginDir = Path.Combine(root, PluginId);
        string anomalyDir = Path.Combine(root, "logs", "anomalies");
        Directory.CreateDirectory(pluginDir);
        string framePath = Path.Combine(root, "preflight.bmp");
        WriteTemplateBmp(frame, new Rect(0, 0, frame.Width, frame.Height), framePath);
        Console.WriteLine("Preflight frame: " + framePath);
        string sourceScript = Path.Combine(AppContext.BaseDirectory, "E2E", "Scripts", "minecraft_cn_e2e_loop.lua");
        File.Copy(sourceScript, Path.Combine(pluginDir, "main.lua"));
        await File.WriteAllTextAsync(Path.Combine(pluginDir, "manifest.json"), """
            {
              "id": "minecraft-cn-phase2-e2e",
              "name": "Minecraft CN real-window E2E",
              "version": "1.0.0",
              "entry": "main.lua",
              "targetProcess": "Minecraft",
              "inputDelivery": "Win32SendInput",
              "permissions": ["timer", "vision.capture", "vision.match", "vision.ocr", "input", "agent"]
            }
            """);
        int optionsX = options.BoundingBox.X + options.BoundingBox.Width / 2;
        int optionsY = options.BoundingBox.Y + options.BoundingBox.Height / 2;
        await File.WriteAllTextAsync(Path.Combine(pluginDir, "config.json"), JsonSerializer.Serialize(new
        {
            fields = new[]
            {
                new { key = "options_x", type = "Slider", @default = optionsX, min = 0, max = frame.Width },
                new { key = "options_y", type = "Slider", @default = optionsY, min = 0, max = frame.Height }
            }
        }));
        string templatePath = Path.Combine(pluginDir, "options_template.bmp");
        WriteTemplateBmp(frame, options.BoundingBox, templatePath);
        var matcher = new TemplateMatchService();
        IReadOnlyList<TemplateMatchResult> matches = await matcher.MatchFrameAsync(frame, templatePath, 0.75f);
        Check(matches.Count > 0, "OpenCV could not match the real menu template in its source WGC frame.");
        Console.WriteLine($"OpenCV source-frame score: {matches.Max(x => x.Score):F3}");
        await preflight.Capture.StopAsync();
        if (preflightOnly) return;
        BringMinecraftToFront(hwnd);

        var logs = new LogSink();
        var captures = new ConcurrentBag<CaptureService>();
        await using var scheduler = new RugTaskScheduler(
            new PluginManager(new TestLogger<PluginManager>(logs)), root,
            () => { var capture = new CaptureService(); captures.Add(capture); return capture; },
            () => new InputService(),
            (capture, input) => new LuaRuntime(capture, ocr, input,
                new TestLogger<LuaRuntime>(logs), templateMatcher: new TemplateMatchService()),
            new TestLogger<RugTaskScheduler>(logs),
            new AnomalyLogger(new TestLogger<AnomalyLogger>(logs), anomalyDir),
            tickInterval: TimeSpan.FromMilliseconds(100));

        Guid id = await scheduler.StartTaskAsync(PluginId, hwnd);
        TaskExecutionContext context = scheduler.Instances.Single(instance => instance.InstanceId == id);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            while (scheduler.Instances.Any(instance => instance.InstanceId == id))
                await Task.Delay(100, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            await scheduler.StopTaskAsync(id);
            throw new TimeoutException("E2E script did not reach its anomaly hook within three minutes.");
        }

        Check(context.TaskStatus == AutomationTaskStatus.Faulted, "The deliberate anomaly did not fault the task.");
        Check(context.LastError?.Message.Contains(ExpectedReason, StringComparison.Ordinal) == true,
            "Task failed before the intended anomaly: " + context.LastError);
        foreach (string marker in new[] { "E2E:on_init", "E2E:template_matched", "E2E:clicked_options",
                     "E2E:settings_visible", "E2E:pressed_escape", "E2E:returned_menu", "E2E:on_stop" })
            Check(logs.Contains(marker), "Lifecycle/visual/input marker missing: " + marker);
        Check(captures.Count == 1 && captures.All(capture => !capture.IsCapturing),
            "The scheduler did not release its WGC session.");

        string jsonPath = Directory.GetFiles(anomalyDir, "*.json").Single();
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, Encoding.UTF8));
        JsonElement record = document.RootElement;
        Check(record.GetProperty("pluginId").GetString() == PluginId, "Wrong anomaly plugin ID.");
        Check(record.GetProperty("instanceId").GetGuid() == id, "Wrong anomaly instance ID.");
        Check(record.GetProperty("reason").GetString() == ExpectedReason, "Chinese anomaly reason was corrupted.");
        Check(record.GetProperty("scriptContext").GetString()?.Contains("stack traceback", StringComparison.OrdinalIgnoreCase) == true,
            "Lua traceback is missing from anomaly context.");
        Check(record.TryGetProperty("agent_resolution", out JsonElement resolution) && resolution.ValueKind == JsonValueKind.Null,
            "Phase 4 agent_resolution placeholder is missing.");
        string pngPath = Path.Combine(anomalyDir, record.GetProperty("screenshotPath").GetString()!);
        Check(File.Exists(pngPath) && IsVisualPng(await File.ReadAllBytesAsync(pngPath)),
            "The anomaly PNG is absent, black, malformed, or nearly uniform.");
        Console.WriteLine($"Anomaly JSON: {jsonPath}");
        Console.WriteLine($"Anomaly PNG:  {pngPath}");
    }

    private static async Task<OcrTextBlock> LocateMenuAndMeasureOcrAsync(OcrService ocr, CapturedFrame frame)
    {
        IReadOnlyList<OcrTextBlock> winrt = [];
        try { winrt = await ocr.RecognizeFrameAsync(frame, OcrEngineType.WinRt, "zh-CN"); }
        catch (Exception ex) { Console.WriteLine("WinRT zh-CN OCR unavailable: " + ex.Message); }
        Console.WriteLine("WinRT OCR: " + string.Join(" | ", winrt.Select(x => x.Text)));

        IReadOnlyList<OcrTextBlock> paddle = await ocr.RecognizeFrameAsync(
            frame, OcrEngineType.Paddle, "ppocr_v6_medium");
        Console.WriteLine("Paddle Medium OCR: " + string.Join(" | ", paddle.Select(x => x.Text)));
        static OcrTextBlock? Find(IReadOnlyList<OcrTextBlock> blocks, string text) =>
            blocks.FirstOrDefault(block => block.Text.Contains(text, StringComparison.Ordinal));
        OcrTextBlock? options = Find(winrt, "选项") ?? Find(paddle, "选项");
        OcrTextBlock? single = Find(winrt, "单人游戏") ?? Find(paddle, "单人游戏");
        Check(options is not null && single is not null,
            "WinRT and Paddle Medium together did not locate both Chinese main-menu labels.");
        OcrTextBlock selected = options!;
        Check(selected.BoundingBox.Width > 0 && selected.BoundingBox.Height > 0 &&
              selected.BoundingBox.X >= 0 && selected.BoundingBox.Y >= 0 &&
              selected.BoundingBox.X + selected.BoundingBox.Width <= frame.Width &&
              selected.BoundingBox.Y + selected.BoundingBox.Height <= frame.Height,
            "OCR option bounding box is outside the captured client area.");
        Console.WriteLine($"Selected OCR: {(Find(winrt, "选项") is not null ? "WinRT" : "Paddle Medium")}, options box: {selected.BoundingBox}");
        return selected;
    }

    private static async Task<CapturedFrame> WaitForFrameAsync(CaptureService capture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            CapturedFrame? frame = await capture.GrabFrameAsync(deadline.Token);
            if (frame is not null) return frame;
            await Task.Delay(100, deadline.Token);
        }
    }

    private static void WriteTemplateBmp(CapturedFrame frame, Rect box, string path)
    {
        int left = Math.Max(0, box.X - 10);
        int top = Math.Max(0, box.Y - 6);
        int right = Math.Min(frame.Width, box.X + box.Width + 10);
        int bottom = Math.Min(frame.Height, box.Y + box.Height + 6);
        int width = right - left, height = bottom - top;
        Check(width >= 8 && height >= 8, "The OCR box is too small for an OpenCV template.");
        int rowBytes = checked((width * 3 + 3) & ~3);
        byte[] bmp = new byte[checked(54 + rowBytes * height)];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), rowBytes * height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Buffer.BlockCopy(frame.Pixels, (top + height - 1 - y) * frame.Stride + (left + x) * 4,
                    bmp, 54 + y * rowBytes + x * 3, 3);
        File.WriteAllBytes(path, bmp);
    }

    private static bool IsVisualFrame(CapturedFrame frame)
    {
        if (frame.Width < 100 || frame.Height < 100 || frame.Stride < frame.Width * 4) return false;
        var colors = new HashSet<int>();
        int lit = 0;
        for (int y = 0; y < frame.Height; y += Math.Max(1, frame.Height / 60))
            for (int x = 0; x < frame.Width; x += Math.Max(1, frame.Width / 80))
            {
                int i = y * frame.Stride + x * 4;
                int color = (frame.Pixels[i + 2] << 16) | (frame.Pixels[i + 1] << 8) | frame.Pixels[i];
                colors.Add(color >> 6);
                if ((frame.Pixels[i] + frame.Pixels[i + 1] + frame.Pixels[i + 2]) > 48) lit++;
            }
        return colors.Count >= 16 && lit >= 50;
    }

    private static bool IsVisualPng(byte[] png)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 45 || !png.AsSpan(0, 8).SequenceEqual(signature)) return false;
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (width < 100 || height < 100 || png[24] != 8 || png[25] != 6) return false;
        using var compressed = new MemoryStream();
        for (int offset = 8; offset + 12 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (length < 0 || offset + 12L + length > png.Length) return false;
            if (png.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8))
                compressed.Write(png, offset + 8, length);
            offset += 12 + length;
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        byte[] raw = new byte[checked((width * 4 + 1) * height)];
        zlib.ReadExactly(raw);
        byte[] bgra = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            int source = y * (width * 4 + 1);
            if (raw[source] != 0) return false;
            for (int x = 0; x < width; x++)
            {
                int src = source + 1 + x * 4, dst = (y * width + x) * 4;
                bgra[dst] = raw[src + 2]; bgra[dst + 1] = raw[src + 1];
                bgra[dst + 2] = raw[src]; bgra[dst + 3] = raw[src + 3];
            }
        }
        return IsVisualFrame(new CapturedFrame(bgra, width, height, width * 4));
    }

    private static string? FindModelRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string path = Path.Combine(current.FullName, "models", "ocr");
            if (Directory.Exists(path)) return path;
            current = current.Parent;
        }
        return null;
    }

    private static nint ResolveWindow(string[] args)
    {
        string? argument = args.SkipWhile(x => !x.Equals("--hwnd", StringComparison.OrdinalIgnoreCase))
            .Skip(1).FirstOrDefault() ?? Environment.GetEnvironmentVariable("RUG_MINECRAFT_HWND");
        if (argument is not null)
        {
            string digits = argument.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? argument[2..] : argument;
            nint explicitHwnd = (nint)Convert.ToInt64(digits,
                argument.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10);
            ValidateWindow(explicitHwnd);
            return explicitHwnd;
        }
        WindowInfo[] found = Windows.FindVisibleWindows().Where(info => LooksLikeMinecraft(info.Title)).ToArray();
        Check(found.Length == 1,
            $"Expected one visible Minecraft window, found {found.Length}. Start the Chinese main menu or pass --hwnd 0x... .");
        ValidateWindow(found[0].Hwnd);
        return found[0].Hwnd;
    }

    private static void ValidateWindow(nint hwnd)
    {
        WindowInfo? info = Windows.ResolveWindow(hwnd);
        Check(info is not null && info.ClientWidth >= 100 && info.ClientHeight >= 100,
            "Minecraft HWND is missing, hidden, minimized, or too small.");
        Check(LooksLikeMinecraft(info!.Title), "The target title is not Minecraft: " + info.Title);
    }

    private static bool LooksLikeMinecraft(string title) =>
        title.Contains("Minecraft", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("我的世界", StringComparison.Ordinal);

    private static void BringMinecraftToFront(nint hwnd)
    {
        if (Windows.IsForeground(hwnd)) return;
        _ = Windows.BringToFront(hwnd);
        Check(Windows.IsForeground(hwnd),
            "Minecraft could not be focused for SendInput. Focus the game manually and rerun.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CaptureServiceScope(CaptureService capture) : IDisposable
    {
        public CaptureService Capture { get; } = capture;
        public void Dispose() => Capture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class LogSink
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public void Add(string line) { _lines.Enqueue(line); Console.WriteLine(line); }
        public bool Contains(string text) => _lines.Any(line => line.Contains(text, StringComparison.Ordinal));
    }

    private sealed class TestLogger<T>(LogSink sink) : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) => EmptyScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Add($"[{logLevel}] {typeof(T).Name}: {formatter(state, exception)} {exception?.Message}");
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }

}
