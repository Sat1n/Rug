using System.Diagnostics;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Exceptions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Rug.UI.Core.Security;
using RugTaskScheduler = Rug.UI.Core.Services.TaskScheduler;

string path = Path.Combine(AppContext.BaseDirectory, "bridge_test.lua");
await File.WriteAllTextAsync(path, "function on_tick() rug.sleep(200); local frame = rug.capture(); assert(frame.width == 1 and frame.height == 1); rug.log('done') end");
var capture = new FakeCapture();
await using (var runtime = new LuaRuntime(capture, new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance))
{
    var states = new List<ScriptState>();
    runtime.StateChanged += (_, e) => states.Add(e.Current);
    await runtime.InitializeAsync(path, new PluginManifest(new HashSet<string> { "timer", "vision.capture" }));
    var watch = Stopwatch.StartNew();
    Check((await runtime.StepAsync()).State == ScriptState.Yielded, "sleep must yield");
    Check(watch.ElapsedMilliseconds < 150, "StepAsync blocked on sleep");
    Check((await runtime.ResumeAsync()).State == ScriptState.Yielded, "capture must yield");
    Task<ScriptExecutionResult> pending = runtime.ResumeAsync();
    await Task.Delay(30);
    Check(!pending.IsCompleted, "capture must not block or complete early");
    capture.Complete();
    Check((await pending).State == ScriptState.Stopped, "script must finish");
    Check(watch.ElapsedMilliseconds >= 200 && watch.ElapsedMilliseconds < 2000, "unexpected elapsed time");
    Check(states.Contains(ScriptState.Running) && states.Count(s => s == ScriptState.Yielded) == 2 && states[^1] == ScriptState.Stopped, "bad state sequence");
}

await File.WriteAllTextAsync(path, "function on_tick() rug.sleep(5000) end");
await using (var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance))
{
    await runtime.InitializeAsync(path, new PluginManifest(new HashSet<string> { "timer" }));
    Check((await runtime.StepAsync()).State == ScriptState.Yielded, "long sleep must yield");
    Task<ScriptExecutionResult> pending = runtime.ResumeAsync();
    runtime.Stop();
    Check((await pending.WaitAsync(TimeSpan.FromSeconds(1))).State == ScriptState.Stopped, "stop must cancel pending sleep");
}

await File.WriteAllTextAsync(path, "function on_tick() local f = rug.capture(); assert(f.width == 1); local lines = rug.ocr('winrt'); assert(#lines == 1 and lines[1].text == 'ready') end");
var readyCapture = new FakeCapture();
readyCapture.Complete();
await using (var runtime = new LuaRuntime(readyCapture, new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance))
{
    await runtime.InitializeAsync(path, new PluginManifest(new HashSet<string> { "vision.capture", "vision.ocr" }));
    Check((await runtime.StepAsync()).State == ScriptState.Yielded, "capture must yield");
    Check((await runtime.ResumeAsync()).State == ScriptState.Yielded, "OCR must yield");
    Check((await runtime.ResumeAsync()).State == ScriptState.Stopped, "OCR result must reach Lua");
}

await using (var runtime = new LuaRuntime(readyCapture, new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance))
{
    await runtime.InitializeAsync(path, new PluginManifest(new HashSet<string>()));
    ScriptExecutionResult denied = await runtime.StepAsync();
    Check(denied.State == ScriptState.Faulted && denied.Error is PermissionDeniedException { Permission: Permission.VisionCapture }, "capture permission must be enforced");
}

foreach ((string call, string permission) in new[]
{
    ("rug.sleep(1)", Permission.Timer),
    ("rug.capture()", Permission.VisionCapture),
    ("rug.ocr('winrt')", Permission.VisionOcr),
    ("rug.click(1, 2)", Permission.ControlInput),
    ("rug.press_key(65)", Permission.ControlInput),
    ("rug.agent.resolve_anomaly('denied', 'detail')", Permission.Agent)
})
{
    await File.WriteAllTextAsync(path, "function on_tick() " + call + " end");
    var deniedCapture = new FakeCapture();
    await using var runtime = new LuaRuntime(deniedCapture, new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance);
    await runtime.InitializeAsync(path, new PluginManifest([]) { Id = "denied-test" });
    ScriptExecutionResult result = await runtime.StepAsync();
    Check(result.State == ScriptState.Faulted && result.Error is PermissionDeniedException ex &&
          ex.Permission == permission && ex.Api == "rug." + call.Split('(')[0][4..],
          "permission mismatch for " + call);
    Check(deniedCapture.GrabCalls == 0, "denied call reached capture service");
}

