using Rug.UI.Core.Native;

/// <summary>Native count-only receiver in this process, called on the same dedicated managed Thread topology.</summary>
internal sealed class DiagnosticNativeRawInputPump : IDiagnosticRawInputPump
{
    private static long _nextGeneration;
    private readonly long _generation = Interlocked.Increment(ref _nextGeneration);
    private readonly object _gate = new();
    private readonly NativeRawInputDiagnosticReceiver _receiver;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _start, _dispose;
    private string? _failure;
    internal string LoadedPath => _receiver.LoadedPath;
    internal static string? LibraryPath
    {
        get
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Rug.slnx"))) root = root.Parent;
            return root is null ? null : Path.Combine(root.FullName, "x64", "Debug", "Rug.NativeRawReceiver.dll");
        }
    }
    internal DiagnosticNativeRawInputPump()
    {
        string path = LibraryPath ?? throw new FileNotFoundException("Repository root not found.");
        if (!File.Exists(path)) throw new FileNotFoundException("Build with tests/run_native_input_probe.ps1 -Mode Build first.", path);
        _receiver = new(path);
        _thread = new Thread(Run) { IsBackground = true, Name = "Rug Raw Input receiver" };
    }
    public DiagnosticRawInputPump.Snapshot Status
    {
        get
        {
            var p = _receiver.Status;
            string? failure = Volatile.Read(ref _failure) ?? p.Failure;
            return new(_generation, (uint)p.ThreadId, (nint)p.Hwnd, true,
                p.Ready != 0 && !_ended.Task.IsCompleted && failure is null,
                _ended.Task.IsCompleted && !_thread.IsAlive, (long)p.Timer, (long)p.Input, (long)p.Device,
                0, failure, null, false, (long)p.Cleanup, true, false);
        }
    }
    public Task StartAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            if (_start is not null) return _start;
            _thread.Start(); return _start = AwaitReadyAsync();
        }
    }
    // @shape native scalar readiness polling; never a callback on the message thread.
    // @source tests/NativeInputProbe/RawReceiver.cpp#class:Receiver
    private async Task AwaitReadyAsync()
    {
        while (true)
        {
            var p = Status;
            if (p.Failure is not null) throw new InvalidOperationException(p.Failure);
            if (p.PumpReady) return;
            if (_ended.Task.IsCompleted) throw new OperationCanceledException("Native receiver ended before readiness.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
    private void Run()
    {
        try { _receiver.Run(); }
        catch (Exception error) { Volatile.Write(ref _failure, error.Message); }
        finally { _ended.TrySetResult(); }
    }
    public async Task SynchronizeAsync()
    {
        ulong barrier = _receiver.SubmitBarrier();
        if (barrier == 0)
        {
            if (Status.Failure is not null) throw new InvalidOperationException(Status.Failure);
            return;
        }
        while (_receiver.Status.Barrier < barrier)
        {
            if (_ended.Task.IsCompleted || Status.Failure is not null) throw new InvalidOperationException(Status.Failure ?? "Native receiver exited before barrier.");
            await Task.Delay(1).ConfigureAwait(false);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        _receiver.RequestStop();
        if (_start is not null)
        {
            await _ended.Task.ConfigureAwait(false);
            await Task.Run(() => _thread.Join()).ConfigureAwait(false);
        }
        _receiver.Dispose(); // After native cleanup AND actual thread join, including start failures.
    }
}
