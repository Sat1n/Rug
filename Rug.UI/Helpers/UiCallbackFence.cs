namespace Rug.UI.Helpers;

/// <summary>Retire a UI producer, then await callbacks already executing without waiting under the gate.</summary>
public sealed class UiCallbackFence
{
    private readonly object _gate=new();
    private bool _closed;
    private int _active;
    private TaskCompletionSource? _drained;
    public IDisposable? Enter()
    {
        lock(_gate){if(_closed)return null;_active++;return new Lease(this);}
    }
    public Task Close()
    {
        lock(_gate){_closed=true;return _active==0?Task.CompletedTask:(_drained??=new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;}
    }
    private sealed class Lease(UiCallbackFence owner):IDisposable
    {
        private int _disposed;
        public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;lock(owner._gate)if(--owner._active==0)owner._drained?.TrySetResult();}
    }
}
