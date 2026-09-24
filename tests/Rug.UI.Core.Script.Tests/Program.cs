using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

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
    Check(denied.State == ScriptState.Faulted && denied.Error is UnauthorizedAccessException, "capture permission must be enforced");
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

Console.WriteLine("Lua bridge tests passed");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class FakeCapture : ICaptureService
{
    private readonly TaskCompletionSource<CapturedFrame?> _frame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsCapturing => true;
    public Task StartAsync(nint hwnd, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default) => _frame.Task.WaitAsync(cancellationToken);
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