await File.WriteAllTextAsync(path, "function on_tick() rug.agent.resolve_anomaly('pending', 'detail'); rug.log('unreachable') end");
var anomalyGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance))
{
    runtime.ConfigureAnomalyHandler((_, _) => anomalyGate.Task);
    await runtime.InitializeAsync(path, new PluginManifest([Permission.Agent]));
    Check((await runtime.StepAsync()).State == ScriptState.Yielded, "agent hook must yield before persistence finishes");
    Task<ScriptExecutionResult> pending = runtime.ResumeAsync();
    await Task.Delay(20);
    Check(!pending.IsCompleted, "agent hook blocked the caller or resumed before persistence");
    anomalyGate.SetResult("incident.json");
    ScriptExecutionResult result = await pending;
    Check(result.State == ScriptState.Faulted && Equals(result.Value, "incident.json") &&
          result.Error?.Message.Contains("incident.json") == true,
          "agent hook must fault after returning the incident record path");
}

await File.WriteAllTextAsync(path, "function on_tick() rug.sleep(10); rug.log('resumed') end");
await using (var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance))
{
    await runtime.InitializeAsync(path, new PluginManifest(new HashSet<string> { "timer" }));
    Check((await runtime.StepAsync()).State == ScriptState.Yielded, "pause fixture must yield");
    runtime.Pause();
    await Task.Delay(20);
    Check((await runtime.ResumeAsync()).State == ScriptState.Paused, "paused runtime must not advance");
    runtime.Resume();
    Check((await runtime.ResumeAsync()).State == ScriptState.Stopped, "completed operation must survive pause");
}

using (var window = new TestWindow())
{
    var mapper = new CoordinateMapper();
    PointInt origin = window.ClientOrigin;
    Check(mapper.ClientToScreen(window.Handle, 100, 100) == new PointInt(origin.X + 100, origin.Y + 100),
          "Win32 client-to-screen mapping changed the physical pixel offset");
    try { mapper.ClientToScreen(window.Handle, -1, 100); throw new Exception("negative client point accepted"); }
    catch (ArgumentOutOfRangeException) { }
    try { mapper.ClientToScreen(window.Handle, window.ClientWidth, 0); throw new Exception("outside client point accepted"); }
    catch (ArgumentOutOfRangeException) { }
}

var fakeMapper = new FakeCoordinateMapper();
var inputAudit = new AuditLogger();
var deniedInput = new FakeInput();
await using (var bridge = new InputBridge(deniedInput,
    new PermissionInterceptor(new PluginManifest([]) { Id = "input-denied" }, inputAudit), fakeMapper))
{
    try { await bridge.ClickAsync(100, 100); throw new Exception("ungranted input was dispatched"); }
    catch (PermissionDeniedException ex)
    {
        Check(ex.Permission == Permission.ControlInput && ex.Api == "rug.click" &&
              deniedInput.TargetModes.Count == 0 && inputAudit.LastLevel == LogLevel.Warning,
              "input bridge did not gate unauthorized calls");
    }
}

var bridgedInput = new FakeInput();
await using (var bridge = new InputBridge(bridgedInput,
    new PermissionInterceptor(new PluginManifest([Permission.ControlInput]), NullLogger.Instance),
    fakeMapper, _ => true))
{
    await bridge.BindAsync(123, InputDeliveryMode.Win32PostMessage);
    await bridge.ClickAsync(100, 100, MouseButton.Right, InputDeliveryMode.Win32SendInput);
    Check(bridge.LastMappedScreenPoint == new PointInt(1100, 2100) &&
          bridgedInput.X == 100 && bridgedInput.Y == 100 && bridgedInput.Button == MouseButton.Right &&
          bridgedInput.TargetModes.SequenceEqual([true, false]),
          "input bridge lost the client point, button or foreground mode");
    var watch = Stopwatch.StartNew();
    Task held = bridge.PressKeyAsync(65, 80);
    Check(!held.IsCompleted && bridgedInput.KeyDownCalls == 1 && bridgedInput.KeyUpCalls == 0,
          "key hold blocked the caller or did not remain pressed");
    await held;
    Check(watch.ElapsedMilliseconds >= 80 && bridgedInput.KeyUpCalls == 1 &&
          bridgedInput.TargetModes.SequenceEqual([true, false, true]),
          "key release or default background mode restoration failed");
    using var canceled = new CancellationTokenSource();
    Task longHold = bridge.PressKeyAsync(65, 5000, canceled.Token);
    await WaitUntilAsync(() => bridgedInput.KeyDownCalls == 2);
    canceled.Cancel();
    try { await longHold; throw new Exception("canceled hold completed normally"); }
    catch (OperationCanceledException) { }
    Check(bridgedInput.KeyUpCalls == 2, "cancellation left the key pressed");
}

