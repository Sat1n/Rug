#nullable enable
namespace Rug.UI.Core.Models;

public enum ScriptState { Uninitialized, Ready, Running, Yielded, Paused, Stopped, Faulted }

public sealed record ScriptExecutionResult(ScriptState State, object? Value = null, Exception? Error = null);

public sealed class ScriptStateChangedEventArgs(ScriptState previous, ScriptState current) : EventArgs
{
    public ScriptState Previous { get; } = previous;
    public ScriptState Current { get; } = current;
}
