#nullable enable
using System.Runtime.Versioning;

namespace Rug.UI.Core.Native;

/// <summary>Confirms asynchronous cross-input-queue activation without joining input queues.</summary>
internal sealed class ForegroundWindowActivation
{
    internal static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private readonly Func<nint> _read;
    private readonly Func<nint, bool> _activate;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal ForegroundWindowActivation(Func<nint> read, Func<nint, bool> activate,
        TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _read = read;
        _activate = activate;
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, _clock, token));
    }

    internal static Task EnsureAsync(nint hwnd, CancellationToken token, Action validate)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Foreground input requires Windows 10 or later.");
        return CreateWindows().ConfirmAsync(hwnd, token, validate);
    }

    [SupportedOSPlatform("windows10.0.17763.0")]
    private static ForegroundWindowActivation CreateWindows() =>
        new(WindowNative.GetForegroundWindow, WindowNative.SetForegroundWindow);

    /// <summary>
    /// @shape hwnd: verified target; validate: live lease/permission/run checks; token: lease cancellation.
    /// @source WindowNative.cs#class:WindowNative
    /// SetForegroundWindow can return before another input queue finishes activation.
    /// Make one activation attempt, then confirm the actual foreground within a monotonic deadline.
    /// The caller retains its lease throughout; this wait never suppresses input detection.
    /// </summary>
    internal async Task ConfirmAsync(nint hwnd, CancellationToken token, Action validate)
    {
        token.ThrowIfCancellationRequested();
        validate();
        if (_read() == hwnd) return;
        long started = _clock.GetTimestamp();
        bool accepted = _activate(hwnd);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            validate();
            nint observed = _read();
            if (observed == hwnd) return;
            TimeSpan elapsed = _clock.GetElapsedTime(started);
            if (!accepted || elapsed >= ConfirmationTimeout)
                throw new InvalidOperationException(
                    $"Target window could not be brought to the foreground for SendInput: " +
                    $"activationAccepted={accepted}, target=0x{hwnd:X}, foreground=0x{observed:X}, " +
                    $"confirmation={(accepted ? "timed-out" : "declined")}, elapsedMs={elapsed.TotalMilliseconds:F0}.");
            await _delay(TimeSpan.FromTicks(Math.Min(PollInterval.Ticks,
                (ConfirmationTimeout - elapsed).Ticks)), token).ConfigureAwait(false);
        }
    }
}