var unfocusedInput = new FakeInput();
await using (var bridge = new InputBridge(unfocusedInput,
    new PermissionInterceptor(new PluginManifest([Permission.ControlInput]), NullLogger.Instance),
    fakeMapper, _ => false))
{
    await bridge.BindAsync(123, InputDeliveryMode.Win32PostMessage);
    await bridge.ClickAsync(100, 100);
    Check(unfocusedInput.MoveCalls == 1, "background click unexpectedly needed foreground focus");
    try { await bridge.ClickAsync(-1, 0); throw new Exception("invalid client point accepted"); }
    catch (ArgumentOutOfRangeException) { }
    try
    {
        await bridge.ClickAsync(100, 100, mode: InputDeliveryMode.Win32SendInput);
        throw new Exception("unfocused SendInput was dispatched");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("foreground")) { }
    Check(unfocusedInput.MoveCalls == 1,
          "invalid point or unfocused window reached native input");
}

await File.WriteAllTextAsync(path, "function on_tick() rug.click(12, 34, 'right', 'sendinput'); rug.press_key(65, 40) end");
var input = new FakeInput();
await using (var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), input,
    NullLogger<LuaRuntime>.Instance, fakeMapper, _ => true))
{
    await runtime.InitializeAsync(path, new PluginManifest(new HashSet<string> { "input" }, TargetWindow: 123));
    Check((await runtime.StepAsync()).State == ScriptState.Yielded, "click must yield");
    Check((await runtime.ResumeAsync()).State == ScriptState.Yielded, "key must yield");
    Check((await runtime.ResumeAsync()).State == ScriptState.Stopped, "input script must finish");
    Check(input.Target == 123 && input.X == 12 && input.Y == 34 && input.Key == 65 &&
          input.Button == MouseButton.Right && input.KeyDownCalls == 1 && input.KeyUpCalls == 1 &&
          input.TargetModes.SequenceEqual([true, false, true]),
          "Lua input bridge lost button, mode or key duration");
}

await File.WriteAllTextAsync(path, "function on_tick() rug.press_key(66) end");
var manifestInput = new FakeInput();
await using (var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), manifestInput,
    NullLogger<LuaRuntime>.Instance, fakeMapper, _ => true))
{
    await runtime.InitializeAsync(path, new PluginManifest([Permission.ControlInput], TargetWindow: 123)
    {
        InputDelivery = InputDeliveryMode.Win32SendInput
    });
    Check(manifestInput.TargetModes.SequenceEqual([false]), "manifest SendInput default was ignored");
    Check((await runtime.StepAsync()).State == ScriptState.Yielded &&
          (await runtime.ResumeAsync()).State == ScriptState.Stopped && manifestInput.Key == 66,
          "manifest-selected input mode did not execute");
}

await File.WriteAllTextAsync(path, "assert(os == nil and io == nil and package == nil and debug == nil and luanet == nil and load == nil)");
await using (var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance))
{
    await runtime.InitializeAsync(path, new PluginManifest([]));
    Check((await runtime.StepAsync()).State == ScriptState.Stopped, "dangerous Lua globals must be absent");
}

