namespace Rug.UI.Core.Models;

/// <summary>
/// Inspection result for a window picked by the crosshair (Spy++-style). All sizes
/// and offsets are PHYSICAL pixels (the host process is Per-Monitor V2 DPI-aware).
/// </summary>
public sealed record WindowInfo(
    nint Hwnd,
    string Title,
    string ProcessName,
    int Width,
    int Height,
    double DpiScale,
    int ClientWidth,
    int ClientHeight,
    string MonitorName,
    int MonitorX,
    int MonitorY,
    int MonitorWidth,
    int MonitorHeight)
{
    /// <summary>HWND as a hex string, e.g. "0x000A0B0C".</summary>
    public string HexHwnd => $"0x{Hwnd:X}";

    /// <summary>DPI scale as a percentage, e.g. 100 / 125 / 150.</summary>
    public int DpiPercent => (int)Math.Round(DpiScale * 100.0);

    /// <summary>Human-readable monitor description, e.g. "DISPLAY2 (-1920, 0) 1920x1080 @ 100%".</summary>
    public string MonitorDescription => string.IsNullOrEmpty(MonitorName)
        ? $"({MonitorX}, {MonitorY}) {MonitorWidth}x{MonitorHeight} @ {DpiPercent}%"
        : $"{MonitorName} ({MonitorX}, {MonitorY}) {MonitorWidth}x{MonitorHeight} @ {DpiPercent}%";
}
