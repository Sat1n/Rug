#nullable enable
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;

namespace Rug.UI.Core.Models;

public enum AutomationTaskStatus { Starting, Running, Paused, Stopping, Stopped, Faulted }

/// <summary>One plugin/window instance and its exclusively owned service sessions.</summary>
public sealed class TaskExecutionContext
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool> _resume = CompletedSignal();
    private AutomationTaskStatus _status = AutomationTaskStatus.Starting;

    internal TaskExecutionContext(Guid instanceId, string pluginId, nint targetHwnd,
        CancellationTokenSource cancellation, IScriptRuntime runtime,
        ICaptureService capture, IInputService input)
    {
        InstanceId = instanceId;
        PluginId = pluginId;
        TargetHwnd = targetHwnd;
        Cancellation = cancellation;
        Runtime = runtime;
        Capture = capture;
        Input = input;
    }

    public Guid InstanceId { get; }
    public string PluginId { get; }
    public nint TargetHwnd { get; }
    public CancellationTokenSource Cancellation { get; }
    public IScriptRuntime Runtime { get; }
    public AutomationTaskStatus TaskStatus { get { lock (_gate) return _status; } }
    public Exception? LastError { get; internal set; }

    internal ICaptureService Capture { get; }
    internal IInputService Input { get; }
    internal Task? Runner { get; set; }

    internal void SetStatus(AutomationTaskStatus status)
    {
        lock (_gate)
        {
            if (status == AutomationTaskStatus.Running && _status is
                AutomationTaskStatus.Paused or AutomationTaskStatus.Stopping or
                AutomationTaskStatus.Stopped or AutomationTaskStatus.Faulted) return;
            _status = status;
        }
    }

    internal bool Pause()
    {
        lock (_gate)
        {
            if (_status is not (AutomationTaskStatus.Starting or AutomationTaskStatus.Running)) return false;
            _status = AutomationTaskStatus.Paused;
            _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Runtime.Pause();
            return true;
        }
    }

    internal bool Resume()
    {
        lock (_gate)
        {
            if (_status != AutomationTaskStatus.Paused) return false;
            _status = AutomationTaskStatus.Running;
            Runtime.Resume();
            _resume.TrySetResult(true);
            return true;
        }
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            if (_status is AutomationTaskStatus.Stopped or AutomationTaskStatus.Stopping) return;
            _status = AutomationTaskStatus.Stopping;
            Cancellation.Cancel();
            Runtime.Stop();
            _resume.TrySetResult(true);
        }
    }

    internal Task WaitIfPausedAsync(CancellationToken token)
    {
        lock (_gate) return _resume.Task.WaitAsync(token);
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(true);
        return signal;
    }
}