string pluginsRoot = Path.Combine(AppContext.BaseDirectory, "plugin-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(pluginsRoot);
try
{
    string valid = Path.Combine(pluginsRoot, "valid");
    Directory.CreateDirectory(valid);
    await File.WriteAllTextAsync(Path.Combine(valid, "main.lua"), "function on_tick() assert(rug.get_config('enabled') == true and rug.get_config('label') == 'hello' and rug.get_config('speed') == 2.5 and rug.get_config('mode') == 'fast') end");
    await File.WriteAllTextAsync(Path.Combine(valid, "manifest.json"), """
        {"id":"demo","name":"Demo","version":"1.2.3","targetProcess":"Demo.exe",
         "permissions":["timer","vision.capture"],"inputDelivery":"Win32SendInput",
         "targetWindow":987,"configuration":{"injected":"yes"}}
        """);
    await File.WriteAllTextAsync(Path.Combine(valid, "config.json"), """
        {"fields":[
          {"key":"enabled","label":"Enabled","type":"CheckBox","default":true},
          {"key":"label","label":"Label","type":"TextBox","default":"hello"},
          {"key":"speed","label":"Speed","type":"Slider","default":2.5,"min":0,"max":5,"step":0.5},
          {"key":"mode","label":"Mode","type":"ComboBox","default":"fast","options":["slow","fast"]}
        ]}
        """);

    string malformed = Path.Combine(pluginsRoot, "malformed");
    Directory.CreateDirectory(malformed);
    await File.WriteAllTextAsync(Path.Combine(malformed, "manifest.json"), "{bad json");

    string traversal = Path.Combine(pluginsRoot, "traversal");
    Directory.CreateDirectory(traversal);
    await File.WriteAllTextAsync(Path.Combine(traversal, "manifest.json"), """
        {"id":"escape","name":"Escape","version":"1.0.0","targetProcess":"Demo.exe",
         "entry":"../valid/main.lua","permissions":[]}
        """);

    string badConfig = Path.Combine(pluginsRoot, "bad-config");
    Directory.CreateDirectory(badConfig);
    await File.WriteAllTextAsync(Path.Combine(badConfig, "main.lua"), "function on_tick() end");
    await File.WriteAllTextAsync(Path.Combine(badConfig, "manifest.json"), """
        {"id":"bad-config","name":"Bad Config","version":"1.0.0","targetProcess":"Demo.exe","permissions":[]}
        """);
    await File.WriteAllTextAsync(Path.Combine(badConfig, "config.json"), "{broken");

    string badDefault = Path.Combine(pluginsRoot, "bad-default");
    Directory.CreateDirectory(badDefault);
    await File.WriteAllTextAsync(Path.Combine(badDefault, "main.lua"), "function on_tick() end");
    await File.WriteAllTextAsync(Path.Combine(badDefault, "manifest.json"), """
        {"id":"bad-default","name":"Bad Default","version":"1.0.0","targetProcess":"Demo.exe","permissions":[]}
        """);
    await File.WriteAllTextAsync(Path.Combine(badDefault, "config.json"), """
        {"fields":[{"key":"enabled","type":"CheckBox","default":"yes"}]}
        """);

    string badMode = Path.Combine(pluginsRoot, "bad-mode");
    Directory.CreateDirectory(badMode);
    await File.WriteAllTextAsync(Path.Combine(badMode, "main.lua"), "function on_tick() end");
    await File.WriteAllTextAsync(Path.Combine(badMode, "manifest.json"), """
        {"id":"bad-mode","name":"Bad Mode","version":"1.0.0","targetProcess":"Demo.exe",
         "permissions":[],"inputDelivery":999}
        """);

    var manager = new PluginManager(NullLogger<PluginManager>.Instance);
    PluginManifest[] plugins = manager.ScanPlugins(pluginsRoot).ToArray();
    Check(plugins.Length == 1 && plugins[0].Id == "demo" && plugins[0].Entry == "main.lua" &&
          plugins[0].Permissions.SequenceEqual(new[] { "timer", "vision.capture" }) &&
          plugins[0].InputDelivery == InputDeliveryMode.Win32SendInput &&
          plugins[0].TargetWindow == 0 && !plugins[0].Configuration!.ContainsKey("injected"), "manifest scan failed");
    PluginConfig config = manager.GetPluginConfig("demo");
    Check(config.Schema.Fields.Count == 4 && config.Schema.Fields[0].Type == PluginConfigControlType.CheckBox &&
          Equals(config.Defaults["enabled"], true) && Equals(config.Defaults["label"], "hello") &&
          Equals(config.Defaults["speed"], 2.5d) && Equals(config.Defaults["mode"], "fast"), "config schema/default parsing failed");
    await using (var configRuntime = new LuaRuntime(new FakeCapture(), new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance))
    {
        await configRuntime.InitializeAsync(Path.Combine(valid, plugins[0].Entry), plugins[0]);
        Check((await configRuntime.StepAsync()).State == ScriptState.Stopped, "typed config defaults did not reach Lua");
    }
    Check(!plugins.Any(p => p.Id == "escape"), "entry traversal was accepted");
    Check(!plugins.Any(p => p.Id == "bad-config"), "malformed config was accepted");
    Check(!plugins.Any(p => p.Id == "bad-default"), "invalid config default was accepted");
    Check(!plugins.Any(p => p.Id == "bad-mode"), "invalid input delivery mode was accepted");
}
finally { Directory.Delete(pluginsRoot, recursive: true); }

var audit = new AuditLogger();
var gate = new PermissionInterceptor(new PluginManifest([Permission.Timer]) { Id = "audit-demo" }, audit);
gate.Demand(Permission.Timer, "rug.sleep");
try
{
    gate.Demand(Permission.Network, "rug.fetch");
    throw new Exception("network permission was not denied");
}
catch (PermissionDeniedException ex)
{
    Check(ex.PluginId == "audit-demo" && ex.Permission == Permission.Network && ex.Api == "rug.fetch" &&
          audit.LastLevel == LogLevel.Warning && audit.LastMessage!.Contains("audit-demo"), "permission audit details missing");
}

await File.WriteAllTextAsync(path, "while true do end");
var watchdogRuntime = new LuaRuntime(new FakeCapture(), new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance);
await watchdogRuntime.InitializeAsync(path, new PluginManifest([]));
Task<ScriptExecutionResult> spinning = watchdogRuntime.StepAsync();
await Task.Delay(30);
watchdogRuntime.Stop();
try
{
    Check((await spinning.WaitAsync(TimeSpan.FromSeconds(1))).State == ScriptState.Stopped,
        "watchdog did not stop a non-yielding Lua loop");
}
catch (TimeoutException)
{
    Environment.FailFast("watchdog did not interrupt a non-yielding Lua loop");
}
await watchdogRuntime.DisposeAsync();

string schedulerRoot = Path.Combine(AppContext.BaseDirectory, "scheduler-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(schedulerRoot);
try
{
    await WritePluginAsync("life", """
        function on_init() local frame = rug.capture(); assert(frame.width == 1); rug.log('life_init') end
        function on_tick() rug.log('life_tick') end
        function on_stop() rug.log('life_stop') end
        """, "\"vision.capture\"");
    await WritePluginAsync("other", """
        function on_init() rug.log('other_init') end
        function on_tick() rug.log('other_tick') end
        function on_stop() rug.log('other_stop') end
        """);
    await WritePluginAsync("spin", """
        function on_init() rug.log('spin_init') end
        function on_tick()
          __rug_should_abort = function() return false end
          if coroutine then coroutine.resume = function() return true end end
          error = function() end
          while true do end
        end
        function on_stop() rug.log('spin_stop') end
        """);
    await WritePluginAsync("spin-init", """
        function on_init() while true do end end
        function on_stop() rug.log('spin_init_stop') end
        """);
    await WritePluginAsync("bad-stop", """
        function on_tick() rug.log('bad_stop_tick') end
        function on_stop() while true do end end
        """);
    await WritePluginAsync("anomaly", """
        function on_tick()
          rug.sleep(50)
          rug.agent.resolve_anomaly('界面未响应', '超时3秒')
          rug.log('must_not_resume')
        end
        function on_stop() rug.log('anomaly_stop') end
        """, "\"timer\",\"agent\"");

    var events = new ConcurrentQueue<string>();
    var captures = new ConcurrentBag<FakeCapture>();
    var inputs = new ConcurrentBag<FakeInput>();
    var luaLogger = new RecordingLogger<LuaRuntime>(events);
    string anomalyDirectory = Path.Combine(schedulerRoot, "logs", "anomalies");
    var anomalyLogger = new AnomalyLogger(new RecordingLogger<AnomalyLogger>(events), anomalyDirectory);
    await using var scheduler = new RugTaskScheduler(
        new PluginManager(NullLogger<PluginManager>.Instance), schedulerRoot,
        () => { var c = new FakeCapture(events); captures.Add(c); return c; },
        () => { var i = new FakeInput(); inputs.Add(i); return i; },
        (c, i) => new LuaRuntime(c, new FakeOcr(), i, luaLogger),
        NullLogger<RugTaskScheduler>.Instance, anomalyLogger, TimeSpan.FromMilliseconds(20));

    Guid life = await scheduler.StartTaskAsync("life", 101);
    TaskExecutionContext lifeContext = scheduler.Instances.Single(c => c.InstanceId == life);
    await WaitUntilAsync(() => events.Any(e => e == "Lua: life_tick"));
    string[] firstEvents = events.ToArray();
    Check(Array.FindIndex(firstEvents, e => e == "capture-start:101") < Array.FindIndex(firstEvents, e => e == "Lua: life_init") &&
          Array.FindIndex(firstEvents, e => e == "Lua: life_init") < Array.FindIndex(firstEvents, e => e == "Lua: life_tick"),
          "capture/on_init/on_tick order is wrong");
    Check(scheduler.PauseTask(life), "pause failed");
    await Task.Delay(60);
    int pausedTicks = events.Count(e => e == "Lua: life_tick");
    await Task.Delay(80);
    Check(events.Count(e => e == "Lua: life_tick") == pausedTicks, "paused instance kept ticking");
    Check(scheduler.ResumeTask(life), "resume failed");
    await WaitUntilAsync(() => events.Count(e => e == "Lua: life_tick") > pausedTicks);
    await scheduler.StopTaskAsync(life);
    Check(lifeContext.TaskStatus == AutomationTaskStatus.Stopped && events.Contains("Lua: life_stop") &&
          events.Contains("capture-stop:101"), "on_stop or capture cleanup missing");

    Guid[] concurrent = await Task.WhenAll(
        scheduler.StartTaskAsync("life", 201),
        scheduler.StartTaskAsync("other", 202),
        scheduler.StartTaskAsync("life", 203));
    Check(concurrent.Distinct().Count() == 3 && scheduler.Instances.Count == 3, "instances were not isolated");
    await WaitUntilAsync(() => events.Contains("Lua: other_tick") && captures.Count(c => c.StartedHwnd is 201 or 202 or 203) == 3);
    Guid other = concurrent[1];
    Check(scheduler.PauseTask(other), "independent pause failed");
    await Task.Delay(60);
    int otherTicks = events.Count(e => e == "Lua: other_tick");
    int lifeTicks = events.Count(e => e == "Lua: life_tick");
    await Task.Delay(80);
    Check(events.Count(e => e == "Lua: other_tick") == otherTicks &&
          events.Count(e => e == "Lua: life_tick") > lifeTicks, "pause affected another instance");
    await scheduler.StopTaskAsync(concurrent[0]);
    int survivingTicks = events.Count(e => e == "Lua: life_tick");
    await WaitUntilAsync(() => events.Count(e => e == "Lua: life_tick") > survivingTicks);
    Check(scheduler.Instances.Count == 2, "stopping one instance affected the others");
    await Task.WhenAll(concurrent.Skip(1).Select(scheduler.StopTaskAsync));
    Check(scheduler.Instances.Count == 0 && captures.All(c => c.StopCalls == 1), "concurrent sessions leaked");

    Guid spin = await scheduler.StartTaskAsync("spin", 303);
    TaskExecutionContext spinContext = scheduler.Instances.Single(c => c.InstanceId == spin);
    await WaitUntilAsync(() => spinContext.Runtime.State == ScriptState.Running);
    var stopWatch = Stopwatch.StartNew();
    Task stopping = scheduler.StopTaskAsync(spin);
    Check(stopWatch.ElapsedMilliseconds < 100, "StopTaskAsync blocked its caller");
    await stopping.WaitAsync(TimeSpan.FromSeconds(2));
    Check(spinContext.TaskStatus == AutomationTaskStatus.Stopped && events.Contains("Lua: spin_stop") &&
          events.Contains("capture-stop:303"), "watchdog stop did not clean up");

    Guid spinInit = await scheduler.StartTaskAsync("spin-init", 304);
    TaskExecutionContext spinInitContext = scheduler.Instances.Single(c => c.InstanceId == spinInit);
    await WaitUntilAsync(() => spinInitContext.Runtime.State == ScriptState.Running);
    await scheduler.StopTaskAsync(spinInit).WaitAsync(TimeSpan.FromSeconds(2));
    Check(spinInitContext.TaskStatus == AutomationTaskStatus.Stopped && events.Contains("Lua: spin_init_stop") &&
          events.Contains("capture-stop:304"), "on_init loop prevented stop/cleanup");

    Guid badStop = await scheduler.StartTaskAsync("bad-stop", 305);
    TaskExecutionContext badStopContext = scheduler.Instances.Single(c => c.InstanceId == badStop);
    await WaitUntilAsync(() => events.Contains("Lua: bad_stop_tick"));
    await scheduler.StopTaskAsync(badStop).WaitAsync(TimeSpan.FromSeconds(3));
    Check(badStopContext.TaskStatus == AutomationTaskStatus.Faulted && badStopContext.LastError is not null &&
          events.Contains("capture-stop:305"), "on_stop watchdog did not release capture");

    Guid anomaly = await scheduler.StartTaskAsync("anomaly", 306);
    TaskExecutionContext anomalyContext = scheduler.Instances.Single(c => c.InstanceId == anomaly);
    await WaitUntilAsync(() => anomalyContext.TaskStatus == AutomationTaskStatus.Faulted &&
        Directory.Exists(anomalyDirectory) && Directory.GetFiles(anomalyDirectory, "*.json").Length == 1);
    await WaitUntilAsync(() => !scheduler.Instances.Any(c => c.InstanceId == anomaly));
    string jsonPath = Directory.GetFiles(anomalyDirectory, "*.json").Single();
    using (JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath)))
    {
        JsonElement record = document.RootElement;
        Check(record.GetProperty("pluginId").GetString() == "anomaly" &&
              record.GetProperty("instanceId").GetGuid() == anomaly &&
              record.GetProperty("reason").GetString() == "界面未响应" &&
              record.GetProperty("timestamp").GetDateTime().Kind == DateTimeKind.Utc &&
              record.GetProperty("scriptContext").GetString()!.Contains("超时3秒") &&
              record.GetProperty("scriptContext").GetString()!.Contains("on_tick") &&
              record.GetProperty("agent_resolution").ValueKind == JsonValueKind.Null,
              "anomaly metadata is incomplete");
        string screenshotName = record.GetProperty("screenshotPath").GetString()!;
        Check(Path.GetFileName(screenshotName) == screenshotName &&
              Path.GetFileNameWithoutExtension(screenshotName) == Path.GetFileNameWithoutExtension(jsonPath),
              "anomaly screenshot path must be relative and share the JSON basename");
        byte[] png = await File.ReadAllBytesAsync(Path.Combine(anomalyDirectory, screenshotName));
        Check(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
              BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) == 1 &&
              BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) == 1, "invalid anomaly PNG");
        int dataLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33, 4));
        using var compressed = new MemoryStream(png, 41, dataLength);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var pixels = new MemoryStream();
        await zlib.CopyToAsync(pixels);
        Check(pixels.ToArray().SequenceEqual(new byte[] { 0, 30, 20, 10, 255 }),
              "anomaly PNG did not encode the captured BGRA pixel");
    }
    Check(anomalyContext.LastError?.Message.Contains(jsonPath) == true &&
          events.Any(e => e.Contains("[Anomaly Detected]")) &&
          events.Contains("Lua: anomaly_stop") && !events.Contains("Lua: must_not_resume") &&
          events.Contains("capture-stop:306"), "anomaly did not fault and clean up safely");

    var delayedCapture = new FakeCapture
    {
        StartGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
    };
    var delayedScheduler = new RugTaskScheduler(
        new PluginManager(NullLogger<PluginManager>.Instance), schedulerRoot,
        () => delayedCapture, () => new FakeInput(),
        (c, i) => new LuaRuntime(c, new FakeOcr(), i, luaLogger),
        NullLogger<RugTaskScheduler>.Instance, anomalyLogger);
    Task<Guid> opening = delayedScheduler.StartTaskAsync("life", 404);
    await WaitUntilAsync(() => delayedCapture.StartCalls == 1);
    await delayedScheduler.DisposeAsync();
    try { await opening; throw new Exception("disposed scheduler accepted a pending start"); }
    catch (OperationCanceledException) { }
    Check(delayedCapture.StopCalls == 1 && delayedCapture.Disposed, "pending start leaked capture");

    async Task WritePluginAsync(string id, string script, string permissions = "")
    {
        string directory = Path.Combine(schedulerRoot, id);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
            "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"version\":\"1.0.0\",\"targetProcess\":\"Demo.exe\",\"permissions\":[" + permissions + "]}");
        await File.WriteAllTextAsync(Path.Combine(directory, "main.lua"), script);
    }
}
finally { Directory.Delete(schedulerRoot, recursive: true); }

