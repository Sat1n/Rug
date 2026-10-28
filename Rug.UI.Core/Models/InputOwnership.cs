#nullable enable
namespace Rug.UI.Core.Models;

public enum InputControlState { AutomationAvailable, TakingOver, ManualControl, CleanupFailed }
public enum InputOwnerSource { Plugin, DevTools, PluginUI, Lua, Agent, Mcp }

/// <summary>Trusted host capability. Never deserialized from operation parameters.</summary>
public sealed class InputOwner
{
    private InputOwner(string principal, InputOwnerSource source, RunIdentity? run)
    { ArgumentException.ThrowIfNullOrWhiteSpace(principal); Principal = principal; Source = source; Run = run; }
    public string Principal { get; }
    public InputOwnerSource Source { get; }
    public RunIdentity? Run { get; }
    public static InputOwner ForRun(RunIdentity run) => new("run:" + run.InstanceId, InputOwnerSource.Plugin, run);
    public static InputOwner ForDevTools(string hostIdentity) => new(hostIdentity, InputOwnerSource.DevTools, null);
    internal static InputOwner ForCaller(AutomationCallContext caller, RunIdentity run) =>
        new(caller.Principal, caller.Source switch
        {
            AutomationSource.PluginUI => InputOwnerSource.PluginUI,
            AutomationSource.Lua => InputOwnerSource.Lua,
            AutomationSource.Agent => InputOwnerSource.Agent,
            AutomationSource.Mcp => InputOwnerSource.Mcp,
            _ => throw new ArgumentOutOfRangeException(nameof(caller))
        }, run);
}

/// <summary>Host-verified window lifetime, independent of plugin binding IDs.</summary>
public sealed record InputTarget(nint Hwnd, long WindowStamp);
public sealed record InputControlSnapshot(InputControlState State, long Revision, int Waiting, int Active, string? Error);

public sealed class InputUnavailableException(string message) : InvalidOperationException(message);
public sealed class InputCleanupException(string message, Exception inner) : Exception(message, inner);
