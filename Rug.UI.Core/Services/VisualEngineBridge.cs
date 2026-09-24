#nullable enable
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Security;

namespace Rug.UI.Core.Services;

/// <summary>
/// Keeps one managed WGC frame per script instance and passes it to native OCR and
/// matching without transferring pixel ownership. Each new capture replaces the
/// previous frame; region operations use a short-lived managed BGRA crop.
/// </summary>
public sealed class VisualEngineBridge : IDisposable
{
    private readonly ICaptureService _capture;
    private readonly IOcrService _ocr;
    private readonly ITemplateMatchService _matcher;
    private readonly PermissionInterceptor _permissions;
    private readonly string? _pluginDirectory;
    private readonly object _frameGate = new();
    private CapturedFrame? _frame;
    private bool _disposed;

    public VisualEngineBridge(ICaptureService capture, IOcrService ocr,
        ITemplateMatchService matcher, PermissionInterceptor permissions,
        string? pluginDirectory = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _pluginDirectory = string.IsNullOrWhiteSpace(pluginDirectory)
            ? null : Path.GetFullPath(pluginDirectory);
    }

    public async Task<CapturedFrame?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        _permissions.Demand(Permission.VisionCapture, "rug.capture");
        ThrowIfDisposed();
        CapturedFrame? frame = await _capture.GrabFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame is not null) ValidateFrame(frame);
        lock (_frameGate)
        {
            ThrowIfDisposed();
            _frame = frame;
        }
        return frame;
    }

    public async Task<ImageFindResult?> FindImageAsync(string templatePath, float threshold = 0.8f,
        Rect? region = null, CancellationToken cancellationToken = default)
    {
        _permissions.Demand(Permission.VisionMatch, "rug.find_image");
        ThrowIfDisposed();
        if (!float.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 0 and 1.");
        string path = ResolveTemplatePath(templatePath);
        CapturedFrame source = CurrentFrame();
        CapturedFrame target = Crop(source, region);
        IReadOnlyList<TemplateMatchResult> hits;
        try
        {
            hits = await _matcher.MatchFrameAsync(target, path, threshold, cancellationToken).ConfigureAwait(false);
        }
        catch (RugNativeException ex) when (ex.Status == RugStatus.ErrInvalidParam)
        {
            throw new InvalidDataException($"Template image is unreadable or invalid: {path}", ex);
        }
        TemplateMatchResult? best = hits.Where(hit => double.IsFinite(hit.Score) && hit.Score >= threshold)
            .OrderByDescending(hit => hit.Score).FirstOrDefault();
        if (best is null) return null;
        return new ImageFindResult(best.X + (region?.X ?? 0), best.Y + (region?.Y ?? 0), best.Score);
    }

    public async Task<VisionOcrResult> OcrAsync(Rect? region = null, string? language = null,
        OcrEngineType engine = OcrEngineType.WinRt, CancellationToken cancellationToken = default)
    {
        _permissions.Demand(Permission.VisionOcr, "rug.ocr");
        ThrowIfDisposed();
        CapturedFrame source = CurrentFrame();
        CapturedFrame target = Crop(source, region);
        IReadOnlyList<OcrTextBlock> local = await _ocr.RecognizeFrameAsync(
            target, engine, language, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<OcrTextBlock> absolute = region is { } rect
            ? local.Select(block => block with
            {
                BoundingBox = block.BoundingBox with
                {
                    X = block.BoundingBox.X + rect.X,
                    Y = block.BoundingBox.Y + rect.Y
                }
            }).ToArray()
            : local;
        return new VisionOcrResult(string.Join("\n", absolute.Select(block => block.Text)), absolute);
    }

    private CapturedFrame CurrentFrame()
    {
        lock (_frameGate)
            return _frame ?? throw new InvalidOperationException("Call rug.capture before vision recognition.");
    }

    private string ResolveTemplatePath(string templatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
        string full = Path.GetFullPath(_pluginDirectory is null || Path.IsPathFullyQualified(templatePath)
            ? templatePath : Path.Combine(_pluginDirectory, templatePath));
        bool inside = _pluginDirectory is not null &&
            full.StartsWith(_pluginDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        if (!inside) _permissions.Demand(Permission.FileSystem, "rug.find_image.external_template");
        if (!File.Exists(full)) throw new FileNotFoundException("Template image was not found.", full);
        if (inside)
        {
            string? part = full;
            while (part is not null && !part.Equals(_pluginDirectory, StringComparison.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Template path crosses a symbolic link.");
                part = Path.GetDirectoryName(part);
            }
        }
        return full;
    }

    private static CapturedFrame Crop(CapturedFrame frame, Rect? region)
    {
        ValidateFrame(frame);
        if (region is null) return frame;
        Rect rect = region.Value;
        if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 ||
            (long)rect.X + rect.Width > frame.Width || (long)rect.Y + rect.Height > frame.Height)
            throw new ArgumentOutOfRangeException(nameof(region), "Region must fit inside the captured frame.");
        int stride = checked(rect.Width * 4);
        byte[] pixels = new byte[checked(stride * rect.Height)];
        for (int row = 0; row < rect.Height; row++)
            Buffer.BlockCopy(frame.Pixels, (rect.Y + row) * frame.Stride + rect.X * 4,
                pixels, row * stride, stride);
        return new CapturedFrame(pixels, rect.Width, rect.Height, stride);
    }

    private static void ValidateFrame(CapturedFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < checked(frame.Width * 4) ||
            frame.Pixels.Length < checked(frame.Stride * frame.Height))
            throw new InvalidDataException("Captured BGRA frame has invalid dimensions or buffer length.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VisualEngineBridge));
    }

    public void Dispose()
    {
        lock (_frameGate)
        {
            _disposed = true;
            _frame = null;
        }
    }
}
