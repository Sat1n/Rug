using Rug.UI.Core.Models;

namespace Rug.UI.Models;

/// <summary>Severity of a dev-sandbox log line; drives its console color.</summary>
public enum LogLevel
{
    Info,
    Success,
    Warn,
    Error,
}

/// <summary>
/// One line in the simulated console log. A mutable class (not an init-only record)
/// because the WinUI XAML compiler emits settable-property type info for any type
/// reached through an <c>{x:Bind}</c> collection, which init-only accessors reject.
/// </summary>
public sealed class LogEntry
{
    public LogEntry()
    {
    }

    public LogEntry(DateTime time, LogLevel level, string tag, string message)
    {
        Time = time;
        Level = level;
        Tag = tag;
        Message = message;
    }

    public DateTime Time { get; set; }
    public LogLevel Level { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>Formatted as "[HH:mm:ss.fff] [LEVEL] [Tag] message".</summary>
    public string Line =>
        $"[{Time:HH:mm:ss.fff}] [{Level.ToString().ToUpperInvariant()}] [{Tag}] {Message}";
}

/// <summary>A captured frame plus any OCR boxes to overlay, pushed to the view.</summary>
public sealed record PreviewFrame(CapturedFrame Frame, IReadOnlyList<OcrTextBlock> Boxes);
