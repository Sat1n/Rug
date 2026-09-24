using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

using Rug.UI.Core.Models;
using Rug.UI.Models;
using Rug.UI.ViewModels;

namespace Rug.UI.Views;

/// <summary>
/// Dev Tools sandbox page. Renders the WGC live preview into a WriteableBitmap with an
/// OCR box overlay, drives the Spy++-style crosshair window picker, and mirrors the
/// view-model's log/frame events onto the UI thread. All native work stays on the
/// view-model's background threads; this code-behind only touches UI objects.
/// </summary>
public sealed partial class DevToolsPage : Page
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<LogEntry> _logs = new();
    private WriteableBitmap? _bitmap;
    private bool _picking;
    private bool _coordPicking;
    private bool _welcomed;

    public DevToolsViewModel ViewModel
    {
        get;
    }

    /// <summary>Log lines bound to the console ItemsControl (UI-thread only).</summary>
    public ObservableCollection<LogEntry> Logs => _logs;

    public DevToolsPage()
    {
        ViewModel = App.GetService<DevToolsViewModel>();
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Logged -= OnLogged;       // idempotent subscribe
        ViewModel.FrameReady -= OnFrameReady;
        ViewModel.Logged += OnLogged;
        ViewModel.FrameReady += OnFrameReady;

        if (!_welcomed)
        {
            _welcomed = true;
            ViewModel.OnViewReady();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Logged -= OnLogged;
        ViewModel.FrameReady -= OnFrameReady;
        _ = ViewModel.StopPreviewAsync();
    }

    // --- Crosshair window picker ----------------------------------------------

    private void Crosshair_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _picking = true;
        CrosshairButton.CapturePointer(e.Pointer);
        HoverText.Text = "拖动中……移到目标窗口后松开即绑定";
        e.Handled = true;
    }

    private void Crosshair_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_picking)
        {
            return;
        }

        WindowInfo? info = ViewModel.SpyCurrentWindow();
        HoverText.Text = info is null
            ? "悬停: 未检测到窗口"
            : $"悬停: {info.HexHwnd} \"{Truncate(info.Title, 28)}\" ({info.ProcessName}) {info.DpiPercent}%";
        e.Handled = true;
    }

    private void Crosshair_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_picking)
        {
            return;
        }

        _picking = false;
        CrosshairButton.ReleasePointerCapture(e.Pointer);
        HoverText.Text = "已绑定，见上方窗口信息卡片";
        _ = ViewModel.BindCurrentWindowCommand.ExecuteAsync(null);
        e.Handled = true;
    }

    private void Crosshair_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // While dragging we hold pointer capture, so an exit is spurious — ignore it.
        if (_picking)
        {
            return;
        }
        HoverText.Text = "拖动准星到窗口以实时预览目标";
    }

    // --- Coordinate picker (client-space point for scripting) -----------------

    private void CoordPicker_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _coordPicking = true;
        CoordPicker.CapturePointer(e.Pointer);
        CoordHoverText.Text = "实时: " + ViewModel.SpyClientPointText();
        e.Handled = true;
    }

    private void CoordPicker_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_coordPicking)
        {
            return;
        }
        CoordHoverText.Text = "实时: " + ViewModel.SpyClientPointText();
        e.Handled = true;
    }

    private void CoordPicker_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_coordPicking)
        {
            return;
        }
        _coordPicking = false;
        CoordPicker.ReleasePointerCapture(e.Pointer);
        ViewModel.PickClientPoint();
        CoordHoverText.Text = "实时: —";
        e.Handled = true;
    }

    private void CopyCoord_Click(object sender, RoutedEventArgs e)
    {
        string raw = ViewModel.PickedPointRaw;
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }
        var package = new DataPackage();
        package.SetText(raw);
        Clipboard.SetContent(package);
    }

    // --- Preview rendering ----------------------------------------------------

    private void OnFrameReady(object? sender, PreviewFrame pf)
        => _dispatcher.TryEnqueue(() => RenderFrame(pf));

    private void RenderFrame(PreviewFrame pf)
    {
        CapturedFrame frame = pf.Frame;
        int w = frame.Width, h = frame.Height;
        if (w <= 0 || h <= 0 || frame.Pixels.Length == 0)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h);
            PreviewImage.Source = _bitmap;
            PreviewRoot.Width = w;    // frame-space; the Viewbox scales it to fit
            PreviewRoot.Height = h;
        }

        int rowBytes = w * 4;
        try
        {
            using var stream = _bitmap.PixelBuffer.AsStream();
            for (int y = 0; y < h; y++)
            {
                int src = y * frame.Stride;
                if (src + rowBytes > frame.Pixels.Length)
                {
                    break;
                }
                stream.Write(frame.Pixels, src, rowBytes);
            }
        }
        catch (Exception)
        {
            return;  // a frame arriving mid-resize; skip it
        }
        _bitmap.Invalidate();

        DrawOverlay(pf.Boxes);
    }

    private void DrawOverlay(IReadOnlyList<OcrTextBlock> boxes)
    {
        OverlayCanvas.Children.Clear();
        if (boxes.Count == 0)
        {
            return;
        }

        var stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));
        var fill = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xD7, 0x00));

        foreach (OcrTextBlock box in boxes)
        {
            Rect r = box.BoundingBox;
            var rect = new Rectangle
            {
                Width = Math.Max(1, r.Width),
                Height = Math.Max(1, r.Height),
                Stroke = stroke,
                StrokeThickness = 2,
                Fill = fill,
            };
            Canvas.SetLeft(rect, r.X);
            Canvas.SetTop(rect, r.Y);
            OverlayCanvas.Children.Add(rect);

            if (!string.IsNullOrEmpty(box.Text))
            {
                double fs = Math.Max(10, r.Height * 0.85);
                var label = new TextBlock
                {
                    Text = box.Text,
                    FontSize = fs,
                    Foreground = stroke,
                };
                Canvas.SetLeft(label, r.X);
                Canvas.SetTop(label, Math.Max(0, r.Y - fs));
                OverlayCanvas.Children.Add(label);
            }
        }
    }

    // --- Console log ----------------------------------------------------------

    private void OnLogged(object? sender, LogEntry entry)
        => _dispatcher.TryEnqueue(() =>
        {
            _logs.Add(entry);
            while (_logs.Count > 2000)
            {
                _logs.RemoveAt(0);
            }
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
        });

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _logs.Clear();

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        string text = string.Join(Environment.NewLine, _logs.Select(l => l.Line));
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // --- x:Bind helpers -------------------------------------------------------

    private Visibility ToReverseVisibility(bool running)
        => running ? Visibility.Collapsed : Visibility.Visible;

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
