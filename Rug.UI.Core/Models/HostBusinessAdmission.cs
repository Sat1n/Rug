namespace Rug.UI.Core.Models;

/// <summary>Shared host gate, independent of cancellation callback propagation. No services or callbacks.</summary>
internal sealed class HostBusinessAdmission
{
    private int _closed;
    internal bool IsClosed => Volatile.Read(ref _closed)!=0;
    internal void Close()=>Interlocked.Exchange(ref _closed,1);
}
