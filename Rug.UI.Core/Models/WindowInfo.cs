namespace Rug.UI.Core.Models;

/// <summary>
/// Inspection result for a window picked by the crosshair (Spy++-style).
/// </summary>
public sealed record WindowInfo(
    nint Hwnd,
    string Title,
    string ProcessName,
    int Width,
    int Height,
    double DpiScale,
    int ClientWidth,
    int ClientHeight)
{
    /// <summary>HWND as a hex string, e.g. "0x000A0B0C".</summary>
    public string HexHwnd => $"0x{Hwnd:X}";

    /// <summary>DPI scale as a percentage, e.g. 100 / 125 / 150.</summary>
    public int DpiPercent => (int)Math.Round(DpiScale * 100.0);
}
