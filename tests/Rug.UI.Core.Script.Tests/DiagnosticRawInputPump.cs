using Rug.UI.Core.Native;
using Rug.UI.Core.Models;

/// <summary>Diagnostic pump only; never implements the registered event-source contract.</summary>
internal sealed class DiagnosticRawInputPump : IAsyncDisposable
{
    private static long _nextGeneration;
    private readonly long _generation = Interlocked.Increment(ref _nextGeneration);
    private readonly WindowsRawInputReceiver _receiver = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly bool _registerDevices;
    private readonly bool _readPackets;
    private readonly bool _registerKeyboard;
    private readonly bool _registerMouse;
    private readonly RawInputDiagnostics.RawAggregate _aggregate = new();
    private int _started, _disposed;
    private long _unexpectedEvents;
    private string? _failure;

    internal sealed record Snapshot(long Generation, uint ThreadId, nint Hwnd, bool RegistrationEnabled,
        bool PumpReady, bool ThreadExited, long TimerMessages, long InputMessages, long DeviceMessages,
        long UnexpectedEvents, string? Failure, RawInputDiagnostics.RawEvidence? Evidence,
        bool PacketReadingEnabled, long InputCleanupCalls, bool KeyboardRegistrationEnabled, bool MouseRegistrationEnabled);
    internal Snapshot Status
    {
        get
        {
            var p = _receiver.DiagnosticSnapshot;
            var failure = Volatile.Read(ref _failure);
            return new(_generation, p.ThreadId, p.Hwnd, p.RegistrationEnabled,
                _ready.Task.IsCompletedSuccessfully && !_ended.Task.IsCompleted && failure is null,
                _ended.Task.IsCompleted && !_thread.IsAlive, p.TimerMessages, p.InputMessages,
                p.DeviceMessages, Interlocked.Read(ref _unexpectedEvents), failure, _registerDevices && _readPackets ? _aggregate.Read() : null,
                p.PacketReadingEnabled, p.InputCleanupCalls, p.KeyboardRegistrationEnabled, p.MouseRegistrationEnabled);
        }
    }
    internal DiagnosticRawInputPump(bool registerDevices = false, bool readPackets = true, bool registerKeyboard = true, bool registerMouse = true)
    {
        if (!registerDevices && !readPackets) throw new ArgumentException("Message-count control requires device registration.");
        if ((!registerKeyboard || !registerMouse) && (!registerDevices || readPackets)) throw new ArgumentException("Selected-class registration requires a registered message-count control.");
        if (!registerKeyboard && !registerMouse) throw new ArgumentException("Registered control requires a device class.");
        _registerDevices = registerDevices;
        _readPackets = readPackets;
        _registerKeyboard = registerKeyboard;
        _registerMouse = registerMouse;
        _thread = new Thread(Run) { IsBackground = true, Name = "Rug Raw Input receiver" };
    }

    internal Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            try { _thread.Start(); }
            catch (Exception error) { Volatile.Write(ref _failure, error.Message); _ready.TrySetException(error); _ended.TrySetResult(); }
        }
        return _ready.Task;
    }
    // @shape Same receiver window/timer/loop; optional registration or registered count-only control, no queue/consumer.
    // @source Rug.UI.Core/Native/WindowsRawInputReceiver.cs#class:WindowsRawInputReceiver
    private void Run()
    {
        try
        {
            void Receive(InputObservation observation)
            {
                if (!_registerDevices) Interlocked.Increment(ref _unexpectedEvents);
                _aggregate.ObserveCopied(observation);
            }
            if (!_readPackets) _receiver.RunMessageCountDiagnostic(() => _ready.TrySetResult(), _stop.Token, _registerKeyboard, _registerMouse);
            else if (_registerDevices) _receiver.Run(() => _ready.TrySetResult(), Receive, _stop.Token);
            else _receiver.RunUnregisteredDiagnostic(() => _ready.TrySetResult(), Receive, _stop.Token);
            if (!_stop.IsCancellationRequested) throw new InvalidOperationException("Diagnostic receiver pump exited unexpectedly");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _ready.TrySetCanceled(_stop.Token); }
        catch (Exception error) { Volatile.Write(ref _failure, error.Message); _ready.TrySetException(error); }
        finally { _ended.TrySetResult(); }
    }
    internal Task SynchronizeAsync() => _receiver.SynchronizeAsync();
    public async ValueTask DisposeAsync()
    {
        bool ownsDisposal = Interlocked.Exchange(ref _disposed, 1) == 0;
        if (ownsDisposal) { _stop.Cancel(); _receiver.RequestStop(); }
        if (Volatile.Read(ref _started) != 0)
        {
            await _ended.Task.ConfigureAwait(false);
            if (_thread.IsAlive) await Task.Run(() => _thread.Join()).ConfigureAwait(false);
        }
        if (ownsDisposal) _stop.Dispose();
    }
}
