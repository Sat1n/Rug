using Rug.UI.Core.Native;

/// <summary>Diagnostic pump only; never implements the registered event-source contract.</summary>
internal sealed class UnregisteredRawInputPump : IAsyncDisposable
{
    private static long _nextGeneration;
    private readonly long _generation = Interlocked.Increment(ref _nextGeneration);
    private readonly WindowsRawInputReceiver _receiver = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private int _started, _disposed;
    private long _unexpectedEvents;
    private string? _failure;

    internal sealed record Snapshot(long Generation, uint ThreadId, nint Hwnd, bool RegistrationEnabled,
        bool PumpReady, bool ThreadExited, long TimerMessages, long InputMessages, long DeviceMessages,
        long UnexpectedEvents, string? Failure);
    internal Snapshot Status
    {
        get
        {
            var p = _receiver.DiagnosticSnapshot;
            var failure = Volatile.Read(ref _failure);
            return new(_generation, p.ThreadId, p.Hwnd, p.RegistrationEnabled,
                _ready.Task.IsCompletedSuccessfully && !_ended.Task.IsCompleted && failure is null,
                _ended.Task.IsCompleted && !_thread.IsAlive, p.TimerMessages, p.InputMessages,
                p.DeviceMessages, Interlocked.Read(ref _unexpectedEvents), failure);
        }
    }
    internal UnregisteredRawInputPump() => _thread = new Thread(Run)
        { IsBackground = true, Name = "Rug Raw Input receiver" };

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
    // @shape Same receiver RunCore/window/timer/loop, no registrations; copied event count only.
    // @source Rug.UI.Core/Native/WindowsRawInputReceiver.cs#class:WindowsRawInputReceiver
    private void Run()
    {
        try
        {
            _receiver.RunUnregisteredDiagnostic(() => _ready.TrySetResult(),
                _ => Interlocked.Increment(ref _unexpectedEvents), _stop.Token);
            if (!_stop.IsCancellationRequested) throw new InvalidOperationException("Unregistered pump exited unexpectedly");
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
