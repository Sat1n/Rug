using System.Text.Json;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Schema;

namespace Rug.UI.Services;

/// <summary>Starts the demo with instance-scoped inputs and projects its timeline for the UI.</summary>
public sealed class DemoWorkflowCoordinator : IDisposable
{
    public const string PluginId = "demo_arknights";
    public const string TimelineStateKey = "demo.steps";
    private readonly ICommandBus _bus;
    private readonly ITaskScheduler _scheduler;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<SchemaStep>> _stepsByInstance = [];
    private readonly Dictionary<Guid, IDisposable> _stateSubscriptions = [];
    private Guid? _activeInstanceId;
    private bool _disposed;

    public event EventHandler? TimelineChanged;

    public Guid? ActiveInstanceId { get { lock (_gate) return _activeInstanceId; } }
    public IReadOnlyList<SchemaStep> CurrentSteps
    {
        get
        {
            lock (_gate)
                return _activeInstanceId is Guid id && _stepsByInstance.TryGetValue(id, out var steps)
                    ? steps.ToArray() : [];
        }
    }

    public DemoWorkflowCoordinator(ICommandBus bus, ITaskScheduler scheduler)
    {
        _bus = bus;
        _scheduler = scheduler;
        _subscription = bus.Subscribe("demo.start_selected", StartAsync);
    }

    private async Task StartAsync(object? parameter, CancellationToken cancellationToken)
    {
        string[] selected = parameter as string[] ?? [];
        if (selected.Length == 0) throw new InvalidOperationException("请先选择至少一个任务。");
        foreach (var active in _scheduler.Instances.Where(item => item.PluginId == PluginId))
            await _scheduler.StopTaskAsync(active.InstanceId);
        lock (_gate)
        {
            foreach (var subscription in _stateSubscriptions.Values) subscription.Dispose();
            _stateSubscriptions.Clear();
            _activeInstanceId = null;
            _stepsByInstance.Clear();
        }
        TimelineChanged?.Invoke(this, EventArgs.Empty);
        var inputs = new Dictionary<string, object?> { ["demo.selection"] = string.Join(",", selected) };
        await _scheduler.StartTaskAsync(PluginId, 0, inputs, OnReady, cancellationToken);
    }

    private void OnReady(RunIdentity identity)
    {
        lock (_gate)
        {
            _activeInstanceId = identity.InstanceId;
            _stepsByInstance[identity.InstanceId] = [];
            _stateSubscriptions[identity.InstanceId] = _bus.SubscribeState(identity.InstanceScope, OnStateChanged);
        }
        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs change)
    {
        if (_disposed || change.Scope.Domain != StateDomain.InstanceState ||
            !string.Equals(change.Scope.PluginId, PluginId, StringComparison.OrdinalIgnoreCase)) return;
        if (change.Key == "demo.completed" && change.Value is true)
        {
            _ = StopCompletedAsync(change.Scope.InstanceId);
            return;
        }
        if (change.Key != "demo.step_event" || change.Value is not string json) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement step = document.RootElement;
            string id = step.GetProperty("id").GetString() ?? "";
            string title = step.GetProperty("title").GetString() ?? "";
            string status = step.GetProperty("status").GetString() ?? "";
            if (id.Length == 0 || title.Length == 0 ||
                status is not ("Waiting" or "Running" or "Succeeded" or "Failed")) return;
            SchemaStep[] snapshot;
            bool current;
            lock (_gate)
            {
                if (!_stepsByInstance.TryGetValue(change.Scope.InstanceId, out var steps))
                    _stepsByInstance[change.Scope.InstanceId] = steps = [];
                int index = steps.FindIndex(item => item.Id == id);
                var update = new SchemaStep(id, title, status, null);
                if (index >= 0) steps[index] = update;
                else steps.Add(update);
                snapshot = steps.ToArray();
                current = _activeInstanceId == change.Scope.InstanceId;
            }
            _bus.SetState(change.Scope, TimelineStateKey, snapshot);
            if (current) TimelineChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task StopCompletedAsync(Guid instanceId)
    {
        try { await _scheduler.StopTaskAsync(instanceId); }
        catch (Exception error) { _bus.SetState(StateScope.App, "demo.last_error", error.Message); }
        finally
        {
            lock (_gate)
            {
                if (_stateSubscriptions.Remove(instanceId, out var subscription)) subscription.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            foreach (var subscription in _stateSubscriptions.Values) subscription.Dispose();
            _stateSubscriptions.Clear();
        }
        _subscription.Dispose();
    }
}