Console.WriteLine("Lua bridge and scheduler tests passed");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task WaitUntilAsync(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    while (!condition())
    {
        await Task.Delay(10, timeout.Token);
    }
}

sealed class FakeCapture : ICaptureService
{
    private readonly TaskCompletionSource<CapturedFrame?> _frame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string>? _events;
    public FakeCapture(ConcurrentQueue<string>? events = null) => _events = events;
    public nint StartedHwnd { get; private set; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public bool Disposed { get; private set; }
    public TaskCompletionSource<bool>? StartGate { get; init; }
    public int GrabCalls { get; private set; }
    public bool IsCapturing => true;
    public async Task StartAsync(nint hwnd, CancellationToken cancellationToken = default)
    {
        StartCalls++;
        if (StartGate is not null) await StartGate.Task.WaitAsync(cancellationToken);
        StartedHwnd = hwnd;
        _events?.Enqueue("capture-start:" + hwnd);
        if (_events is not null) Complete();
    }
    public Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default)
    { GrabCalls++; return _frame.Task.WaitAsync(cancellationToken); }
    public Task StopAsync()
    { StopCalls++; _events?.Enqueue("capture-stop:" + StartedHwnd); return Task.CompletedTask; }
    public ValueTask DisposeAsync()
    { Disposed = true; return ValueTask.CompletedTask; }
    public void Complete() => _frame.SetResult(new CapturedFrame([10, 20, 30, 255], 1, 1, 4));
}

