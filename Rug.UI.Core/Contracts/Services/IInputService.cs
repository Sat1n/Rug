#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Contracts.Services;

/// <summary>
/// Humanized mouse/keyboard synthesis over the Rug.Core input C-ABI. Each method
/// runs on a background (MTA) thread; the service owns the native controller for
/// its lifetime and releases it on <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </summary>
public interface IInputService : IAsyncDisposable
{
    /// <summary>Absolute move to (x, y) along a humanized trajectory.</summary>
    Task MoveMouseAsync(int x, int y,
        TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true,
        CancellationToken cancellationToken = default);

    /// <summary>Relative move by (dx, dy) from the current position.</summary>
    Task MoveMouseRelativeAsync(int dx, int dy,
        TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true,
        CancellationToken cancellationToken = default);

    /// <summary>Press and release <paramref name="button"/>. holdMs 0 -> random.</summary>
    Task ClickAsync(MouseButton button = MouseButton.Left, int holdMs = 0,
        CancellationToken cancellationToken = default);

    Task MouseDownAsync(MouseButton button, CancellationToken cancellationToken = default);
    Task MouseUpAsync(MouseButton button, CancellationToken cancellationToken = default);

    /// <summary>Humanized drag from (sx, sy) to (ex, ey) with the left button.</summary>
    Task DragAndDropAsync(int sx, int sy, int ex, int ey,
        TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true,
        CancellationToken cancellationToken = default);

    Task KeyDownAsync(int virtualKey, CancellationToken cancellationToken = default);
    Task KeyUpAsync(int virtualKey, CancellationToken cancellationToken = default);

    /// <summary>Press and release a virtual key. holdMs 0 -> random.</summary>
    Task KeyPressAsync(int virtualKey, int holdMs = 0, CancellationToken cancellationToken = default);

    /// <summary>Send a UTF-8 string as a sequence of Unicode characters.</summary>
    Task SendTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Re-bind the background target window (IntPtr HWND); null -> foreground.</summary>
    Task SetTargetWindowAsync(nint hwnd, CancellationToken cancellationToken = default);

    /// <summary>Replace the humanization tuning.</summary>
    Task SetHumanizeConfigAsync(HumanizeConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dry-run the humanizer without emitting OS input: returns the planned samples
    /// (absolute points + per-sample delay in ms). Used to inspect the dynamic cadence.
    /// </summary>
    Task<IReadOnlyList<TrajectorySample>> PlanTrajectoryAsync(
        int sx, int sy, int ex, int ey,
        TrajectoryType trajectory = TrajectoryType.CubicBezier,
        CancellationToken cancellationToken = default);
}
