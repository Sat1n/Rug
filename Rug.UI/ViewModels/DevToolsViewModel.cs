using System.Collections.ObjectModel;
using System.Diagnostics;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Helpers;
using Rug.UI.Core.Models;
using Rug.UI.Models;

namespace Rug.UI.ViewModels;

/// <summary>
/// Backing view-model for the Dev Tools sandbox. Orchestrates window binding,
/// the WGC+OCR live-preview loop, the input self-tests and the 100x full-chain
/// stress run. All native work happens on background threads (the services use
/// Task.Run/MTA); UI-facing updates are pushed through <see cref="Logged"/> and
/// <see cref="FrameReady"/> events that the view marshals onto the UI thread.
/// </summary>
public partial class DevToolsViewModel : ObservableObject
{
    // Virtual-key codes that must NEVER be synthesized (Windows key). Fn is not a
    // VK at all (hardware-only), so it cannot appear in the sequence by construction.
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private readonly ICaptureService _capture;
    private readonly IWindowSpyService _windowSpy;
    private readonly IOcrService _ocr;
    private readonly IInputService _input;

    private CancellationTokenSource? _previewCts;

    public DevToolsViewModel(
        ICaptureService capture, IWindowSpyService windowSpy, IOcrService ocr, IInputService input)
    {
        _capture = capture;
        _windowSpy = windowSpy;
        _ocr = ocr;
        _input = input;

        try
        {
            foreach (string id in _ocr.ListPaddleModelIds())
            {
                PaddleModelIds.Add(id);
            }
            if (PaddleModelIds.Count > 0)
            {
                SelectedPaddleModelId = PaddleModelIds[0];
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, "Init", $"加载 Paddle 模型列表失败: {ex.Message}");
        }
    }

    /// <summary>Raised for every console line; the view appends on the UI thread.</summary>
    public event EventHandler<LogEntry>? Logged;

    /// <summary>Raised for each preview frame + OCR overlay; the view renders it.</summary>
    public event EventHandler<PreviewFrame>? FrameReady;

    public ObservableCollection<string> PaddleModelIds { get; } = new();