sealed class FakeOcr : IOcrService
{
    public Task<IReadOnlyList<OcrTextBlock>> RecognizeAsync(string imagePath, OcrEngineType engineType, string? modelId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OcrTextBlock>>([]);
    public Task<IReadOnlyList<OcrTextBlock>> RecognizeFrameAsync(CapturedFrame frame, OcrEngineType engineType, string? modelId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OcrTextBlock>>([new("ready", 1, new Rect(0, 0, 1, 1))]);
    public IReadOnlyList<string> ListPaddleModelIds() => [];
}

sealed class FakeInput : IInputService
{
    public nint Target { get; private set; }
    public List<bool> TargetModes { get; } = [];
    public MouseButton Button { get; private set; }
    public int KeyDownCalls { get; private set; }
    public int KeyUpCalls { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int MoveCalls { get; private set; }
    public int Key { get; private set; }
    public Task MoveMouseAsync(int x, int y, TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true, CancellationToken cancellationToken = default)
    { X = x; Y = y; MoveCalls++; return Task.CompletedTask; }
    public Task MoveMouseRelativeAsync(int dx, int dy, TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClickAsync(MouseButton button = MouseButton.Left, int holdMs = 0, CancellationToken cancellationToken = default)
    { Button = button; return Task.CompletedTask; }
    public Task MouseDownAsync(MouseButton button, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task MouseUpAsync(MouseButton button, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DragAndDropAsync(int sx, int sy, int ex, int ey, TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task KeyDownAsync(int virtualKey, CancellationToken cancellationToken = default)
    { Key = virtualKey; KeyDownCalls++; return Task.CompletedTask; }
    public Task KeyUpAsync(int virtualKey, CancellationToken cancellationToken = default)
    { KeyUpCalls++; return Task.CompletedTask; }
    public Task KeyPressAsync(int virtualKey, int holdMs = 0, CancellationToken cancellationToken = default)
    { Key = virtualKey; return Task.CompletedTask; }
    public Task SendTextAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetTargetAsync(nint hwnd, bool background, CancellationToken cancellationToken = default)
    { Target = hwnd; TargetModes.Add(background); return Task.CompletedTask; }
    public Task SetHumanizeConfigAsync(HumanizeConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<TrajectorySample>> PlanTrajectoryAsync(int sx, int sy, int ex, int ey, TrajectoryType trajectory = TrajectoryType.CubicBezier, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TrajectorySample>>([]);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeCoordinateMapper : ICoordinateMapper
{
    public PointInt ClientToScreen(nint hwnd, int x, int y)
    {
        if (hwnd != 123 || x < 0 || y < 0 || x >= 400 || y >= 300)
            throw new ArgumentOutOfRangeException(nameof(x));
        return new PointInt(x + 1000, y + 2000);
    }
}

sealed class TestWindow : IDisposable
{
    public nint Handle { get; }
    public int ClientWidth { get; }
    public PointInt ClientOrigin { get; }

    public TestWindow()
    {
        Handle = CreateWindowExW(0, "STATIC", "Rug Coordinate Test", 0x00CF0000,
            80, 90, 400, 300, 0, 0, 0, 0);
        if (Handle == 0) throw new Exception("Could not create a Win32 coordinate test window.");
        if (!GetClientRect(Handle, out Rect rect)) throw new Exception("Could not read test client area.");
        ClientWidth = rect.Right - rect.Left;
        var point = new WinPoint();
        if (MapWindowPoints(Handle, 0, ref point, 1) == 0 && Marshal.GetLastWin32Error() != 0)
            throw new Exception("Could not map the test window origin.");
        ClientOrigin = new PointInt(point.X, point.Y);
    }

    public void Dispose() => DestroyWindow(Handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int MapWindowPoints(nint from, nint to, ref WinPoint point, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);
}

sealed class AuditLogger : ILogger
{
    public LogLevel? LastLevel { get; private set; }
    public string? LastMessage { get; private set; }
    public IDisposable BeginScope<TState>(TState state) => NoopScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        LastLevel = logLevel;
        LastMessage = formatter(state, exception);
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}

sealed class RecordingLogger<T>(ConcurrentQueue<string> events) : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) => NoopScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => events.Enqueue(formatter(state, exception));
    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
