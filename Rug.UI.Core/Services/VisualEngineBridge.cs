#nullable enable
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Security;

namespace Rug.UI.Core.Services;

/// <summary>
/// Owner-scoped single-frame cache and recognition leases in a shared bounded host pool.
/// Public values contain metadata only; trusted service pixels never escape to Lua/Gateway.
/// </summary>
public sealed class VisualEngineBridge : IDisposable
{
    private readonly ICaptureService _capture;
    private readonly IOcrService _ocr;
    private readonly ITemplateMatchService _matcher;
    private readonly PermissionInterceptor _permissions;
    private readonly string? _pluginDirectory;
    private readonly object _frameGate = new();
    private readonly ObservationFramePool _frames;
    private readonly ObservationFramePool.Session _session;
    private readonly TaskExecutionContext? _context;
    private readonly Action? _validateRun;
    private volatile bool _disposed;

    public VisualEngineBridge(ICaptureService capture, IOcrService ocr,
        ITemplateMatchService matcher, PermissionInterceptor permissions,
        RunIdentity identity, string? pluginDirectory = null, ObservationFramePool? frames = null,
        TaskExecutionContext? context = null, Action? validateRun = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        if (context is not null && context.Identity != identity) throw new ArgumentException("Observation lifecycle/identity mismatch.", nameof(identity));
        _frames = frames ?? ObservationFramePool.Shared;
        _context = context; _validateRun = validateRun;
        _pluginDirectory = string.IsNullOrWhiteSpace(pluginDirectory)
            ? null : Path.GetFullPath(pluginDirectory);
        _session = _frames.Open(identity);
    }

    /// <summary>@shape return: metadata/null only; producer/copy bytes reserved globally before capture.
    /// @source pool: Services/ObservationFramePool.cs#class:ObservationFramePool</summary>
    public async Task<CapturedFrameMetadata?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        _permissions.Demand(Permission.VisionCapture, "rug.capture");
        ThrowIfDisposed();
        _validateRun?.Invoke();
        using var work = _context?.EnterOperation(false);
        CheckCancellation(cancellationToken);
        using var operation = _frames.Capture(_session);
        long requested = _frames.Timestamp; var requestedUtc = _frames.UtcNow;
        CapturedFrame? frame = await (_capture is IBoundedFrameCapture bounded
            ? bounded.GrabFrameBoundedAsync(_frames.Limits.MaxFrameBytes, cancellationToken)
            : _capture.GrabFrameAsync(cancellationToken)).ConfigureAwait(false);
        long received = _frames.Timestamp; var receivedUtc = _frames.UtcNow;
        CheckCancellation(cancellationToken);
        var owned = frame is null ? null : operation.Detach(frame);
        _permissions.Demand(Permission.VisionCapture, "rug.capture.publish");
        ObservationEnvelope? observation = null;
        lock (_frameGate)
        {
            ThrowIfDisposed();
            CheckCancellation(cancellationToken);
            _validateRun?.Invoke();
            if (_context is null) Publish(); else _context.PublishObservation(Publish, cancellationToken);
            void Publish() => observation = operation.Publish(owned, requestedUtc, requested, receivedUtc, received, cancellationToken);
        }
        return observation is null ? null : new(observation.Image.Width, observation.Image.Height, observation.Image.Stride, observation);
    }

    /// <summary>@shape region: source-image ROI; hit: legacy client coordinates plus actual held-frame provenance.
    /// @source source: Services/ObservationFramePool.cs#class:ObservationFramePool</summary>
    public async Task<ImageFindResult?> FindImageAsync(string templatePath, float threshold = 0.8f,
        Rect? region = null, CancellationToken cancellationToken = default, string? frameReference = null)
    {
        _permissions.Demand(Permission.VisionMatch, "rug.find_image");
        ThrowIfDisposed();
        _validateRun?.Invoke();
        using var work = _context?.EnterOperation(false);
        if (!float.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 0 and 1.");
        string path = ResolveTemplatePath(templatePath);
        using var lease = _frames.Read(_session, frameReference, region);
        CapturedFrame source = lease.Frame;
        CapturedFrame target = Crop(source, region);
        long started = _frames.Timestamp;
        CheckCancellation(cancellationToken); _validateRun?.Invoke();
        IReadOnlyList<TemplateMatchResult> hits;
        try
        {
            hits = await _matcher.MatchFrameAsync(target, path, threshold, cancellationToken).ConfigureAwait(false);
        }
        catch (RugNativeException ex) when (ex.Status == RugStatus.ErrInvalidParam)
        {
            throw new InvalidDataException($"Template image is unreadable or invalid: {path}", ex);
        }
        CheckCancellation(cancellationToken); _validateRun?.Invoke(); ThrowIfDisposed();
        TemplateMatchResult? best = hits.Where(hit => double.IsFinite(hit.Score) && hit.Score >= threshold)
            .OrderByDescending(hit => hit.Score).FirstOrDefault();
        if (best is null) return null;
        return new ImageFindResult(best.X + (region?.X ?? 0), best.Y + (region?.Y ?? 0), best.Score,
            Recognized(lease.Observation, target, region, "template-match", started));
    }

    /// <summary>@shape blocks: ROI-local native boxes translated once, with the leased source envelope.
    /// @source source: Services/ObservationFramePool.cs#class:ObservationFramePool</summary>
    public async Task<VisionOcrResult> OcrAsync(Rect? region = null, string? language = null,
        OcrEngineType engine = OcrEngineType.WinRt, CancellationToken cancellationToken = default, string? frameReference = null)
    {
        _permissions.Demand(Permission.VisionOcr, "rug.ocr");
        ThrowIfDisposed();
        _validateRun?.Invoke();
        using var work = _context?.EnterOperation(false);
        using var lease = _frames.Read(_session, frameReference, region);
        CapturedFrame source = lease.Frame;
        CapturedFrame target = Crop(source, region);
        long started = _frames.Timestamp;
        CheckCancellation(cancellationToken); _validateRun?.Invoke();
        IReadOnlyList<OcrTextBlock> local = await _ocr.RecognizeFrameAsync(
            target, engine, language, cancellationToken).ConfigureAwait(false);
        CheckCancellation(cancellationToken); _validateRun?.Invoke(); ThrowIfDisposed();
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
        return new VisionOcrResult(string.Join("\n", absolute.Select(block => block.Text)), Array.AsReadOnly(absolute.ToArray()),
            Recognized(lease.Observation, target, region, "ocr", started));
    }
    private ObservationEnvelope Recognized(ObservationEnvelope source, CapturedFrame input, Rect? region, string kind, long started)
        => source with { ObservationId = Guid.NewGuid(), Processing = new(kind,
            region ?? new Rect(0, 0, source.Image.Width, source.Image.Height), input.Width, input.Height, input.Stride,
            region?.X ?? 0, region?.Y ?? 0, started, _frames.Timestamp) };

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

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VisualEngineBridge));
    }
    private static void CheckCancellation(CancellationToken token)
    { token.ThrowIfCancellationRequested(); ObservationCallBoundary.Current.Value?.Check(); }

    public void Dispose()
    {
        lock (_frameGate)
        {
            _disposed = true;
            _session.Dispose();
        }
    }
}
