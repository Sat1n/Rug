#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

/// <summary>One Lua script instance. StepAsync starts it; ResumeAsync awaits one pending operation.</summary>
public interface IScriptRuntime : IAsyncDisposable
{
    ScriptState State { get; }
    event EventHandler<ScriptStateChangedEventArgs>? StateChanged;
    Task InitializeAsync(string scriptPath, PluginManifest manifest, CancellationToken ct = default);
    Task<ScriptExecutionResult> StepAsync();
    Task<ScriptExecutionResult> ResumeAsync();
    void Pause();
    void Resume();
    void Stop();
}
