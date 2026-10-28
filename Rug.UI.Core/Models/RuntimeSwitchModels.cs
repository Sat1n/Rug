#nullable enable
using Rug.UI.Core.Abstractions;

namespace Rug.UI.Core.Models;

/// <summary>Trusted direct continuation only: no state/action/config transformation.</summary>
public sealed record RuntimeUpdateCompatibility(string RuleId, PluginInstallation Installation,
    string SourceSnapshot, string TargetSnapshot, string SourceVersion, string TargetVersion,
    string SourceHash, string TargetHash, string NodeId, int StateVersion, string ConfigHash,
    IReadOnlyList<string> CompletedActionIds)
{
    internal CancellationToken RegistrationToken { get; init; }
}
public sealed record RuntimeUpdateOrigin(Guid UpdateId, string RuleId, string CheckpointFingerprint,
    string SourceSnapshot, string TargetSnapshot);
internal sealed class RuntimeSwitchGroup
{
    public readonly TaskCompletionSource Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _restoreDecision; // 0 observing, 1 interrupted, 2 restore completion committed.
    public bool IsInterrupted => Volatile.Read(ref _restoreDecision) == 1;
    public bool IsOpen => Opened.Task.IsCompletedSuccessfully && !IsInterrupted;
    public void Interrupt()
    {
        if (Interlocked.CompareExchange(ref _restoreDecision, 1, 0) == 0) Interrupted.TrySetResult();
    }
    public bool CompleteRestoration() => Interlocked.CompareExchange(ref _restoreDecision, 2, 0) == 0;
}
internal sealed record RuntimeSwitchCandidate(RuntimeSwitchGroup Group, RuntimeUpdateOrigin Origin);
internal sealed class RuntimeSwitchWork(Guid id, PluginSnapshot effective, PluginSnapshot candidate,
    TaskExecutionContext[] sources, IReadOnlyDictionary<Guid, UpdateCheckpoint> receipts)
{
    public Guid Id { get; } = id;
    public PluginSnapshot Effective { get; } = effective;
    public PluginSnapshot Candidate { get; } = candidate;
    public TaskExecutionContext[] Sources { get; } = sources;
    public IReadOnlyDictionary<Guid, UpdateCheckpoint> Receipts { get; } = receipts;
    public readonly TaskCompletionSource Aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource<RuntimeUpdateSnapshot> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Stopping, Committed;
    public RuntimeUpdateSnapshot InitialSnapshot { get; set; } = null!;
    public RuntimeSwitchGroup Group { get; } = new();
    public Dictionary<Guid, RunIdentity> Replacements { get; } = [];
}
