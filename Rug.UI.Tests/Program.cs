using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Schema;
using Rug.UI.Core.Services;

var parser = new SchemaParser();
string file = Path.Combine(AppContext.BaseDirectory, "ui.json");
UISchemaNode page = await parser.ParseFileAsync(file);
Check(page.Type == "Page" && page.Children.Count == 1, "page root");
UISchemaNode tabs = page.Children[0];
Check(tabs.Type == "SelectorBarGroup" && tabs.Tabs.Count == 2, "selector tabs");
string[] expected = ["ToggleCard", "SelectCard", "SliderCard", "TextCard", "NumberCard",
    "ButtonCard", "InfoBanner", "PresetPickerCard", "TaskSelectorCard", "StepFlowView"];
var actual = tabs.Tabs.SelectMany(tab => tab.Children).Select(node => node.Type).ToHashSet();
Check(expected.All(actual.Contains), "complete control palette");
Check(tabs.Tabs[1].Children[1].Tasks.Count == 3, "task tree");
Check(tabs.Tabs[1].Children[2].Steps.Count == 2, "step timeline");

ExpectInvalid("{", "malformed JSON");
ExpectInvalid("{\"type\":\"Mystery\",\"id\":\"x\"}", "unknown type");
ExpectInvalid("{\"type\":\"Page\",\"id\":\"x\",\"children\":[{\"type\":\"ToggleCard\",\"id\":\"x\",\"stateKey\":\"flag\"}]}", "duplicate id");
ExpectInvalid("{\"type\":\"SliderCard\",\"id\":\"x\",\"stateKey\":\"n\",\"min\":10,\"max\":1}", "invalid range");
ExpectInvalid("{\"type\":\"SelectorBarGroup\",\"id\":\"x\",\"tabs\":[]}", "empty tabs");

var store = new StateStore();
using ICommandBus bus = new CommandBus(store);
var node = tabs.Tabs[0].Children.First(item => item.Type == "ToggleCard");
Check(node.StateKey == "auto_sell" && node.DefaultValue?.GetBoolean() == false, "toggle schema");
bus.SetState(node.StateKey!, true);
Check(store.GetState("auto_sell", false), "UI-to-store value");
store.SetState("auto_sell", false);
Check(!bus.GetState("auto_sell", true), "store-to-UI value");
int notices = 0;
bus.StateChanged += (_, change) =>
{
    if (change.Key == "auto_sell") notices++;
};
await Task.Run(() => store.SetState("auto_sell", true));
Check(notices == 1, "background state notification");
string? selectedTask = null;
using var subscription = bus.Subscribe("task.start_selected", (parameter, _) =>
{
    selectedTask = (parameter as string[])?.FirstOrDefault();
    return Task.CompletedTask;
});
await bus.ExecuteCommandAsync(tabs.Tabs[1].Children[1].Command!, new[] { "capture" });
Check(selectedTask == "capture", "schema command route");
Console.WriteLine("Schema and state tests passed.");

void ExpectInvalid(string json, string reason)
{
    try { parser.Parse(json); throw new Exception($"Expected rejection: {reason}"); }
    catch (SchemaValidationException) { }
}

static void Check(bool value, string reason)
{
    if (!value) throw new Exception($"Failed: {reason}");
}
