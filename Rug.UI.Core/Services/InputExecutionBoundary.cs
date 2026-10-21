#nullable enable
namespace Rug.UI.Core.Services;

/// <summary>Flow-local Gateway progress; queuing is not an input side effect.</summary>
internal static class InputExecutionBoundary
{
    internal static readonly AsyncLocal<Action?> Started = new();
    internal static readonly AsyncLocal<Rug.UI.Core.Models.AutomationCallContext?> Caller = new();
    internal static readonly AsyncLocal<Action<Rug.UI.Core.Models.ObservedClickResult>?> Observation = new();
    internal static void MarkStarted()
    {
        Started.Value?.Invoke();
        ExecutionTraceScope.Current.Value?.Emit(Rug.UI.Core.Models.TraceEventKind.InputSideEffect,
            reason:"Input side effect begins; not business success",uncertain:true);
    }
}
