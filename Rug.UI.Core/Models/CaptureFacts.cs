#nullable enable
namespace Rug.UI.Core.Models;

/// <summary>Compositor render time, NOT user action time. Native sample and frequency are in the
/// same Windows boot QPC domain. Never subtract a TimeProvider timestamp or interpret this as UTC.</summary>
public sealed record NativeFrameTime(long SystemRelative100ns, long QpcFrequency, long QpcSample,
    string ClockDomain = "Windows boot QPC", string Meaning = "compositor frame render")
{
    public TimeSpan? AgeAtNativeSample => AgeAt(QpcSample, QpcFrequency, ClockDomain);
    // Arbitrary host timestamp conversion is deliberately not a public API.
    internal TimeSpan? AgeAt(long ticks, long frequency, string domain)
    {
        if (domain != ClockDomain || frequency != QpcFrequency || frequency <= 0 || ticks <= 0 || SystemRelative100ns <= 0) return null;
        // Integer rational conversion preserves precision and avoids long multiplication overflow.
        Int128 elapsed = (Int128)ticks * TimeSpan.TicksPerSecond / frequency - SystemRelative100ns;
        return elapsed >= 0 && elapsed <= long.MaxValue ? TimeSpan.FromTicks((long)elapsed) : null;
    }
}
/// <summary>Opaque native window-lifetime tokens, not HWNDs. Distinct from host targetBindingId.</summary>
public sealed record CaptureWindowFacts(string? BoundLifetime, string? CapturedLifetime, bool SameWindow);
public sealed record CaptureFacts(NativeFrameTime? NativeTime, CaptureWindowFacts Windows,
    int ContentWidth, int ContentHeight, int BufferWidth, int BufferHeight, uint GeometryStatus,
    long GeometryBeforeQpc, long GeometryAfterQpc, int Dpi, ObservationGeometry Geometry,
    long QpcFrequency = 0, string GeometryClockDomain = "Windows boot QPC");

/// <summary>Half-open continuous pixel bounds. Coordinates refer to pixel centers; padding and border
/// pixels are rejected. Nearest integer uses AwayFromZero, then validates again; never clamps.</summary>
public sealed record PixelRegion(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => double.IsFinite(x) && double.IsFinite(y) &&
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) &&
        double.IsFinite(X + Width) && double.IsFinite(Y + Height) &&
        Width > 0 && Height > 0 && x >= X && y >= Y && x < X + Width && y < Y + Height;
}
public sealed record PixelTransform(string SourceSpace, string TargetSpace, PixelRegion ValidSource,
    PixelRegion ValidTarget, double ScaleX, double ScaleY, double OffsetX, double OffsetY)
{
    public bool TryMap(double x, double y, out double targetX, out double targetY)
    {
        targetX = targetY = double.NaN;
        if (!ValidSource.Contains(x, y) || !double.IsFinite(ScaleX) || !double.IsFinite(ScaleY) ||
            ScaleX <= 0 || ScaleY <= 0 || !double.IsFinite(OffsetX) || !double.IsFinite(OffsetY)) return false;
        double tx = x * ScaleX + OffsetX, ty = y * ScaleY + OffsetY;
        if (!ValidTarget.Contains(tx, ty)) return false;
        targetX = tx; targetY = ty; return true;
    }
    public bool TryMapPixel(double x, double y, out int targetX, out int targetY)
    {
        targetX = targetY = 0;
        if (!TryMap(x, y, out double tx, out double ty)) return false;
        tx = Math.Round(tx, MidpointRounding.AwayFromZero); ty = Math.Round(ty, MidpointRounding.AwayFromZero);
        if (tx < int.MinValue || tx > int.MaxValue || ty < int.MinValue || ty > int.MaxValue || !ValidTarget.Contains(tx, ty)) return false;
        targetX = (int)tx; targetY = (int)ty; return true;
    }
    public PixelTransform Inverse() => new(TargetSpace, SourceSpace, ValidTarget, ValidSource,
        1 / ScaleX, 1 / ScaleY, -OffsetX / ScaleX, -OffsetY / ScaleY);
}
