#nullable enable
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;

namespace Rug.UI.Core.Services;

/// <summary>Global candidate-input policy over whole host input sessions, including waiting periods.
/// @shape events: detached generation/sequence/time; callbacks close admission but never await cleanup.
/// @source takeover: Abstractions/IInputArbiter.cs#interface:IInputArbiter</summary>
public sealed class InputInterventionCoordinator : IInputInterventionCoordinator
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly IInputEventSource _source;
    private readonly IInputArbiter _arbiter;
    private readonly ILogger _logger;
    private readonly Func<IReadOnlyCollection<ushort>> _pressedKeys;
    private readonly IDisposable _guard, _stop;
    private readonly HashSet<Session> _sessions = [];
    private readonly HashSet<(ulong Device, ushort Key, ushort Scan, ushort Extended)> _keys = [];
    private readonly HashSet<ushort> _heldAtBoundary = [];
    private readonly HashSet<(ulong Device, int Button)> _buttons = [];
    private readonly Dictionary<ulong, (long X, long Y)> _positions = [];
    private TaskCompletionSource _drained = Completed();
    private TaskCompletionSource? _takeover;
    private Task? _dispose;
    private long _revision, _generation, _sequence, _minimumSequence, _minimumTicks;
    private bool _startAttempted, _ready, _disposed, _tripped;
    private string? _reason, _detectorError, _cleanupError;

    public InputInterventionCoordinator(IInputEventSource source, IInputArbiter arbiter, ILogger<InputInterventionCoordinator> logger,
        Func<IReadOnlyCollection<ushort>>? pressedKeys = null)
    {
        _source = source; _arbiter = arbiter; _logger = logger;
        _pressedKeys = pressedKeys ?? InputKeyState.PressedKeys;
        _source.InputReceived += OnInput;
        _source.StateChanged += OnSourceState;
        _arbiter.StateChanged += OnControlState;
        _guard = _arbiter.RegisterAdmissionGuard(ValidateDetector);
        _stop = _arbiter.RegisterStopHandler(DrainSessionsAsync);
    }
    public event EventHandler? StateChanged;
    public InputInterventionSnapshot Snapshot
    {
        get
        {
            var source = _source.Snapshot;
            var control = _arbiter.Snapshot;
            lock (_gate) return new(_revision, source.State, source.Generation, _sessions.Count,
                _ready && !_disposed && !_tripped && _detectorError is null && source.State == InputEventSourceState.Ready &&
                source.Generation == _generation && source.Fault == InputEventSourceFault.None && source.ConsumerErrors == 0 &&
                control.State == InputControlState.AutomationAvailable && _sessions.Count > 0,
                _reason, _detectorError, _cleanupError);
        }
    }
    public Task PendingTakeover { get { lock (_gate) return _takeover?.Task ?? Task.CompletedTask; } }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_startAttempted) { RequireReady(_source.Snapshot); return; }
                _startAttempted = true;
            }
            try
            {
                await _source.StartAsync(cancellationToken).ConfigureAwait(false);
                var held = _pressedKeys();
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var source = _source.Snapshot;
                    if (source.State != InputEventSourceState.Ready) throw new InputUnavailableException("Input detector did not become ready.");
                    _generation = source.Generation; _sequence = source.LastSequence; _ready = true;
                    _heldAtBoundary.UnionWith(held); _revision++;
                }
            }
            catch (Exception error) { DetectorFailed("Input detector startup failed: " + error.Message); throw; }
        }
        finally { _lifecycle.Release(); Notify(); }
    }

    public async Task<IInputSession> BeginSessionAsync(InputOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Run is null && owner.Source != InputOwnerSource.DevTools)
            throw new ArgumentException("Input session needs a host run or DevTools owner.", nameof(owner));
        ValidateDetector(false);
        var boundary = await _source.CaptureBoundaryAsync(cancellationToken).ConfigureAwait(false);
        var held = _pressedKeys();
        var epoch = _arbiter.AutomationToken;
        Session session;
        lock (_gate)
        {
            RequireReady(_source.Snapshot);
            if (_tripped || boundary.Generation != _generation) throw new InputUnavailableException("Input session boundary was invalidated.");
            cancellationToken.ThrowIfCancellationRequested(); epoch.ThrowIfCancellationRequested();
            if (_sessions.Count >= 256) throw new InputUnavailableException("Input session capacity exceeded.");
            if (_sessions.Count == 0)
            {
                _minimumSequence = Math.Max(boundary.Sequence, _minimumSequence);
                _minimumTicks = Math.Max(boundary.MonotonicTicks, _minimumTicks);
                _heldAtBoundary.Clear(); _heldAtBoundary.UnionWith(held);
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            session = new(this, owner, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, epoch));
            _sessions.Add(session); _revision++;
        }
        Notify();
        return session;
    }
    private sealed class Session(InputInterventionCoordinator host, InputOwner owner, CancellationTokenSource cancellation) : IInputSession
    {
        private int _disposed;
        private readonly CancellationToken _token = cancellation.Token;
        public InputOwner Owner => owner;
        public CancellationToken Token => _token;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (host._gate)
            {
                host._sessions.Remove(this); host._revision++;
                if (host._sessions.Count == 0) host._drained.TrySetResult();
            }
            cancellation.Dispose(); host.Notify();
        }
    }
    private Task DrainSessionsAsync() { lock (_gate) return _drained.Task; }
    private void RequireReady(InputEventSourceSnapshot source)
    {
        if (_disposed || !_ready || _detectorError is not null || source.State != InputEventSourceState.Ready ||
            source.Generation != _generation || source.Fault != InputEventSourceFault.None || source.Overflows != 0 || source.ConsumerErrors != 0)
            throw new InputUnavailableException("Input detector unavailable: " + (_detectorError ?? source.Error ?? source.State.ToString()));
    }
    private void ValidateDetector(bool returning)
    {
        lock (_gate)
        {
            RequireReady(_source.Snapshot);
            if (!returning && _tripped) throw new InputUnavailableException("External input takeover has closed admission.");
        }
    }
    private void OnSourceState(object? sender, EventArgs args)
    {
        var source = _source.Snapshot;
        bool failed;
        lock (_gate)
        {
            if (_disposed) return;
            failed = _ready && (source.State != InputEventSourceState.Ready || source.Generation != _generation || source.Fault != InputEventSourceFault.None || source.ConsumerErrors != 0 || source.Overflows != 0);
            _revision++;
        }
        if (failed) DetectorFailed("Input detector stopped or faulted: " + (source.Error ?? source.State.ToString()));
        Notify();
    }
    private void OnControlState(object? sender, EventArgs args)
    {
        var control = _arbiter.Snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            if (control.State == InputControlState.AutomationAvailable && _tripped)
            {
                _minimumSequence = Math.Max(_minimumSequence, _source.Snapshot.LastSequence);
                _minimumTicks = Stopwatch.GetTimestamp();
                _tripped = false; _takeover = null; _reason = null; _cleanupError = null;
            }
            _revision++;
        }
        Notify();
    }

    /// <summary>@shape candidate: new down, wheel or effective motion; releases only maintain held state.
    /// @source events: Models/InputEvents.cs#class:InputSourceEvent</summary>
    private void OnInput(object? sender, InputSourceEvent item)
    {
        string? trigger = null, failure = null;
        lock (_gate)
        {
            if (_disposed || !_ready || _detectorError is not null || item.Generation != _generation || item.Sequence <= _sequence) return;
            long first = item.FirstSequence == 0 ? item.Sequence : item.FirstSequence;
            if (first > _sequence + 1 || item.Sequence < first || item.Samples < 1) failure = "Input detector event continuity lost.";
            _sequence = item.Sequence;
            var o = item.Observation;
            bool action = false;
            // Tagged absolute samples update the position baseline, but never trigger takeover.
            if (o.IsRugSoftwareInput && o.Kind == InputEventKind.Mouse && (o.MouseFlags & 1) != 0)
                _positions[o.DeviceId] = (o.X, o.Y);
            if (o.Kind == InputEventKind.DeviceRemoved)
            { _keys.RemoveWhere(key => key.Device == o.DeviceId); _buttons.RemoveWhere(button => button.Device == o.DeviceId); _positions.Remove(o.DeviceId); }
            else if (o.IsCandidateInput && o.Kind == InputEventKind.Keyboard)
            {
                var key = (o.DeviceId, o.VirtualKey, o.ScanCode, (ushort)(o.KeyFlags & 6));
                if (o.KeyReleased) { _keys.Remove(key); _heldAtBoundary.Remove(o.VirtualKey); }
                else action = _keys.Add(key) && !o.IsRepeat && !_heldAtBoundary.Contains(o.VirtualKey);
                if (action) trigger = "候选外部键盘按下";
            }
            else if (o.IsCandidateInput && o.Kind == InputEventKind.Mouse)
            {
                for (int button = 0; button < 5; button++)
                {
                    if ((o.ButtonFlags & (1 << (button * 2 + 1))) != 0) _buttons.Remove((o.DeviceId, button));
                    if ((o.ButtonFlags & (1 << (button * 2))) != 0) action |= _buttons.Add((o.DeviceId, button));
                }
                bool movement = item.HasMovement;
                if ((o.MouseFlags & 1) != 0)
                {
                    movement |= _positions.TryGetValue(o.DeviceId, out var old) && old != (o.X, o.Y);
                    _positions[o.DeviceId] = (o.X, o.Y);
                }
                else movement |= o.X != 0 || o.Y != 0;
                if (item.MovementSequence != 0)
                    movement &= item.MovementSequence > _minimumSequence && item.MovementTicks >= _minimumTicks;
                else if (item.FirstSequence != 0 && item.FirstSequence <= _minimumSequence)
                    movement = false; // merged pre-session motion cannot be promoted by a later zero sample
                bool wheel = (o.ButtonFlags & 0xC00) != 0 && o.WheelDelta != 0;
                action |= movement || wheel;
                if (action) trigger = wheel ? "候选外部滚轮" : movement ? "候选外部鼠标移动" : "候选外部鼠标按钮按下";
            }
            if (_keys.Count > 4096 || _buttons.Count > 4096 || _positions.Count > 4096) failure = "Input detector held-state capacity exceeded.";
            if (!action || _sessions.Count == 0 || _tripped || item.Sequence <= _minimumSequence || item.MonotonicTicks < _minimumTicks) trigger = null;
        }
        if (failure is not null) DetectorFailed(failure);
        else if (trigger is not null) RequestTakeover(trigger);
    }
    private void DetectorFailed(string error)
    {
        bool active;
        lock (_gate) { _detectorError ??= error; _revision++; active = _sessions.Count != 0; }
        if (active) RequestTakeover("输入检测器故障");
        Notify();
    }
    private void RequestTakeover(string reason)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_tripped) return;
            _tripped = true; _reason = reason; _revision++;
            completion = _takeover = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        // TakeOverAsync closes admission synchronously; observation never awaits cleanup on this callback.
        try { _ = ObserveTakeoverAsync(_arbiter.TakeOverAsync(), completion); }
        catch (Exception error) { _ = ObserveTakeoverAsync(Task.FromException(error), completion); }
        Notify();
    }
    private async Task ObserveTakeoverAsync(Task takeover, TaskCompletionSource completion)
    {
        try { await takeover.ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_gate) { _cleanupError = error.Message; _revision++; }
            try { _logger.LogError(error, "Candidate input takeover cleanup failed"); } catch { /* observed failure remains in snapshot */ }
        }
        finally { completion.TrySetResult(); Notify(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= Task.Run(DisposeCoreAsync));
    }
    private async Task DisposeCoreAsync()
    {
        lock (_gate) { _disposed = true; _revision++; }
        try { await _arbiter.TakeOverAsync().ConfigureAwait(false); }
        catch (Exception error) { lock (_gate) _cleanupError = error.Message; }
        await PendingTakeover.ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _source.InputReceived -= OnInput; _source.StateChanged -= OnSourceState; _arbiter.StateChanged -= OnControlState;
            await _source.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose(); _guard.Dispose(); _lifecycle.Release(); Notify();
            lock (_gate) { _keys.Clear(); _heldAtBoundary.Clear(); _buttons.Clear(); _positions.Clear(); StateChanged = null; }
        }
    }
    private void Notify()
    { foreach (EventHandler handler in StateChanged?.GetInvocationList() ?? []) { try { handler(this, EventArgs.Empty); } catch { } } }
    private static TaskCompletionSource Completed()
    { var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
}
