using Rug.UI.Core.Models;

namespace Rug.UI.Core.Helpers;

/// <summary>
/// Per-Monitor DPI pixel conversions. The whole capture/input pipeline works in
/// PHYSICAL pixels (the host process is Per-Monitor V2 DPI-aware); these helpers
/// convert to/from WinUI logical DIPs (96-DPI baseline) for scripting and any
/// XAML-facing coordinate a plugin author might reason about.
/// </summary>
public static class DpiHelper
{
    public const double BaselineDpi = 96.0;

    /// <summary>Scale factor for a raw DPI value (96 -> 1.0, 144 -> 1.5).</summary>
    public static double ScaleAt(double dpi) => dpi > 0 ? dpi / BaselineDpi : 1.0;

    /// <summary>Physical pixels -> logical DIPs at the given DPI.</summary>
    public static PointInt PhysicalToLogical(PointInt physical, double dpi)
    {
        double s = ScaleAt(dpi);
        return new PointInt((int)Math.Round(physical.X / s), (int)Math.Round(physical.Y / s));
    }

    /// <summary>Logical DIPs -> physical pixels at the given DPI.</summary>
    public static PointInt LogicalToPhysical(PointInt logical, double dpi)
    {
        double s = ScaleAt(dpi);
        return new PointInt((int)Math.Round(logical.X * s), (int)Math.Round(logical.Y * s));
    }
}
