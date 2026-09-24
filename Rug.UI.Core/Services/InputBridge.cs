#nullable enable
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Security;

namespace Rug.UI.Core.Services;

/// <summary>
/// Permission-gated, per-window input delivery. Native InputService owns the actual
/// client-to-screen conversion for SendInput and retains client coordinates for
/// PostMessage; the mapper validates each point and exposes its screen projection.
/// </summary>
public sealed class InputBridge : IAsyncDisposable
{
    private readonly IInputService _input;
    private readonly ICoordinateMapper _mapper;
    private readonly PermissionInterceptor _permissions;
    private readonly Func<nint, bool> _ensureForeground;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private nint _hwnd;
    private InputDeliveryMode _defaultMode;
    private InputDeliveryMode _activeMode;
    private bool _bound;
    private bool _disposed;

    public InputBridge(IInputService input, PermissionInterceptor permissions,
        ICoordinateMapper? mapper = null, Func<nint, bool>? ensureForeground = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _mapper = mapper ?? new CoordinateMapper();
        _ensureForeground = ensureForeground ?? EnsureForeground;
    }

    /// <summary>Latest validated screen projection; native input still receives client pixels.</summary>
    public PointInt? LastMappedScreenPoint { get; private set; }

    public async Task BindAsync(nint hwnd, InputDeliveryMode mode, CancellationToken cancellationToken = default)
    {
        _permissions.Demand(Permission.ControlInput, "rug.input.bind");
        if (hwnd == 0) throw new ArgumentException("A bound window is required.", nameof(hwnd));
        ValidateMode(mode);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _input.SetTargetAsync(hwnd, mode == InputDeliveryMode.Win32PostMessage,
                cancellationToken).ConfigureAwait(false);
            _hwnd = hwnd;
            _defaultMode = mode;
            _activeMode = mode;
            _bound = true;
            LastMappedScreenPoint = null;
        }
        finally { _gate.Release(); }
    }

    public async Task ClickAsync(int x, int y, MouseButton button = MouseButton.Left,
        InputDeliveryMode? mode = null, CancellationToken cancellationToken = default)
    {
        _permissions.Demand(Permission.ControlInput, "rug.click");
        if (!Enum.IsDefined(button)) throw new ArgumentOutOfRangeException(nameof(button));
        if (mode is { } selected) ValidateMode(selected);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnbound();
            // Validate in the same physical client coordinate space consumed by native.
            // Do not feed the screen point back to the bound native controller.
            LastMappedScreenPoint = _mapper.ClientToScreen(_hwnd, x, y);
            await SelectModeAsync(mode ?? _defaultMode, cancellationToken).ConfigureAwait(false);
            await _input.MoveMouseAsync(x, y, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _input.ClickAsync(button, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task PressKeyAsync(int virtualKey, int durationMs = 0,
        CancellationToken cancellationToken = default)
    {
        _permissions.Demand(Permission.ControlInput, "rug.press_key");
        if (virtualKey is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(virtualKey));
        if (durationMs is < 0 or > 60000) throw new ArgumentOutOfRangeException(nameof(durationMs));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnbound();
            await SelectModeAsync(_defaultMode, cancellationToken).ConfigureAwait(false);
            if (durationMs == 0)
            {
                await _input.KeyPressAsync(virtualKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            await _input.KeyDownAsync(virtualKey, cancellationToken).ConfigureAwait(false);
            try { await Task.Delay(durationMs, cancellationToken).ConfigureAwait(false); }
            finally { await _input.KeyUpAsync(virtualKey).ConfigureAwait(false); }
        }
        finally { _gate.Release(); }
    }

    private async Task SelectModeAsync(InputDeliveryMode mode, CancellationToken ct)
    {
        if (_activeMode != mode)
        {
            await _input.SetTargetAsync(_hwnd, mode == InputDeliveryMode.Win32PostMessage, ct).ConfigureAwait(false);
            _activeMode = mode;
        }
        if (mode == InputDeliveryMode.Win32SendInput && !_ensureForeground(_hwnd))
            throw new InvalidOperationException("Target window could not be brought to the foreground for SendInput.");
    }

    private static bool EnsureForeground(nint hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Foreground input requires Windows 10 or later.");
        return WindowNative.GetForegroundWindow() == hwnd ||
            WindowNative.SetForegroundWindow(hwnd) && WindowNative.GetForegroundWindow() == hwnd;
    }

    private static void ValidateMode(InputDeliveryMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
    }

    private void ThrowIfUnbound()
    {
        ThrowIfDisposed();
        if (!_bound) throw new InvalidOperationException("Input bridge has no bound target window.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InputBridge));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; }
        finally { _gate.Release(); }
        _gate.Dispose();
    }
}
