#nullable enable
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

namespace Rug.UI.Core.Abstractions;

/// <summary>One Lua script instance. StepAsync starts it; ResumeAsync awaits one pending operation.</summary>
public interface IScriptRuntime : IAsyncDisposable
{
    void ConfigureGateway(AutomationGateway gateway) { }
    void ConfigureInputArbiter(IInputArbiter arbiter) { }
    ScriptState State { get; }
    event EventHandler<ScriptStateChangedEventArgs>? StateChanged;
    /// <summary>Bind structural identity, launch inputs and a plugin config snapshot before initialization.</summary>
    void ConfigureState(RunIdentity identity, IReadOnlyDictionary<string, object?> launchInputs,
        IReadOnlyDictionary<string, object?> pluginConfig) { }
    Task InitializeAsync(string scriptPath, PluginManifest manifest, CancellationToken ct = default);
    /// <summary>Bind the owning task's black-box handler before execution begins.</summary>
    void ConfigureAnomalyHandler(Func<string, string, Task<string>> handler);
    /// <summary>Switch from one-shot execution to on_init/on_tick lifecycle mode before StepAsync.</summary>
    Task PrepareLifecycleAsync();
    /// <summary>Create the next on_tick coroutine after the previous hook completes.</summary>
    Task BeginTickAsync();
    /// <summary>Run on_stop after cancellation with its own watchdog deadline.</summary>
    Task InvokeStopHookAsync(TimeSpan timeout);
    Task<ScriptExecutionResult> StepAsync();
    Task<ScriptExecutionResult> ResumeAsync();
    void Pause();
    void Resume();
    void Stop();
}
