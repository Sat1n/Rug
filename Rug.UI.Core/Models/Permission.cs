namespace Rug.UI.Core.Models;

/// <summary>Exact manifest permission names. No wildcard or prefix matching.</summary>
public static class Permission
{
    public static bool IsKnown(string permission) => permission is Timer or VisionCapture or VisionMatch or
        VisionOcr or ControlInput or Network or FileSystem or Agent;
    public const string Timer = "timer";
    public const string VisionCapture = "vision.capture";
    public const string Capture = VisionCapture;
    public const string VisionMatch = "vision.match";
    public const string VisionOcr = "vision.ocr";
    public const string ControlInput = "input";
    public const string Network = "network";
    public const string FileSystem = "filesystem";
    public const string Agent = "agent";
}
