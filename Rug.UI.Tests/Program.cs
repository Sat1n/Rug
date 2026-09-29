using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Schema;
using Rug.UI.Core.Services;
using Rug.UI.Core.Models;

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
var timelineNode = parser.Parse("""
    {"type":"StepFlowView","id":"custom-flow","stateKey":"custom.timeline",
     "steps":[{"id":"pending","title":"Default step","status":"Waiting"}]}
    """);

ExpectInvalid("{", "malformed JSON");
ExpectInvalid("{\"type\":\"Mystery\",\"id\":\"x\"}", "unknown type");
ExpectInvalid("{\"type\":\"Page\",\"id\":\"x\",\"children\":[{\"type\":\"ToggleCard\",\"id\":\"x\",\"stateKey\":\"flag\"}]}", "duplicate id");
ExpectInvalid("{\"type\":\"SliderCard\",\"id\":\"x\",\"stateKey\":\"n\",\"min\":10,\"max\":1}", "invalid range");
ExpectInvalid("{\"type\":\"SelectorBarGroup\",\"id\":\"x\",\"tabs\":[]}", "empty tabs");

var store = new StateStore();
using ICommandBus bus = new CommandBus(store);
var firstRun = StateScope.Instance("flow-plugin", Guid.NewGuid());
var secondRun = StateScope.Instance("flow-plugin", Guid.NewGuid());
bus.OpenInstance(firstRun);
bus.OpenInstance(secondRun);
var flowBinding = new StepFlowStateBinding(timelineNode.StateKey, timelineNode.Steps);
var firstSteps = new[] { new SchemaStep("a", "First run", "Running", null) };
var secondSteps = new[] { new SchemaStep("b", "Second run", "Succeeded", null) };
bus.SetState(firstRun, "custom.timeline", firstSteps);
bus.SetState(secondRun, "custom.timeline", secondSteps);
bus.SetState(firstRun, "demo.steps", secondSteps);
Check(flowBinding.Read(bus, firstRun).Single().Title == "First run" &&
      flowBinding.Read(bus, secondRun).Single().Title == "Second run",
    "non-demo timeline key did not follow the selected instance");
int timelineEvents = 0;
using var timelineSubscription = bus.SubscribeState(secondRun, (_, change) =>
{
    if (flowBinding.Matches(change, secondRun)) timelineEvents++;
});
bus.SetState(firstRun, "custom.timeline", secondSteps);
bus.SetState(secondRun, "demo.steps", firstSteps);
bus.SetState(secondRun, "custom.timeline", firstSteps);
Check(timelineEvents == 1 && flowBinding.Read(bus, secondRun).Single().Title == "First run",
    "timeline event crossed an instance or a different schema key");
var rebuiltBinding = new StepFlowStateBinding(timelineNode.StateKey, timelineNode.Steps);
Check(rebuiltBinding.Read(bus, secondRun).Single().Title == "First run",
    "rebuilt timeline did not read its instance state");
bus.CloseInstance(firstRun);
bus.CloseInstance(secondRun);
Check(rebuiltBinding.Read(bus, secondRun).Single().Title == "Default step" &&
      rebuiltBinding.Read(bus, secondRun,
          new Dictionary<string, IReadOnlyList<SchemaStep>>
          {
              ["custom.timeline"] = firstSteps
          }).Single().Title == "First run",
    "rebuilt timeline did not distinguish evicted state from an explicit display copy");
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
await HotReloadTests.RunAsync();
if (args.Contains("--e2e", StringComparer.Ordinal)) await E2ETests.RunAsync();
if (args.Contains("--instance-ui", StringComparer.Ordinal)) await E2ETests.RunInstanceUiAsync();
Console.WriteLine("Schema, state, and hot reload tests passed.");

void ExpectInvalid(string json, string reason)
{
    try { parser.Parse(json); throw new Exception($"Expected rejection: {reason}"); }
    catch (SchemaValidationException) { }
}

static void Check(bool value, string reason)
{
    if (!value) throw new Exception($"Failed: {reason}");
}
