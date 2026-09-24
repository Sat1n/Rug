namespace Rug.UI.Core.Models;

/// <summary>Exact manifest permission names. No wildcard or prefix matching.</summary>
public static class Permission
{
    public const string Timer = "timer";
    public const string VisionCapture = "vision.capture";
    public const string VisionOcr = "vision.ocr";
    public const string ControlInput = "input";
    public const string Network = "network";
    public const string FileSystem = "filesystem";
    public const string Agent = "agent";
}
