using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Controls;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Schema;

namespace Rug.UI.Services;

/// <summary>Creates native WinUI controls and owns their state subscriptions.</summary>
public sealed class UIFactory : IDisposable
{
    private readonly ICommandBus _bus;
    private readonly Dictionary<string, List<Action<object?>>> _bindings = new(StringComparer.Ordinal);
    private bool _disposed;

    public event EventHandler<Exception>? CommandFailed;

    public UIFactory(ICommandBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _bus.StateChanged += OnStateChanged;
    }

    public UIElement Build(UISchemaNode node) => node.Type switch
    {
        "Page" or "Group" or "Tab" => BuildGroup(node),
        "SelectorBarGroup" => BuildTabs(node),
        "InfoBanner" => new InfoBar
        {
            Title = node.Title, Message = node.Description, IsOpen = true,
            Severity = Enum.Parse<InfoBarSeverity>(node.Severity)
        },
        "TaskSelectorCard" => BuildTaskSelector(node),
        "StepFlowView" => BuildStepFlow(node),
        _ => BuildCard(node)
    };

    private UIElement BuildGroup(UISchemaNode node)
    {
        var stack = new StackPanel { Spacing = 12 };
        if (!string.IsNullOrEmpty(node.Title))
            stack.Children.Add(new TextBlock { Text = node.Title, FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 } });
        foreach (UISchemaNode child in node.Children) stack.Children.Add(Build(child));
        return stack;
    }

    private UIElement BuildTabs(UISchemaNode node)
    {
        var stack = new StackPanel { Spacing = 14 };
        if (!string.IsNullOrEmpty(node.Title))
            stack.Children.Add(new TextBlock { Text = node.Title, FontSize = 22 });
        var bar = new SelectorBar();
        var content = new ContentControl();
        var pages = new Dictionary<SelectorBarItem, UIElement>();
        foreach (UISchemaNode tab in node.Tabs)
        {
            var item = new SelectorBarItem { Text = tab.Title };
            bar.Items.Add(item);
            pages.Add(item, Build(tab));
        }
        bar.SelectionChanged += (_, _) =>
        {
            if (bar.SelectedItem is { } selected && pages.TryGetValue(selected, out UIElement? page))
                content.Content = page;
        };
        stack.Children.Add(bar);
        stack.Children.Add(content);
        bar.SelectedItem = bar.Items[0];
        content.Content = pages[bar.Items[0]];
        return stack;
    }

    private UIElement BuildCard(UISchemaNode node)
    {
        UIElement content = node.Type switch
        {
            "ToggleCard" => BuildToggle(node),
            "SelectCard" => BuildSelect(node),
            "SliderCard" => BuildSlider(node),
            "TextCard" => BuildText(node),
            "NumberCard" => BuildNumber(node),
            "ButtonCard" => BuildButton(node),
            "PresetPickerCard" => BuildPresetPicker(node),
            _ => throw new ArgumentOutOfRangeException(nameof(node.Type))
        };
        return new SettingsCard
        {
            Header = node.Title, Description = node.Description,
            Content = content, IsClickEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private UIElement BuildToggle(UISchemaNode node)
    {
        string key = node.StateKey!;
        var control = new ToggleSwitch { IsOn = State(key, node.DefaultValue?.GetBoolean() ?? false) };
        control.Toggled += (_, _) => _bus.SetState(key, control.IsOn);
        Bind(key, value => { if (value is bool current && control.IsOn != current) control.IsOn = current; });
        return control;
    }

    private UIElement BuildSelect(UISchemaNode node)
    {
        string key = node.StateKey!;
        var control = new ComboBox { MinWidth = 160, DisplayMemberPath = nameof(SchemaOption.Label) };
        foreach (SchemaOption option in node.Options) control.Items.Add(option);
        string selected = State(key, node.DefaultValue?.GetString() ?? node.Options[0].Key);
        control.SelectedItem = node.Options.FirstOrDefault(option => option.Key == selected) ?? node.Options[0];
        control.SelectionChanged += (_, _) =>
        {
            if (control.SelectedItem is SchemaOption option) _bus.SetState(key, option.Key);
        };
        Bind(key, value =>
        {
            if (value is string current)
                control.SelectedItem = node.Options.FirstOrDefault(option => option.Key == current);
        });
        return control;
    }

    private UIElement BuildSlider(UISchemaNode node)
    {
        string key = node.StateKey!;
        var control = new Slider
        {
            Minimum = node.Min!.Value, Maximum = node.Max!.Value,
            StepFrequency = node.Step ?? 1, Width = 220,
            Value = State(key, node.DefaultValue?.GetDouble() ?? node.Min.Value)
        };
        control.ValueChanged += (_, _) => _bus.SetState(key, control.Value);
        Bind(key, value =>
        {
            if (value is double number && Math.Abs(control.Value - number) > 0.000001)
                control.Value = number;
        });
        return control;
    }

    private UIElement BuildText(UISchemaNode node)
    {
        string key = node.StateKey!;
        var control = new TextBox { MinWidth = 220, Text = State(key, node.DefaultValue?.GetString() ?? "") };
        control.TextChanged += (_, _) => _bus.SetState(key, control.Text);
        Bind(key, value => { if (value is string text && control.Text != text) control.Text = text; });
        return control;
    }

    private UIElement BuildNumber(UISchemaNode node)
    {
        string key = node.StateKey!;
        var control = new NumberBox
        {
            Minimum = node.Min!.Value, Maximum = node.Max!.Value,
            SmallChange = node.Step ?? 1, Width = 160,
            Value = State(key, node.DefaultValue?.GetDouble() ?? node.Min.Value)
        };
        control.ValueChanged += (_, _) =>
        {
            if (!double.IsNaN(control.Value)) _bus.SetState(key, control.Value);
        };
        Bind(key, value =>
        {
            if (value is double number && Math.Abs(control.Value - number) > 0.000001)
                control.Value = number;
        });
        return control;
    }

    private UIElement BuildButton(UISchemaNode node)
    {
        var button = new Button { Content = node.Title, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        button.Click += async (_, _) => await DispatchAsync(node.Command!, JsonValue(node.CommandParameter));
        return button;
    }

    private UIElement BuildPresetPicker(UISchemaNode node)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(BuildSelect(node));
        if (node.OpenFolderCommand is { } folder)
        {
            var button = new Button { Content = "打开文件夹" };
            button.Click += async (_, _) => await DispatchAsync(folder);
            row.Children.Add(button);
        }
        if (node.ImportCommand is { } import)
        {
            var button = new Button { Content = "从剪贴板导入" };
            button.Click += async (_, _) =>
            {
                try
                {
                    string text = await Windows.ApplicationModel.DataTransfer.Clipboard.GetContent().GetTextAsync();
                    await DispatchAsync(import, text);
                }
                catch (Exception ex) { CommandFailed?.Invoke(this, ex); }
            };
            row.Children.Add(button);
        }
        return row;
    }

    private UIElement BuildTaskSelector(UISchemaNode node)
    {
        string key = node.StateKey!;
        var control = new TaskSelectorCard();
        control.SetTasks(node.Tasks);
        var defaults = node.DefaultValue?.EnumerateArray().Select(item => item.GetString()!).ToArray() ?? [];
        control.SetSelection(State(key, defaults));
        control.SelectionChanged += (_, ids) => _bus.SetState(key, ids);
        control.StartRequested += async (_, _) =>
        {
            if (node.Command is { } command) await DispatchAsync(command, control.SelectedIds);
        };
        Bind(key, value => { if (value is IEnumerable<string> ids) control.SetSelection(ids); });
        return new SettingsCard { Header = node.Title, Description = node.Description, Content = control };
    }

    private UIElement BuildStepFlow(UISchemaNode node)
    {
        var control = new StepFlowView();
        control.SetSteps(node.StateKey is { } existingKey
            ? _bus.GetState<IEnumerable<SchemaStep>>(existingKey, node.Steps)
            : node.Steps);
        if (node.StateKey is { } key)
            Bind(key, value => { if (value is IEnumerable<SchemaStep> steps) control.SetSteps(steps); });
        return new SettingsCard { Header = node.Title, Description = node.Description, Content = control };
    }

    private T State<T>(string key, T fallback)
    {
        object marker = new();
        object? existing = _bus.GetState<object?>(key, marker);
        if (ReferenceEquals(existing, marker))
        {
            _bus.SetState(key, fallback);
            return fallback;
        }
        return existing is T typed ? typed : throw new InvalidCastException($"State '{key}' has an incompatible type.");
    }

    private void Bind(string key, Action<object?> update)
    {
        if (!_bindings.TryGetValue(key, out var actions)) _bindings[key] = actions = [];
        actions.Add(update);
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        if (!_disposed && _bindings.TryGetValue(e.Key, out var actions))
            foreach (var action in actions) action(e.Value);
    }

    private async Task DispatchAsync(string command, object? parameter = null)
    {
        try { await _bus.ExecuteCommandAsync(command, parameter); }
        catch (Exception ex) { CommandFailed?.Invoke(this, ex); }
    }

    private static object? JsonValue(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString(),
        JsonValueKind.Number => value.Value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value?.GetRawText()
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bus.StateChanged -= OnStateChanged;
        _bindings.Clear();
    }
}
