#nullable enable
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Bounded conflict-FIFO queue. Reserves window and global foreground atomically.
/// @shape resources: HWND plus optional global foreground; callbacks execute outside state lock.
/// @source owner: Models/RunIdentity.cs#class:RunIdentity</summary>
public sealed class InputArbiter(IInputWindowValidator? windows = null, int capacity = 256) : IInputArbiter
{
    public static IInputArbiter Shared { get; } = new InputArbiter();
    private readonly IInputWindowValidator _windows = windows ?? new InputWindowValidator();
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _gate = new();
    private readonly List<Pending> _queue = [];
    private readonly HashSet<Lease> _active = [];
    private readonly List<Func<Task>> _stops = [];
    private readonly List<Action<bool>> _guards = [];
    private CancellationTokenSource _epoch = new();
    private InputControlState _state;
    private long _revision;
    private string? _error;
    private Task? _takeover;
    private TaskCompletionSource _drained = Completed();
    public event EventHandler? StateChanged;
    public IReadOnlyList<InputOwner> ActiveOwners { get { lock (_gate) return _active.Select(a => a.Owner).ToArray(); } }
    public int RegisteredStopHandlerCount { get { lock (_gate) return _stops.Count; } }
    public int RegisteredAdmissionGuardCount { get { lock (_gate) return _guards.Count; } }
    public InputControlSnapshot Snapshot { get { lock (_gate) return new(_state, _revision, _queue.Count, _active.Count, _error); } }
    public CancellationToken AutomationToken
    { get { CheckGuards(); lock (_gate) { CheckAvailable(); return _epoch.Token; } } }
    public InputTarget BindTarget(nint hwnd) => _windows.Bind(hwnd);
    public void ValidateTarget(InputTarget target, bool requireControl = true)
    {
        if (!_windows.IsValid(target)) throw new InputUnavailableException("Target lifetime is no longer valid.");
        if (!requireControl) return;
        CheckGuards();
        lock (_gate) CheckAvailable();
    }

