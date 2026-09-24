using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Exceptions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Rug.UI.Core.Security;

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
    ("rug.press_key(65)", Permission.ControlInput)
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

await File.WriteAllTextAsync(path, "function on_tick() rug.click(12, 34); rug.press_key(65) end");
var input = new FakeInput();
await using (var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), input, NullLogger<LuaRuntime>.Instance))
{
    await runtime.InitializeAsync(path, new PluginManifest(new HashSet<string> { "input" }, TargetWindow: 123));
    Check((await runtime.StepAsync()).State == ScriptState.Yielded, "click must yield");
    Check((await runtime.ResumeAsync()).State == ScriptState.Yielded, "key must yield");
    Check((await runtime.ResumeAsync()).State == ScriptState.Stopped, "input script must finish");
    Check(input.Target == 123 && input.X == 12 && input.Y == 34 && input.Key == 65, "input target/arguments were lost");
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
         "permissions":["timer","vision.capture"],"targetWindow":987,"configuration":{"injected":"yes"}}
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

    var manager = new PluginManager(NullLogger<PluginManager>.Instance);
    PluginManifest[] plugins = manager.ScanPlugins(pluginsRoot).ToArray();
    Check(plugins.Length == 1 && plugins[0].Id == "demo" && plugins[0].Entry == "main.lua" &&
          plugins[0].Permissions.SequenceEqual(new[] { "timer", "vision.capture" }) &&
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

Console.WriteLine("Lua bridge tests passed");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class FakeCapture : ICaptureService
{
    private readonly TaskCompletionSource<CapturedFrame?> _frame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int GrabCalls { get; private set; }
    public bool IsCapturing => true;
    public Task StartAsync(nint hwnd, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default)
    { GrabCalls++; return _frame.Task.WaitAsync(cancellationToken); }
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Complete() => _frame.SetResult(new CapturedFrame(new byte[4], 1, 1, 4));
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
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Key { get; private set; }
    public Task MoveMouseAsync(int x, int y, TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true, CancellationToken cancellationToken = default)
    { X = x; Y = y; return Task.CompletedTask; }
    public Task MoveMouseRelativeAsync(int dx, int dy, TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClickAsync(MouseButton button = MouseButton.Left, int holdMs = 0, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task MouseDownAsync(MouseButton button, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task MouseUpAsync(MouseButton button, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DragAndDropAsync(int sx, int sy, int ex, int ey, TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task KeyDownAsync(int virtualKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task KeyUpAsync(int virtualKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task KeyPressAsync(int virtualKey, int holdMs = 0, CancellationToken cancellationToken = default)
    { Key = virtualKey; return Task.CompletedTask; }
    public Task SendTextAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetTargetAsync(nint hwnd, bool background, CancellationToken cancellationToken = default)
    { Target = hwnd; return Task.CompletedTask; }
    public Task SetHumanizeConfigAsync(HumanizeConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<TrajectorySample>> PlanTrajectoryAsync(int sx, int sy, int ex, int ey, TrajectoryType trajectory = TrajectoryType.CubicBezier, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TrajectorySample>>([]);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
