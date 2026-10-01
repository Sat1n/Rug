#nullable enable
using System.Diagnostics;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;

namespace Rug.UI.Core.Services;

/// <summary>Bounded, generation-isolated event delivery. Explicit host startup only.</summary>
public sealed class RawInputEventSource : IInputEventSource
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1), _wake = new(0, 1);
    private readonly Func<IRawInputReceiver> _factory;
    private readonly int _capacity;
    private readonly LinkedList<InputSourceEvent> _buffer = new();
    private IRawInputReceiver? _receiver;
    private CancellationTokenSource? _session;
    private Task? _native, _consumer;
    private bool _disposed, _notify;
    private long _generation, _revision, _sequence, _keyboard, _mouse, _devices, _coalesced, _overflows, _consumerErrors;
    private InputEventSourceState _state;
    private InputEventSourceFault _fault;
    private string? _error;
    private EventHandler<InputSourceEvent>? _events;
    private EventHandler? _states;

    public RawInputEventSource(int capacity = 1024) : this(() => new WindowsRawInputReceiver(), capacity) { }
    internal RawInputEventSource(Func<IRawInputReceiver> factory, int capacity = 1024)
    {
        if (capacity is < 2 or > 65536) throw new ArgumentOutOfRangeException(nameof(capacity));
        _factory = factory; _capacity = capacity;
    }
    public InputEventSourceSnapshot Snapshot
    {
        get { lock (_gate) return new(_state, _generation, _revision, _fault, _error, _keyboard, _mouse, _devices,
            _coalesced, _overflows, _consumerErrors, _buffer.Count, (_events?.GetInvocationList().Length ?? 0) + (_states?.GetInvocationList().Length ?? 0)); }
    }
    public event EventHandler<InputSourceEvent> InputReceived
    {
        add { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _events += value; } }
        remove { lock (_gate) _events -= value; }
    }
    public event EventHandler StateChanged
    {
        add { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _states += value; } }
        remove { lock (_gate) _states -= value; }
    }
    private void Wake() { if (_wake.CurrentCount == 0) { try { _wake.Release(); } catch (SemaphoreFullException) { } } }
    private void ChangeState(InputEventSourceState state)
    { _state = state; _revision++; _notify = true; Wake(); }

    /// <summary>@shape startup: serialized readiness/cancellation; @source receiver: Native/IRawInputReceiver.cs#interface:IRawInputReceiver</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Snapshot.State == InputEventSourceState.Ready) return;
            await EndSessionAsync().ConfigureAwait(false);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long generation;
            lock (_gate)
            {
                generation = ++_generation; _sequence = _keyboard = _mouse = _devices = _coalesced = _overflows = _consumerErrors = 0;
                _fault = InputEventSourceFault.None; _error = null;
                _session = new(); _receiver = _factory(); ChangeState(InputEventSourceState.Starting);
            }
            var receiver = _receiver; var token = _session.Token;
            _consumer = Task.Run(() => ConsumeAsync(generation, token));
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _native = ended.Task;
            var thread = new Thread(() =>
            {
                try
                {
                    receiver.Run(() =>
                    {
                        lock (_gate) { token.ThrowIfCancellationRequested(); ChangeState(InputEventSourceState.Ready); }
                        ready.TrySetResult();
                    }, observation => Publish(generation, observation), token);
                    if (!token.IsCancellationRequested) throw new RawInputException(InputEventSourceFault.NativeFailure, "Receiver exited unexpectedly.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { ready.TrySetCanceled(token); }
                catch (Exception error) { Fail(generation, error); ready.TrySetException(error); }
                finally { ended.TrySetResult(); }
            }) { IsBackground = true, Name = "Rug Raw Input receiver" };
            try { thread.Start(); }
            catch (Exception error) { Fail(generation, error); ready.TrySetException(error); ended.TrySetResult(); }
            try { await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                _session.Cancel(); receiver.RequestStop();
                await EndSessionAsync().ConfigureAwait(false);
                lock (_gate) { if (_state != InputEventSourceState.Faulted) ChangeState(InputEventSourceState.Stopped); }
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>@shape observation: copied scalar values; @source receiver: Native/WindowsRawInputReceiver.cs</summary>
    private void Publish(long generation, InputObservation observation)
    {
        bool stop = false;
        lock (_gate)
        {
            if (generation != _generation || _state != InputEventSourceState.Ready || _session!.IsCancellationRequested) return;
            if (observation.Kind == InputEventKind.Keyboard) _keyboard++;
            else if (observation.Kind == InputEventKind.Mouse) _mouse++;
            else _devices++;
            var item = new InputSourceEvent(generation, ++_sequence, Stopwatch.GetTimestamp(), observation);
            try
            {
                if (_buffer.Last is { Value: var last } && last.Observation.IsPureMovement && observation.IsPureMovement
                    && last.Observation.DeviceId == observation.DeviceId && last.Observation.Foreground == observation.Foreground
                    && last.Observation.MouseFlags == observation.MouseFlags && last.Observation.ExtraInformation == observation.ExtraInformation)
                {
                    bool absolute = (observation.MouseFlags & 1) != 0;
                    _buffer.Last.Value = item with { Samples = checked(last.Samples + 1), Observation = observation with
                    { X = absolute ? observation.X : checked(last.Observation.X + observation.X), Y = absolute ? observation.Y : checked(last.Observation.Y + observation.Y) } };
                    _coalesced++;
                }
                else if (_buffer.Count >= _capacity) throw new OverflowException();
                else _buffer.AddLast(item);
            }
            catch (OverflowException)
            {
                _overflows++; SetFault(InputEventSourceFault.Overflow, "Input buffer overflow; observation continuity lost."); stop = true;
            }
            _revision++; Wake();
        }
        if (stop) _receiver?.RequestStop();
    }
    private void SetFault(InputEventSourceFault fault, string error)
    { _fault = fault; _error = error; _buffer.Clear(); ChangeState(InputEventSourceState.Faulted); }
    private void Fail(long generation, Exception error)
    {
        lock (_gate)
        {
            var fault = error is RawInputException native ? native.Fault : InputEventSourceFault.NativeFailure;
            if (generation != _generation || _state == InputEventSourceState.Stopped || _state == InputEventSourceState.Faulted && fault != InputEventSourceFault.CleanupFailure) return;
            if (fault == InputEventSourceFault.Overflow) _overflows++;
            SetFault(fault, error.Message);
        }
    }
    private async Task ConsumeAsync(long generation, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            while (!token.IsCancellationRequested)
            {
                InputSourceEvent? item = null; Delegate[]? states = null, events = null;
                lock (_gate)
                {
                    if (generation != _generation) return;
                    if (_notify) { _notify = false; states = _states?.GetInvocationList(); }
                    if (_state == InputEventSourceState.Ready && _buffer.First is { } first)
                    { item = first.Value; _buffer.RemoveFirst(); events = _events?.GetInvocationList(); }
                }
                if (states is not null) foreach (EventHandler handler in states) Invoke(() => handler(this, EventArgs.Empty));
                if (item is not null && events is not null) foreach (EventHandler<InputSourceEvent> handler in events)
                {
                    lock (_gate) { if (_state != InputEventSourceState.Ready || token.IsCancellationRequested) break; }
                    Invoke(() => handler(this, item));
                }
                if (item is null) break;
            }
        }
    }
    private void Invoke(Action callback)
    { try { callback(); } catch { lock (_gate) { _consumerErrors++; _revision++; } } }

    private async Task EndSessionAsync()
    {
        if (_session is null) return;
        _session.Cancel(); _receiver?.RequestStop();
        if (_native is not null) await _native.ConfigureAwait(false);
        if (_consumer is not null) await _consumer.ConfigureAwait(false);
        _session.Dispose(); _session = null; _receiver = null; _native = _consumer = null;
        lock (_gate) _buffer.Clear();
    }
    private void RequestEnd()
    {
        CancellationTokenSource? session; IRawInputReceiver? receiver;
        lock (_gate) { session = _session; receiver = _receiver; }
        try { session?.Cancel(); } catch (ObjectDisposedException) { }
        receiver?.RequestStop();
    }
    public async Task StopAsync()
    {
        RequestEnd(); // A stop also interrupts startup before waiting for the lifecycle gate.
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await EndSessionAsync().ConfigureAwait(false);
            Delegate[]? states;
            lock (_gate)
            {
                states = _state == InputEventSourceState.Stopped ? null : _states?.GetInvocationList();
                if (_state != InputEventSourceState.Stopped) { _state = _fault == InputEventSourceFault.CleanupFailure ? InputEventSourceState.Faulted : InputEventSourceState.Stopped; _revision++; }
                _events = null; _states = null; _notify = false;
            }
            if (states is not null) await Task.Run(() => { foreach (EventHandler handler in states) Invoke(() => handler(this, EventArgs.Empty)); }).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
    internal Task SynchronizeAsync() => _receiver?.SynchronizeAsync() ?? Task.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        RequestEnd();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await EndSessionAsync().ConfigureAwait(false);
            lock (_gate) { _state = _fault == InputEventSourceFault.CleanupFailure ? InputEventSourceState.Faulted : InputEventSourceState.Stopped; _revision++; _events = null; _states = null; _notify = false; }
        }
        finally { _lifecycle.Release(); }
    }
}
