namespace Rug.UI.Core.Models;

/// <summary>Input controller back-end (mirrors the native RugInputMode).</summary>
public enum InputMode
{
    Win32Software = 0,
    HardwareKmbox = 1,
}

/// <summary>Mouse button (mirrors the native RugMouseButton).</summary>
public enum MouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
    XButton1 = 3,
    XButton2 = 4,
}

/// <summary>Mouse movement path shape (mirrors the native RugTrajectoryType).</summary>
public enum TrajectoryType
{
    Straight = 0,
    CubicBezier = 1,
}

/// <summary>Humanized input tuning knobs (mirrors the native RugHumanizeConfig).</summary>
public record HumanizeConfig
{
    public int MinClickHoldMs { get; init; } = 80;
    public int MaxClickHoldMs { get; init; } = 120;
    public int MinKeyHoldMs { get; init; } = 60;
    public int MaxKeyHoldMs { get; init; } = 100;
    public float CorridorRatio { get; init; } = 0.15f;
    public bool EnableJitter { get; init; } = true;
}

/// <summary>One planned trajectory step: an absolute point plus the delay after it.</summary>
public readonly record struct TrajectorySample(int X, int Y, float DelayMs);
