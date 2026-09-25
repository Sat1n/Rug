using System.Text.Json;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Schema;

namespace Rug.UI.Services;

/// <summary>Routes the demonstration command to Lua and projects Lua step events into the UI timeline.</summary>
public sealed class DemoWorkflowCoordinator : IDisposable
{
    public const string PluginId = "demo_arknights";
    private readonly ICommandBus _bus;
    private readonly ITaskScheduler _scheduler;
    private readonly IDisposable _subscription;
    private readonly List<SchemaStep> _steps = [];

    public DemoWorkflowCoordinator(ICommandBus bus, ITaskScheduler scheduler)
    {
        _bus = bus;
        _scheduler = scheduler;
        _subscription = bus.Subscribe("demo.start_selected", StartAsync);
        bus.StateChanged += OnStateChanged;
    }

    private async Task StartAsync(object? parameter, CancellationToken cancellationToken)
    {
        string[] selected = parameter as string[] ?? [];
        if (selected.Length == 0) throw new InvalidOperationException("请先选择至少一个任务。");
        foreach (var active in _scheduler.Instances.Where(item => item.PluginId == PluginId))
            await _scheduler.StopTaskAsync(active.InstanceId);
        _steps.Clear();
        _bus.SetState("demo.steps", Array.Empty<SchemaStep>());
        _bus.SetState("demo.selected_csv", string.Join(",", selected));
        _bus.SetState("demo.run_request", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await _scheduler.StartTaskAsync(PluginId, 0, cancellationToken);
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs change)
    {
        if (change.Key == "demo.completed_request")
        {
            _ = StopCompletedAsync();
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
            int index = _steps.FindIndex(item => item.Id == id);
            var update = new SchemaStep(id, title, status, null);
            if (index >= 0) _steps[index] = update;
            else _steps.Add(update);
            _bus.SetState("demo.steps", _steps.ToArray());
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
    }

    private async Task StopCompletedAsync()
    {
        try
        {
            foreach (var active in _scheduler.Instances.Where(item => item.PluginId == PluginId))
                await _scheduler.StopTaskAsync(active.InstanceId);
        }
        catch (Exception error) { _bus.SetState("demo.last_error", error.Message); }
    }

    public void Dispose()
    {
        _bus.StateChanged -= OnStateChanged;
        _subscription.Dispose();
    }
}
