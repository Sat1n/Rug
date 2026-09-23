#nullable enable
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;

namespace Rug.UI.Core.Services;

/// <summary>
/// Managed facade over the Rug.Core input C-ABI. Creates one native controller for
/// its lifetime, marshals each call onto a background (MTA) thread via Task.Run, and
/// releases the native handle deterministically through <see cref="InputControllerHandle"/>.
/// </summary>
public sealed class InputService : IInputService
{
    private readonly InputControllerHandle _handle;

    /// <param name="mode">Win32 software (SendInput/PostMessage) or KMBox hardware.</param>
    /// <param name="hwnd">Optional background target window; null uses foreground SendInput.</param>
    public InputService(InputMode mode = InputMode.Win32Software, nint hwnd = 0)
    {
        int rc = RugCoreNative.Rug_CreateInputController((int)mode, hwnd, out nint h);
        if (rc != RugStatus.Ok || h == 0)
            throw new RugNativeException(nameof(RugCoreNative.Rug_CreateInputController), rc);
        _handle = new InputControllerHandle(h);
    }

    private nint H => _handle.DangerousGetHandle();

    public Task MoveMouseAsync(int x, int y,
        TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Check(RugCoreNative.Rug_Input_MouseMove(H, x, y, (int)trajectory, smooth ? 1 : 0),
            nameof(RugCoreNative.Rug_Input_MouseMove)), cancellationToken);

    public Task MoveMouseRelativeAsync(int dx, int dy,
        TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Check(RugCoreNative.Rug_Input_MouseMoveRelative(H, dx, dy, (int)trajectory, smooth ? 1 : 0),
            nameof(RugCoreNative.Rug_Input_MouseMoveRelative)), cancellationToken);

    public Task ClickAsync(MouseButton button = MouseButton.Left, int holdMs = 0,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Check(RugCoreNative.Rug_Input_Click(H, (int)button, holdMs),
            nameof(RugCoreNative.Rug_Input_Click)), cancellationToken);

    public Task MouseDownAsync(MouseButton button, CancellationToken cancellationToken = default)
        => Task.Run(() => Check(RugCoreNative.Rug_Input_MouseDown(H, (int)button),
            nameof(RugCoreNative.Rug_Input_MouseDown)), cancellationToken);

    public Task MouseUpAsync(MouseButton button, CancellationToken cancellationToken = default)
        => Task.Run(() => Check(RugCoreNative.Rug_Input_MouseUp(H, (int)button),
            nameof(RugCoreNative.Rug_Input_MouseUp)), cancellationToken);

    public Task DragAndDropAsync(int sx, int sy, int ex, int ey,
        TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Check(RugCoreNative.Rug_Input_DragAndDrop(H, sx, sy, ex, ey, (int)trajectory, smooth ? 1 : 0),
            nameof(RugCoreNative.Rug_Input_DragAndDrop)), cancellationToken);

    public Task KeyDownAsync(int virtualKey, CancellationToken cancellationToken = default)
        => Task.Run(() => Check(RugCoreNative.Rug_Input_KeyDown(H, virtualKey),
            nameof(RugCoreNative.Rug_Input_KeyDown)), cancellationToken);

    public Task KeyUpAsync(int virtualKey, CancellationToken cancellationToken = default)
        => Task.Run(() => Check(RugCoreNative.Rug_Input_KeyUp(H, virtualKey),
            nameof(RugCoreNative.Rug_Input_KeyUp)), cancellationToken);

    public Task KeyPressAsync(int virtualKey, int holdMs = 0, CancellationToken cancellationToken = default)
        => Task.Run(() => Check(RugCoreNative.Rug_Input_KeyPress(H, virtualKey, holdMs),
            nameof(RugCoreNative.Rug_Input_KeyPress)), cancellationToken);

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        => Task.Run(() => Check(RugCoreNative.Rug_Input_SendText(H, text),
            nameof(RugCoreNative.Rug_Input_SendText)), cancellationToken);

    public Task SetTargetWindowAsync(nint hwnd, CancellationToken cancellationToken = default)
        => Task.Run(() => Check(RugCoreNative.Rug_Input_SetTargetWindow(H, hwnd),
            nameof(RugCoreNative.Rug_Input_SetTargetWindow)), cancellationToken);

    public Task SetHumanizeConfigAsync(HumanizeConfig config, CancellationToken cancellationToken = default)
    {
        var native = new RugHumanizeConfig
        {
            MinClickHoldMs = config.MinClickHoldMs,
            MaxClickHoldMs = config.MaxClickHoldMs,
            MinKeyHoldMs = config.MinKeyHoldMs,
            MaxKeyHoldMs = config.MaxKeyHoldMs,
            CorridorRatio = config.CorridorRatio,
            EnableJitter = config.EnableJitter ? 1 : 0,
        };
        return Task.Run(() => Check(RugCoreNative.Rug_Input_SetHumanizeConfig(H, in native),
            nameof(RugCoreNative.Rug_Input_SetHumanizeConfig)), cancellationToken);
    }

    public Task<IReadOnlyList<TrajectorySample>> PlanTrajectoryAsync(
        int sx, int sy, int ex, int ey,
        TrajectoryType trajectory = TrajectoryType.CubicBezier,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<TrajectorySample>>(() => PlanTrajectory(sx, sy, ex, ey, trajectory), cancellationToken);

    private IReadOnlyList<TrajectorySample> PlanTrajectory(int sx, int sy, int ex, int ey, TrajectoryType trajectory)
    {
        // Capacity 512 covers the humanizer's max step count (clamped to 120) with headroom.
        const int capacity = 512;
        var xs = new int[capacity];
        var ys = new int[capacity];
        var delays = new float[capacity];
        int count = capacity;

        int rc = RugCoreNative.Rug_Input_PlanTrajectory(H, sx, sy, ex, ey, (int)trajectory, xs, ys, delays, ref count);
        if (rc != RugStatus.Ok && rc != RugStatus.ErrBufferTooSmall)
            throw new RugNativeException(nameof(RugCoreNative.Rug_Input_PlanTrajectory), rc);

        var samples = new List<TrajectorySample>(count);
        for (int i = 0; i < count; i++)
            samples.Add(new TrajectorySample(xs[i], ys[i], delays[i]));
        return samples;
    }

    private static void Check(int rc, string function)
    {
        if (rc != RugStatus.Ok)
            throw new RugNativeException(function, rc);
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