    public IReadOnlyList<OcrEngineType> OcrEngines { get; } = new[] { OcrEngineType.WinRt, OcrEngineType.Paddle };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoundHwndText))]
    [NotifyPropertyChangedFor(nameof(BoundTitleText))]
    [NotifyPropertyChangedFor(nameof(BoundProcessText))]
    [NotifyPropertyChangedFor(nameof(BoundSizeText))]
    [NotifyPropertyChangedFor(nameof(BoundDpiText))]
    [NotifyPropertyChangedFor(nameof(BoundMonitorText))]
    [NotifyPropertyChangedFor(nameof(HasBoundWindow))]
    private WindowInfo? boundWindow;

    public bool HasBoundWindow => BoundWindow is not null;
    public string BoundHwndText => BoundWindow?.HexHwnd ?? "—";
    public string BoundTitleText => string.IsNullOrEmpty(BoundWindow?.Title) ? "—" : BoundWindow!.Title;
    public string BoundProcessText => string.IsNullOrEmpty(BoundWindow?.ProcessName) ? "—" : BoundWindow!.ProcessName;
    public string BoundSizeText => BoundWindow is null ? "—" : $"{BoundWindow.Width} × {BoundWindow.Height}";
    public string BoundDpiText => BoundWindow is null ? "—" : $"{BoundWindow.DpiPercent}%";
    public string BoundMonitorText => BoundWindow?.MonitorDescription ?? "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewToggleText))]
    private bool isPreviewRunning;

    [ObservableProperty]
    private bool isOcrOverlayEnabled = true;

    [ObservableProperty]
    private bool useBackgroundInput = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaddleSelected))]
    private OcrEngineType selectedOcrEngine = OcrEngineType.WinRt;

    [ObservableProperty]
    private string? selectedPaddleModelId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool isBusy;

    public bool IsIdle => !IsBusy;

    public bool IsPaddleSelected => SelectedOcrEngine == OcrEngineType.Paddle;

    public string PreviewToggleText => IsPreviewRunning ? "暂停预览" : "开启实时预览";

    /// <summary>Called by the view once it has subscribed to <see cref="Logged"/>.</summary>
    public void OnViewReady()
    {
        Log(LogLevel.Info, "Init", "Dev Tools 沙箱就绪。请把右上角准星拖到目标窗口完成绑定。");
        Log(LogLevel.Info, "Init", PaddleModelIds.Count > 0
            ? $"发现 Paddle 模型: [{string.Join(", ", PaddleModelIds)}]"
            : "未发现 Paddle 模型（Paddle 引擎将不可用）。");
    }

    // --- Window binding (Spy++-style crosshair) -------------------------------

    /// <summary>Live highlight helper the view calls during the crosshair drag.</summary>
    public WindowInfo? SpyCurrentWindow() => _windowSpy.ResolveAtCurrentCursor();

    [RelayCommand]
    private async Task BindCurrentWindowAsync()
    {
        WindowInfo? info = _windowSpy.ResolveAtCurrentCursor();
        if (info is null || info.Hwnd == 0)
        {
            Log(LogLevel.Warn, "Spy", "未在光标下找到窗口，绑定取消。");
            return;
        }

        BoundWindow = info;
        Log(LogLevel.Success, "Spy",
            $"已绑定 {info.HexHwnd} \"{info.Title}\" 进程={info.ProcessName} " +
            $"尺寸={info.Width}x{info.Height} DPI={info.DpiPercent}%");

        await ApplyInputTargetAsync();

        // Keep a running preview pointed at the newly bound window.
        if (IsPreviewRunning)
        {
            await RestartCaptureAsync(info.Hwnd);
        }
    }

    // --- Coordinate picker (client-space point for scripting) -----------------

    [ObservableProperty]
    private string pickedClientText = "—";

    private PointInt? _lastPickedPoint;

    /// <summary>Live client-space coord under the cursor, for the picker drag readout.</summary>
    public string SpyClientPointText()
    {
        if (BoundWindow is null)
        {
            return "未绑定窗口";
        }
        PointInt? p = _windowSpy.ClientPointUnderCursor(BoundWindow.Hwnd);
        return p is null ? "无法取点" : $"({p.Value.X}, {p.Value.Y})";
    }

    /// <summary>Commit the client-space point currently under the cursor (called on drag release).</summary>
    public void PickClientPoint()
    {
        if (BoundWindow is null)
        {
            Log(LogLevel.Warn, "Picker", "请先绑定窗口再取坐标。");
            return;
        }

        PointInt? p = _windowSpy.ClientPointUnderCursor(BoundWindow.Hwnd);
        if (p is null)
        {
            Log(LogLevel.Warn, "Picker", "取点失败。");
            return;
        }

        _lastPickedPoint = p;
        PickedClientText = $"({p.Value.X}, {p.Value.Y})";

        // Also give the logical DIP equivalent — WinUI/XAML-side scripts reason in DIPs,
        // while the input API consumes the physical client coordinate.
        double dpi = BoundWindow.DpiScale * 96.0;
        PointInt logical = DpiHelper.PhysicalToLogical(p.Value, dpi);

        Log(LogLevel.Success, "Picker",
            $"客户区物理坐标 {PickedClientText} · 逻辑(DIP) ({logical.X}, {logical.Y}) " +
            $"@ {BoundWindow.DpiPercent}% [{BoundWindow.MonitorName}]（客户区 {ClientWidth}x{ClientHeight}）");
    }

    /// <summary>The last picked point as "x,y" for clipboard/script use, or empty.</summary>
    public string PickedPointRaw => _lastPickedPoint is { } p ? $"{p.X},{p.Y}" : string.Empty;

    // --- Live preview ---------------------------------------------------------

    [RelayCommand]
    private async Task TogglePreviewAsync()
    {
        if (IsPreviewRunning)
        {
            await StopPreviewAsync();
        }
        else
        {
            await StartPreviewAsync();
        }
    }

    public async Task StartPreviewAsync()
    {
        if (IsPreviewRunning)
        {
            return;
        }
        if (BoundWindow is null)
        {
            Log(LogLevel.Warn, "Capture", "请先用准星绑定一个窗口再开启预览。");
            return;
        }

        try
        {
            await _capture.StartAsync(BoundWindow.Hwnd);
            _previewCts = new CancellationTokenSource();
            IsPreviewRunning = true;
            Log(LogLevel.Success, "Capture", $"开始实时预览 {BoundWindow.HexHwnd}（OCR 叠加 {(IsOcrOverlayEnabled ? "开" : "关")}）");
            _ = Task.Run(() => RunPreviewLoopAsync(_previewCts.Token));
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, "Capture", $"启动预览失败: {ex.Message}");
        }
    }

    public async Task StopPreviewAsync()
    {
        if (_previewCts is not null)
        {
            _previewCts.Cancel();
            _previewCts.Dispose();
            _previewCts = null;
        }
        if (IsPreviewRunning)
        {
            IsPreviewRunning = false;
            await _capture.StopAsync();
            Log(LogLevel.Info, "Capture", "已暂停实时预览。");
        }
    }

    private async Task RestartCaptureAsync(nint hwnd)
    {
        try
        {
            await _capture.StartAsync(hwnd);
            Log(LogLevel.Info, "Capture", $"预览已切换到 {hwnd:X}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, "Capture", $"切换捕获窗口失败: {ex.Message}");
        }
    }

    private async Task RunPreviewLoopAsync(CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        long lastOcrMs = -10_000;
        IReadOnlyList<OcrTextBlock> cached = Array.Empty<OcrTextBlock>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                CapturedFrame? frame = await _capture.GrabFrameAsync(ct);
                if (frame is not null)
                {
                    if (!IsOcrOverlayEnabled)
                    {
                        cached = Array.Empty<OcrTextBlock>();
                    }
                    else if (sw.ElapsedMilliseconds - lastOcrMs >= 300)  // throttle OCR to ~3 Hz
                    {
                        lastOcrMs = sw.ElapsedMilliseconds;
                        try
                        {
                            cached = await _ocr.RecognizeFrameAsync(frame, SelectedOcrEngine, SelectedPaddleModelId, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            cached = Array.Empty<OcrTextBlock>();
                            Log(LogLevel.Warn, "OCR", $"实时识别失败: {ex.Message}");
                        }
                    }

                    FrameReady?.Invoke(this, new PreviewFrame(frame, cached));
                }

                await Task.Delay(33, ct);  // ~30 fps cap
            }
        }
        catch (OperationCanceledException) { /* normal stop */ }
        catch (Exception ex)
        {
            Log(LogLevel.Error, "Capture", $"预览循环异常: {ex.Message}");
        }
    }

    // --- Input self-tests -----------------------------------------------------

    [RelayCommand]
    private async Task TestMouseAsync()
    {
        if (IsBusy || !EnsureBoundForInput())
        {
            return;
        }

        IsBusy = true;
        try
        {
            await PrepareInputTargetAsync();
            Log(LogLevel.Info, "Input", $"开始鼠标全功能测试（客户区 {ClientWidth}x{ClientHeight} 内：左/右/中/侧键X1/X2 + 贝塞尔拖拽）...");

            (string Name, MouseButton Button)[] clicks =
            {
                ("左键", MouseButton.Left),
                ("右键", MouseButton.Right),
                ("中键", MouseButton.Middle),
                ("侧键1 (X1)", MouseButton.XButton1),
                ("侧键2 (X2)", MouseButton.XButton2),
            };

            int w = ClientWidth, h = ClientHeight;
            for (int i = 0; i < clicks.Length; i++)
            {
                // Spread the clicks across the client area so each lands inside the window.
                int cx = (int)(w * (0.30 + 0.10 * i));
                int cy = (int)(h * 0.45);
                Log(LogLevel.Info, "Input", $"移动到客户区 ({cx},{cy}) 并点击 {clicks[i].Name} ...");
                await _input.MoveMouseAsync(cx, cy, TrajectoryType.CubicBezier, true);
                await _input.ClickAsync(clicks[i].Button, 0);
                await Task.Delay(150);
            }

            GetDragRegion(out int sx, out int sy, out int ex, out int ey);
            Log(LogLevel.Info, "Input", $"贝塞尔缓动拖拽 ({sx},{sy}) → ({ex},{ey}) ...");
            await _input.DragAndDropAsync(sx, sy, ex, ey, TrajectoryType.CubicBezier, true);

            Log(LogLevel.Success, "Input", "鼠标全功能测试完成（全程限制在绑定窗口客户区内）。");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, "Input", $"鼠标测试失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestKeyboardAsync()
    {
        if (IsBusy || !EnsureBoundForInput())
        {
            return;
        }

        IsBusy = true;
        try
        {
            await PrepareInputTargetAsync();
            List<(string Name, int Vk)> keys = BuildKeySequence();
            Log(LogLevel.Info, "Input", $"开始键盘覆盖测试（{keys.Count} 个按键，已严格屏蔽 Win/Fn）...");

            int sent = 0;
            foreach ((string name, int vk) in keys)
            {
                if (vk == VK_LWIN || vk == VK_RWIN)
                {
                    Log(LogLevel.Error, "Input", $"检测到 Win 键 (VK=0x{vk:X2})，已跳过。");
                    continue;
                }
                await _input.KeyPressAsync(vk, 0);
                await Task.Delay(40);
                if (++sent % 20 == 0)
                {
                    Log(LogLevel.Info, "Input", $"已发送 {sent}/{keys.Count} 个按键...");
                }
            }

            Log(LogLevel.Success, "Input", $"键盘覆盖测试完成，共发送 {sent} 个按键。");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, "Input", $"键盘测试失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StressTestAsync()
    {
        if (IsBusy || !EnsureBoundForInput())
        {
            return;
        }

        IsBusy = true;
        try
        {
            await PrepareInputTargetAsync();
            if (!_capture.IsCapturing)
            {
                await _capture.StartAsync(BoundWindow!.Hwnd);
            }

            Log(LogLevel.Info, "Stress", "开始 100 次全链路压测（WGC 截图 → OCR → 贝塞尔点击）...");
            long memBefore = GC.GetTotalMemory(false);
            int handlesBefore = Process.GetCurrentProcess().HandleCount;

            OcrEngineType engine = SelectedOcrEngine;
            string? model = SelectedPaddleModelId;

            for (int i = 1; i <= 100; i++)
            {
                CapturedFrame? frame = await _capture.GrabFrameAsync();
                if (frame is not null)
                {
                    try
                    {
                        IReadOnlyList<OcrTextBlock> boxes = await _ocr.RecognizeFrameAsync(frame, engine, model);

                        int cx = frame.Width / 2, cy = frame.Height / 2;
                        if (boxes.Count > 0)
                        {
                            Rect b = boxes[0].BoundingBox;
                            cx = b.X + b.Width / 2;
                            cy = b.Y + b.Height / 2;
                        }
                        await _input.MoveMouseAsync(cx, cy, TrajectoryType.CubicBezier, true);
                        await _input.ClickAsync(MouseButton.Left, 0);
                    }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Warn, "Stress", $"第 {i} 次迭代异常: {ex.Message}");
                    }
                }

                if (i % 10 == 0)
                {
                    Log(LogLevel.Info, "Stress", $"进度 {i}/100");
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long memAfter = GC.GetTotalMemory(false);
            int handlesAfter = Process.GetCurrentProcess().HandleCount;
            Log(LogLevel.Success, "Stress",
                $"压测完成：托管内存 {memBefore / 1024}KB → {memAfter / 1024}KB " +
                $"(Δ{(memAfter - memBefore) / 1024}KB)，句柄 {handlesBefore} → {handlesAfter} " +
                $"(Δ{handlesAfter - handlesBefore})。");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, "Stress", $"压测失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- Input-mode toggle ----------------------------------------------------

    partial void OnUseBackgroundInputChanged(bool value)
    {
        Log(LogLevel.Info, "Input", value
            ? "投递方式：后台 PostMessage（发往绑定 HWND，不抢焦点、不动真实光标）。"
            : "投递方式：前台 SendInput（移动真实光标，测试前会激活目标窗口）。");
        _ = ApplyInputTargetAsync();
    }

    // --- Helpers --------------------------------------------------------------

    private bool EnsureBoundForInput()
    {
        if (BoundWindow is null)
        {
            Log(LogLevel.Warn, "Input", "请先用准星绑定一个目标窗口再运行输入测试。");
            return false;
        }
        return true;
    }

    /// <summary>Point the native controller at the bound window per the current mode.</summary>
    private async Task ApplyInputTargetAsync()
    {
        if (BoundWindow is null)
        {
            return;
        }
        try
        {
            // The window stays bound in BOTH modes so native maps client->screen and
            // confines the cursor to the client rect; the flag only picks the delivery.
            await _input.SetTargetAsync(BoundWindow.Hwnd, UseBackgroundInput);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, "Input", $"设置输入目标窗口失败: {ex.Message}");
        }
    }

    /// <summary>Before an input test: bind target + delivery, activating the window for foreground.</summary>
    private async Task PrepareInputTargetAsync()
    {
        if (BoundWindow is null)
        {
            return;
        }

        await _input.SetTargetAsync(BoundWindow.Hwnd, UseBackgroundInput);

        if (UseBackgroundInput)
        {
            return;
        }

        bool ok = _windowSpy.BringToFront(BoundWindow.Hwnd);
        Log(ok ? LogLevel.Info : LogLevel.Warn, "Input", ok
            ? $"已激活前台窗口 {BoundWindow.HexHwnd}，SendInput 将发往该窗口（客户区内）。"
            : "前台激活被系统拒绝，SendInput 将发往当前焦点窗口。");
        await Task.Delay(150);
    }

    private void GetDragRegion(out int sx, out int sy, out int ex, out int ey)
    {
        int w = ClientWidth;
        int h = ClientHeight;
        sx = (int)(w * 0.30); sy = (int)(h * 0.35);
        ex = (int)(w * 0.65); ey = (int)(h * 0.60);
    }

    // Client-area size of the bound window (falls back to the window rect, then a
    // default). All test coordinates are client-space; native clamps them anyway.
    private int ClientWidth => BoundWindow is { ClientWidth: > 0 } w ? w.ClientWidth : (BoundWindow?.Width ?? 800);
    private int ClientHeight => BoundWindow is { ClientHeight: > 0 } h ? h.ClientHeight : (BoundWindow?.Height ?? 600);

    private static List<(string Name, int Vk)> BuildKeySequence()
    {
        var keys = new List<(string, int)>();

        for (int c = 0x41; c <= 0x5A; c++) keys.Add((((char)c).ToString(), c));  // A-Z
        for (int d = 0x30; d <= 0x39; d++) keys.Add((((char)d).ToString(), d));  // 0-9

        keys.Add(("Left", 0x25));
        keys.Add(("Up", 0x26));
        keys.Add(("Right", 0x27));
        keys.Add(("Down", 0x28));

        keys.Add(("Shift", 0x10));
        keys.Add(("Ctrl", 0x11));
        keys.Add(("Alt", 0x12));

        for (int f = 0x70; f <= 0x7B; f++) keys.Add(("F" + (f - 0x6F), f));    // F1-F12

        // Deliberately NO Win keys (0x5B/0x5C) and NO Fn (not a virtual key).
        return keys;
    }

    private void Log(LogLevel level, string tag, string message)
        => Logged?.Invoke(this, new LogEntry(DateTime.Now, level, tag, message));
}