    private sealed class Pending(InputOwner owner, InputTarget target, bool foreground, CancellationTokenSource cancellation, Action validate)
    {
        public InputOwner Owner { get; } = owner;
        public InputTarget Target { get; } = target;
        public bool Foreground { get; } = foreground;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Action Validate { get; } = validate;
        public TaskCompletionSource<IInputLease> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task<IInputLease> AcquireAsync(InputOwner owner, InputTarget target, InputDeliveryMode mode,
        Action validateAdmission, CancellationToken cancellationToken = default, DateTimeOffset? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(validateAdmission);
        CheckGuards();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (owner.Run is { } run && run.TargetHwnd != target.Hwnd) throw new InputUnavailableException("Owner target mismatch.");
        validateAdmission();
        if (!_windows.IsValid(target)) throw new InputUnavailableException("Target lifetime is no longer valid.");
        Pending pending;
        lock (_gate)
        {
            CheckAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (_queue.Count >= _capacity) throw new InputUnavailableException("Input queue capacity exceeded.");
            if (deadline <= DateTimeOffset.UtcNow) throw new TimeoutException("Input deadline expired.");
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _epoch.Token);
            if (deadline is { } due) linked.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0, (due - DateTimeOffset.UtcNow).TotalMilliseconds)));
            pending = new(owner, target, mode == InputDeliveryMode.Win32SendInput, linked, validateAdmission);
            _queue.Add(pending);
            Pump();
        }
        IInputLease? lease = null;
        try
        {
            using var registration = pending.Cancellation.Token.Register(() => CancelWaiting(pending));
            lease = await pending.Ready.Task.ConfigureAwait(false);
            lease.Token.ThrowIfCancellationRequested();
            validateAdmission();
            CheckGuards();
            if (!_windows.IsValid(target)) throw new InputUnavailableException("Target invalidated while queued.");
            lock (_gate) CheckAvailable();
            return lease;
        }
        catch
        {
            if (lease is not null) lease.Dispose();
            else pending.Cancellation.Dispose();
            throw;
        }
    }

    private void CancelWaiting(Pending pending)
    {
        lock (_gate)
        {
            if (!_queue.Remove(pending)) return;
            pending.Ready.TrySetCanceled(pending.Cancellation.Token);
            Pump();
        }
    }
    private static bool Conflicts(InputTarget a, bool foregroundA, InputTarget b, bool foregroundB) =>
        a.Hwnd == b.Hwnd || foregroundA && foregroundB;
    private void Pump()
    {
        if (_state != InputControlState.AutomationAvailable) return;
        for (int i = 0; i < _queue.Count;)
        {
            var p = _queue[i];
            if (_active.Any(a => Conflicts(a.Pending.Target, a.Pending.Foreground, p.Target, p.Foreground)) ||
                _queue.Take(i).Any(q => Conflicts(q.Target, q.Foreground, p.Target, p.Foreground))) { i++; continue; }
            _queue.RemoveAt(i);
            if (p.Cancellation.IsCancellationRequested) { p.Ready.TrySetCanceled(p.Cancellation.Token); continue; }
            if (_active.Count == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var lease = new Lease(this, p);
            _active.Add(lease);
            p.Ready.SetResult(lease);
        }
    }
    private sealed class Lease(InputArbiter host, Pending pending) : IInputLease
    {
        public Pending Pending { get; } = pending;
        public InputOwner Owner => Pending.Owner;
        public InputTarget Target => Pending.Target;
        private int _released;
        private bool _unsafe;
        public CancellationToken Token => Pending.Cancellation.Token;
        public void Validate()
        {
            host.CheckGuards();
            Token.ThrowIfCancellationRequested();
            Pending.Validate();
            if (!host._windows.IsValid(Pending.Target)) throw new InputUnavailableException("Input target is no longer valid.");
            lock (host._gate) host.CheckAvailable();
        }
        public void CleanupFailed(Exception error)
        {
            lock (host._gate) { _unsafe = true; host._state = InputControlState.CleanupFailed; host._error = error.Message; host._revision++; host._drained.TrySetException(error); }
            host._epoch.Cancel();
            host.Notify();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (host._gate)
            {
                // A failed release leaves its resources quarantined, never reused.
                if (!_unsafe) { host._active.Remove(this); if (host._active.Count == 0) host._drained.TrySetResult(); host.Pump(); }
            }
            Pending.Cancellation.Dispose();
        }
    }
    public IDisposable RegisterStopHandler(Func<Task> stopInputRuns)
    {
        ArgumentNullException.ThrowIfNull(stopInputRuns);
        lock (_gate) _stops.Add(stopInputRuns);
        return new Registration(this, stopInputRuns);
    }
    private sealed class Registration(InputArbiter host, Func<Task> stop) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) lock (host._gate) host._stops.Remove(stop); }
    }
    public IDisposable RegisterAdmissionGuard(Action<bool> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        lock (_gate) _guards.Add(guard);
        return new GuardRegistration(this, guard);
    }
    private sealed class GuardRegistration(InputArbiter host, Action<bool> guard) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) lock (host._gate) host._guards.Remove(guard); }
    }
    private void CheckGuards(bool returning = false)
    {
        Action<bool>[] guards;
        lock (_gate) guards = _guards.ToArray();
        foreach (var guard in guards) guard(returning);
    }

    public Task TakeOverAsync()
    {
        Task result;
        lock (_gate)
        {
            if (_takeover is not null) return _takeover;
            if (_state == InputControlState.ManualControl) return Task.CompletedTask;
            if (_state == InputControlState.CleanupFailed) return Task.FromException(new InputUnavailableException(_error ?? "Input cleanup failed."));
            _state = InputControlState.TakingOver; _revision++;
            var stops = _stops.ToArray();
            var epoch = _epoch;
            // No callback or cancellation runs inside the arbitration lock.
            result = _takeover = Task.Run(() => FinishTakeoverAsync(epoch, stops));
        }
        Notify();
        return result;
    }
    private async Task FinishTakeoverAsync(CancellationTokenSource epoch, Func<Task>[] stops)
    {
        try
        {
            epoch.Cancel();
            Task drained;
            lock (_gate) drained = _drained.Task;
            await Task.WhenAll(stops.Select(s => s()).Append(drained)).ConfigureAwait(false);
            lock (_gate)
            {
                if (_state == InputControlState.CleanupFailed) throw new InputUnavailableException(_error ?? "Cleanup failed.");
                _state = InputControlState.ManualControl; _revision++;
            }
        }
        catch (Exception error)
        {
            lock (_gate) { _state = InputControlState.CleanupFailed; _error = error.Message; _revision++; }
            throw;
        }
        finally { Notify(); }
    }
    public void ReturnControl()
    {
        CheckGuards(returning: true);
        lock (_gate)
        {
            if (_state == InputControlState.AutomationAvailable) return;
            if (_state != InputControlState.ManualControl || _active.Count != 0 || _queue.Count != 0)
                throw new InputUnavailableException("Input cleanup has not completed.");
            _epoch.Dispose(); _epoch = new(); _takeover = null;
            _state = InputControlState.AutomationAvailable; _revision++;
        }
        Notify();
    }
    private void CheckAvailable()
    { if (_state != InputControlState.AutomationAvailable) throw new InputUnavailableException("Automation input is closed: " + _state); }
    private void Notify()
    { foreach (EventHandler handler in StateChanged?.GetInvocationList() ?? []) { try { handler(this, EventArgs.Empty); } catch { /* observer cannot affect control */ } } }
    private static TaskCompletionSource Completed()
    { var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
}
