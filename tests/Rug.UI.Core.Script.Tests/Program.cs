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
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;
using Rug.UI.Core.Security;
using Rug.UI.Core.Schema;
using RugTaskScheduler = Rug.UI.Core.Services.TaskScheduler;

if (args.Contains("--checkpoint-restore-child")) { await CheckpointTests.RestoreChildAsync(args[1]); return; }
if (args.Contains("--checkpoint-restore-manual")) { await ManualCheckpointRestore.RunAsync(); return; }
if (args.Contains("--checkpoint-restore-ui")) { await ManualCheckpointRestore.RunAsync(automated: true); return; }
if (args.Contains("--checkpoint-restore-native")) { await NativeCheckpointRestore.RunAsync(); return; }
if (args.Contains("--checkpoint-restore-tests")) { await CheckpointTests.RunRestoreAsync(); return; }
if (args.Contains("--observed-click-tests")) { await CheckpointTests.RunObservedClicksAsync(); return; }
if (args.Contains("--execution-trace-tests")) { await CheckpointTests.RunExecutionTracesAsync(); return; }
if (args.Contains("--trace-review-tests")) { await CheckpointTests.RunTraceReviewAsync(); return; }
if (args.Contains("--trace-review-ui")) { await TraceReviewUi.RunAsync(); return; }
if (args.Contains("--controlled-visual-sample")) { await ControlledVisualSample.RunAsync(args.Length>1?args[1]:Path.Combine(Environment.CurrentDirectory,"artifacts","controlled-visual")); return; }
if (args.Contains("--controlled-recovery-sample")) { await ControlledRecoverySample.RunAsync(args.Length>1?args[1]:Path.Combine(Environment.CurrentDirectory,"artifacts","controlled-recovery")); return; }
if (args.Contains("--host-exit-ui")) { await HostExitMatrix.RunAsync(); return; }
if (args.Contains("--controlled-recovery-ui") || args.Contains("--controlled-recovery-manual"))
{
    string repo = ControlledRecoverySample.Repo(); ControlledRecoverySample.LoadNative(repo);
    string root = Path.Combine(repo,"artifacts","controlled-recovery-ui-"+Guid.NewGuid().ToString("N"));
    var result = await ControlledRecoveryUi.RunAsync(repo, root, args.Contains("--controlled-recovery-manual"));
    Console.WriteLine(JsonSerializer.Serialize(result)); Console.WriteLine("ARTIFACTS "+root);
    if(!result.InteractionsPassed || result.ExitCode!=0) Environment.ExitCode=1;
    return;
}
if (args.Contains("--execution-trace-read")) { CheckpointTests.TraceReadChild(args); return; }
if (args.Contains("--execution-trace-export"))
{
    var page=new TraceFileStore(args[1]).Read(Guid.Parse(args[2]),Guid.Parse(args[3]),limit:256);
    Console.WriteLine(JsonSerializer.Serialize(page,new JsonSerializerOptions{WriteIndented=true,Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}}));return;
}
if (args.Contains("--execution-trace-native")) { await ObservedClickNative.RunAsync(args[^1]); return; }
if (args.Contains("--observed-click-native")) { await ObservedClickNative.RunAsync(); return; }
if (args.Contains("--observation-tests")) { await CheckpointTests.RunObservationsAsync(); return; }
if (args.Contains("--observation-native")) { await ObservationNativeSmoke.RunAsync(); return; }
if (args.Contains("--capture-geometry-native")) { await CaptureGeometryTests.RunNativeAsync(); return; }
if (args.Contains("--plugin-update-tests")) { await CheckpointTests.RunPluginUpdatesAsync(); return; }
if (args.Contains("--runtime-update-tests")) { await CheckpointTests.RunRuntimeUpdatesAsync(); return; }
if (args.Contains("--runtime-switch-tests")) { await CheckpointTests.RunRuntimeSwitchesAsync(); return; }
if (args.Contains("--runtime-switch-race")) { await CheckpointTests.RunRuntimeSwitchRaceAsync(args.Last()); return; }
if (args.Contains("--runtime-update-late-tests")) { await CheckpointTests.RunLateCheckpointResultsAsync(); return; }
if (args.Contains("--runtime-update-ui")) { await RuntimeUpdateUi.RunAsync(); return; }
if (args.Contains("--runtime-switch-ui")) { await RuntimeUpdateUi.RunAsync("Switch"); await RuntimeUpdateUi.RunAsync("SwitchPause"); await RuntimeUpdateUi.RunAsync("SwitchReject"); return; }
if (args.Contains("--runtime-switch-manual")) { await RuntimeUpdateUi.RunSwitchManualAsync(); return; }
if (args.Contains("--checkpoint-child-read")) { await CheckpointTests.ChildReadAsync(args); return; }
if (args.Contains("--checkpoint-tests")) { await CheckpointTests.RunAsync(); return; }

if (args.Contains("--recovery-ui")) { await ManualRecovery.RunAsync(automated: true); return; }
if (args.Contains("--recovery-manual")) { await ManualRecovery.RunAsync(); return; }
if (args.Contains("--intervention-manual")) { await ManualInputIntervention.RunAsync(); return; }

if (args.Contains("--hook-observer"))
{
    try
    {
        ExternalHookObserver.ValidateArguments(args);
        await ExternalHookObserver.RunAsync();
    }
    catch (Exception error) { Console.WriteLine($"OBSERVER FAILED: {error.Message}"); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--hook-isolation-stage"))
{
    try
    {
        int index = Array.IndexOf(args, "--hook-isolation-stage");
        int moduleIndex = Array.IndexOf(args, "--hook-module");
        int orderIndex = Array.IndexOf(args, "--startup-order");
        if (args.Count(a => a == "--manual-hook-install") > 1) throw new ArgumentException("Specify --manual-hook-install once");
        await RawInputDiagnostics.RunIsolationAsync(index + 1 < args.Length ? args[index + 1] : "",
            moduleIndex < 0 ? "callback" : moduleIndex + 1 < args.Length ? args[moduleIndex + 1] : "",
            orderIndex < 0 ? null : orderIndex + 1 < args.Length ? args[orderIndex + 1] : "",
            args.Contains("--manual-hook-install"));
    }
    catch (Exception error) { Console.WriteLine($"ISOLATION FAILED: {error.Message}"); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--manual-hook-install"))
{
    Console.WriteLine("ISOLATION FAILED: --manual-hook-install requires --hook-isolation-stage keyboard-native-callback");
    Environment.ExitCode = 1; return;
}

if (args.Contains("--raw-input-self-test") || args.Contains("--raw-input-diagnostic"))
{
    try { await RawInputDiagnostics.RunAsync(args.Contains("--raw-input-diagnostic")); }
    catch (Exception error) { Console.WriteLine($"DIAGNOSTIC FAILED: {error.Message}"); Environment.ExitCode = 1; }
    return;
}

if (args.Contains("--raw-input-tests")) { await RawInputTests.RunAsync(); return; }
if (args.Contains("--intervention-tests")) { await InputInterventionTests.RunAsync(); return; }
if (args.Contains("--recovery-native")) { await NativeRecoveryTests.RunAsync(); return; }
if (args.Contains("--recovery-tests")) { await RecoveryTests.RunAsync(); return; }
if (args.Contains("--lifecycle-tests")) { await LifecycleTests.RunAsync(); return; }
if (args.Contains("--observer-tests")) { await ExternalHookObserverTests.RunAsync(); return; }
if (args.Contains("--manual-hook-tests")) { await ManualHookInstallTests.RunAsync(); return; }

string path = Path.Combine(AppContext.BaseDirectory, "bridge_test.lua");
await File.WriteAllTextAsync(path, "function on_tick() rug.sleep(200); local frame = rug.capture(); assert(frame.width == 1 and frame.height == 1); rug.log('done') end");
var capture = new FakeCapture();
await using (var runtime = new LuaRuntime(capture, new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance))
{
    var states = new List<ScriptState>();
    runtime.StateChanged += (_, e) => states.Add(e.Current);
    await TestAuthorizationHost.InitializeAsync(runtime, path, new PluginManifest(new HashSet<string> { "timer", "vision.capture" }));
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
    await TestAuthorizationHost.InitializeAsync(runtime, path, new PluginManifest(new HashSet<string> { "timer" }));
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
    await TestAuthorizationHost.InitializeAsync(runtime, path, new PluginManifest(new HashSet<string> { "vision.capture", "vision.ocr" }));
    Check((await runtime.StepAsync()).State == ScriptState.Yielded, "capture must yield");
    Check((await runtime.ResumeAsync()).State == ScriptState.Yielded, "OCR must yield");
    Check((await runtime.ResumeAsync()).State == ScriptState.Stopped, "OCR result must reach Lua");
}

await using (var runtime = new LuaRuntime(readyCapture, new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance))
{
    await TestAuthorizationHost.InitializeAsync(runtime, path, new PluginManifest(new HashSet<string>()));
    ScriptExecutionResult denied = await runtime.StepAsync();
    Check(denied.State == ScriptState.Faulted && denied.Error is PermissionDeniedException { Permission: Permission.VisionCapture }, "capture permission must be enforced");
}

foreach ((string call, string permission) in new[]
{
    ("rug.sleep(1)", Permission.Timer),
    ("rug.capture()", Permission.VisionCapture),
    ("rug.ocr('winrt')", Permission.VisionOcr),
    ("rug.find_image('template.png')", Permission.VisionMatch),
    ("rug.click(1, 2)", Permission.ControlInput),
    ("rug.press_key(65)", Permission.ControlInput),
    ("rug.agent.resolve_anomaly('denied', 'detail')", Permission.Agent)
})
{
    await File.WriteAllTextAsync(path, "function on_tick() " + call + " end");
    var deniedCapture = new FakeCapture();
    await using var runtime = new LuaRuntime(deniedCapture, new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance);
    await TestAuthorizationHost.InitializeAsync(runtime, path, new PluginManifest([]) { Id = "denied-test" });
    ScriptExecutionResult result = await runtime.StepAsync();
    Check(result.State == ScriptState.Faulted && result.Error is PermissionDeniedException ex &&
          ex.Permission == permission && ex.Api == "rug." + call.Split('(')[0][4..],
          "permission mismatch for " + call);
    Check(deniedCapture.GrabCalls == 0, "denied call reached capture service");
}

string visionRoot = Path.Combine(AppContext.BaseDirectory, "vision-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(visionRoot);
try
{
    await File.WriteAllBytesAsync(Path.Combine(visionRoot, "template.png"), [137, 80, 78, 71]);
    await File.WriteAllTextAsync(Path.Combine(visionRoot, "broken.png"), "not an image");
    var visionManifest = new PluginManifest([Permission.VisionCapture, Permission.VisionMatch, Permission.VisionOcr,
        Permission.Timer]) { PluginDirectory = visionRoot };
    var visionGate = TestAuthorizationHost.Gate(visionManifest, NullLogger.Instance);
    var matcher = new FakeTemplateMatch();
    var ocr = new FakeOcr();
    var directCapture = new SequenceCapture(_ => VisualFrame(true));
    using (var visual = new VisualEngineBridge(directCapture, ocr, matcher, visionGate,
        new RunIdentity("visual-test", Guid.NewGuid(), 1, null, 0), visionRoot))
    {
        Check((await visual.CaptureAsync())?.Width == 4, "visual capture did not cache the WGC frame");
        ImageFindResult? hit = await visual.FindImageAsync("template.png", 0.9f, new Rect(1, 1, 3, 2));
        Check(hit is { X: 2, Y: 2 } && hit.Similarity >= 0.9 &&
              matcher.LastWidth == 3 && matcher.LastHeight == 2,
              "region match did not restore physical client coordinates");
        VisionOcrResult recognized = await visual.OcrAsync(new Rect(1, 1, 3, 2), "zh-Hans");
        Check(recognized.Text == "ready" && recognized.Blocks.Single().BoundingBox.X == 1 &&
              recognized.Blocks.Single().BoundingBox.Y == 1 &&
              ocr.LastFrame?.Width == 3 && ocr.LastLanguage == "zh-Hans" &&
              ocr.LastEngine == OcrEngineType.WinRt,
              "region OCR lost its language or full-frame bounding box");
        try { await visual.FindImageAsync("missing.png"); throw new Exception("missing template accepted"); }
        catch (FileNotFoundException) { }
        try { await visual.FindImageAsync("broken.png"); throw new Exception("invalid template accepted"); }
        catch (InvalidDataException ex) when (ex.Message.Contains("unreadable")) { }
        try { await visual.FindImageAsync("../outside.png"); throw new Exception("template escaped plugin root"); }
        catch (PermissionDeniedException ex) when (ex.Permission == Permission.FileSystem) { }
        try { await visual.FindImageAsync("template.png", region: new Rect(3, 2, 2, 2));
              throw new Exception("out-of-bounds region accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }

    using (var deniedVisual = new VisualEngineBridge(new SequenceCapture(_ => VisualFrame(true)),
        new FakeOcr(), matcher, TestAuthorizationHost.Gate(new PluginManifest([]), NullLogger.Instance),
        new RunIdentity("visual-test", Guid.NewGuid(), 1, null, 0), visionRoot))
    {
        try { await deniedVisual.CaptureAsync(); throw new Exception("capture without grant succeeded"); }
        catch (PermissionDeniedException ex) when (ex.Permission == Permission.VisionCapture) { }
        try { await deniedVisual.FindImageAsync("template.png"); throw new Exception("match without grant succeeded"); }
        catch (PermissionDeniedException ex) when (ex.Permission == Permission.VisionMatch) { }
        try { await deniedVisual.OcrAsync(); throw new Exception("OCR without grant succeeded"); }
        catch (PermissionDeniedException ex) when (ex.Permission == Permission.VisionOcr) { }
    }

    await File.WriteAllTextAsync(path, """
        function on_tick()
          local frame = rug.capture()
          assert(frame.width == 4 and frame.height == 3)
          local hit = rug.find_image('template.png', 0.9, {x=1,y=1,width=3,height=2})
          assert(hit.x == 2 and hit.y == 2 and hit.similarity > 0.9)
          local result = rug.ocr({x=1,y=1,width=3,height=2}, 'zh-Hans')
          assert(result.text == 'ready' and #result == 1 and result.blocks[1].x == 1)
        end
        """);
    await using (var runtime = new LuaRuntime(new SequenceCapture(_ => VisualFrame(true)),
        new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance, templateMatcher: matcher))
    {
        await TestAuthorizationHost.InitializeAsync(runtime, path, visionManifest);
        ScriptExecutionResult result = await DriveToEndAsync(runtime);
        Check(result.State == ScriptState.Stopped, "Lua visual result tables lost coordinates or OCR text");
    }

    await File.WriteAllTextAsync(path, """
        function on_tick()
          local hit = rug.wait_image('template.png', 500, 0.8, 20)
          assert(hit and hit.x == 2 and hit.y == 2)
        end
        """);
    var appearing = new SequenceCapture(index => VisualFrame(index >= 3));
    await using (var runtime = new LuaRuntime(appearing, new FakeOcr(), new FakeInput(),
        NullLogger<LuaRuntime>.Instance, templateMatcher: matcher))
    {
        await TestAuthorizationHost.InitializeAsync(runtime, path, visionManifest);
        ScriptExecutionResult result = await DriveToEndAsync(runtime);
        Check(result.State == ScriptState.Stopped && appearing.GrabCalls == 3,
              "wait_image did not return promptly when the template appeared");
    }

    await File.WriteAllTextAsync(path, """
        function on_tick()
          assert(rug.wait_image('template.png', 200, 0.8, 30) == nil)
        end
        """);
    var absent = new SequenceCapture(_ => VisualFrame(false));
    await using (var runtime = new LuaRuntime(absent, new FakeOcr(), new FakeInput(),
        NullLogger<LuaRuntime>.Instance, templateMatcher: matcher))
    {
        await TestAuthorizationHost.InitializeAsync(runtime, path, visionManifest);
        var watch = Stopwatch.StartNew();
        ScriptExecutionResult result = await DriveToEndAsync(runtime);
        Check(result.State == ScriptState.Stopped && absent.GrabCalls >= 2 &&
              watch.ElapsedMilliseconds >= 180 && watch.ElapsedMilliseconds < 1000,
              "wait_image timeout blocked or returned too early");
    }

    WeakReference<CapturedFrame>? firstFrame = null;
    var repeatedCapture = new SequenceCapture(index =>
    {
        var frame = LeakFrame();
        if (index == 1) firstFrame = new WeakReference<CapturedFrame>(frame);
        return frame;
    });
    var repeatedMatch = new FakeTemplateMatch();
    using (var visual = new VisualEngineBridge(repeatedCapture, new FakeOcr(), repeatedMatch,
        visionGate, new RunIdentity("visual-test", Guid.NewGuid(), 1, null, 0), visionRoot))
    {
        for (int i = 0; i < 1000; i++)
        {
            await visual.CaptureAsync();
            Check(await visual.FindImageAsync("template.png") is not null,
                  "repeated in-memory matching lost its frame");
        }
        Check(repeatedCapture.GrabCalls == 1000 && repeatedMatch.MatchCalls == 1000,
              "capture/match stress loop did not complete");
    }
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Check(firstFrame is not null && !firstFrame.TryGetTarget(out _),
          "visual bridge retained an old capture frame after 1,000 iterations");
}
finally { Directory.Delete(visionRoot, recursive: true); }

await File.WriteAllTextAsync(path, "function on_tick() rug.agent.resolve_anomaly('pending', 'detail'); rug.log('unreachable') end");
var anomalyGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), new FakeInput(), NullLogger<LuaRuntime>.Instance))
{
    runtime.ConfigureAnomalyHandler((_, _) => anomalyGate.Task);
    await TestAuthorizationHost.InitializeAsync(runtime, path, new PluginManifest([Permission.Agent]));
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
    await TestAuthorizationHost.InitializeAsync(runtime, path, new PluginManifest(new HashSet<string> { "timer" }));
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
    TestAuthorizationHost.Gate(new PluginManifest([]) { Id = "input-denied" }, inputAudit), fakeMapper))
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
    TestAuthorizationHost.Gate(new PluginManifest([Permission.ControlInput]), NullLogger.Instance),
    fakeMapper, _ => true, arbiter: TestAuthorizationHost.InputArbiter))
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
    TestAuthorizationHost.Gate(new PluginManifest([Permission.ControlInput]), NullLogger.Instance),
    fakeMapper, _ => false, arbiter: TestAuthorizationHost.InputArbiter))
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
    await TestAuthorizationHost.InitializeAsync(runtime, path, new PluginManifest(new HashSet<string> { "input" }, TargetWindow: 123));
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
    await TestAuthorizationHost.InitializeAsync(runtime, path, new PluginManifest([Permission.ControlInput], TargetWindow: 123)
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
    await TestAuthorizationHost.InitializeAsync(runtime, path, new PluginManifest([]));
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
        await TestAuthorizationHost.InitializeAsync(configRuntime, Path.Combine(valid, plugins[0].Entry), plugins[0]);
        Check((await configRuntime.StepAsync()).State == ScriptState.Stopped, "typed config defaults did not reach Lua");
    }
    Check(!plugins.Any(p => p.Id == "escape"), "entry traversal was accepted");
    Check(!plugins.Any(p => p.Id == "bad-config"), "malformed config was accepted");
    Check(!plugins.Any(p => p.Id == "bad-default"), "invalid config default was accepted");
    Check(!plugins.Any(p => p.Id == "bad-mode"), "invalid input delivery mode was accepted");
}
finally { Directory.Delete(pluginsRoot, recursive: true); }

var audit = new AuditLogger();
var gate = TestAuthorizationHost.Gate(new PluginManifest([Permission.Timer]) { Id = "audit-demo" }, audit);
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
await TestAuthorizationHost.InitializeAsync(watchdogRuntime, path, new PluginManifest([]));
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
var schedulerAuthorization = new PluginAuthorization();
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
    var scopedStore = new StateStore();
    using var scopedBus = new CommandBus(scopedStore);
    string anomalyDirectory = Path.Combine(schedulerRoot, "logs", "anomalies");
    var anomalyLogger = new AnomalyLogger(new RecordingLogger<AnomalyLogger>(events), anomalyDirectory);
    await using var scheduler = new RugTaskScheduler(
        new PluginManager(NullLogger<PluginManager>.Instance), schedulerRoot,
        () => { var c = new FakeCapture(events); captures.Add(c); return c; },
        () => { var i = new FakeInput(); inputs.Add(i); return i; },
        (c, i) => new LuaRuntime(c, new FakeOcr(), i, luaLogger, commandBus: scopedBus),
        NullLogger<RugTaskScheduler>.Instance, anomalyLogger, TimeSpan.FromMilliseconds(20),
        commandBus: scopedBus, authorization: schedulerAuthorization, inputArbiter: TestAuthorizationHost.InputArbiter);
    int instanceListNotifications = 0;
    scheduler.InstancesChanged += (_, _) => Interlocked.Increment(ref instanceListNotifications);

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
    Check(instanceListNotifications >= 2, "instance add/remove did not notify the host");
    Check(lifeContext.TaskStatus == AutomationTaskStatus.Stopped && events.Contains("Lua: life_stop") &&
          events.Contains("capture-stop:101"), "on_stop or capture cleanup missing");
    Guid rebound = await scheduler.StartTaskAsync("life", 101);
    TaskExecutionContext reboundContext = scheduler.Instances.Single(item => item.InstanceId == rebound);
    Check(reboundContext.InstanceId != lifeContext.InstanceId &&
          reboundContext.RunGeneration > lifeContext.RunGeneration &&
          reboundContext.TargetBindingId is not null &&
          reboundContext.TargetBindingId != lifeContext.TargetBindingId,
          "same HWND was reused as run or target-binding identity");
    await scheduler.StopTaskAsync(rebound);
    int stablePartitionCount = scopedStore.ActivePartitionCount;
    StateScope? stoppedScope = null;
    for (int run = 0; run < 24; run++)
    {
        Guid repeated = await scheduler.StartTaskAsync("other", 0);
        StateScope scope = scheduler.Instances.Single(item => item.InstanceId == repeated).InstanceScope;
        scopedBus.SetState(scope, "repeat", run);
        await scheduler.StopTaskAsync(repeated);
        stoppedScope = scope;
        Check(scopedStore.ActivePartitionCount == stablePartitionCount,
            "repeated starts and stops retained instance partitions");
    }
    try { scopedBus.SetState(stoppedScope!.Value, "late", true);
        throw new Exception("stopped run accepted a late write"); }
    catch (ObjectDisposedException) { }

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

    await WritePluginAsync("scoped-a", """
        function on_init()
          rug.set_state('same_key', rug.get_state('launch'))
          rug.set_state('config_at_start', rug.get_config('level'))
        end
        """);
    await WritePluginAsync("scoped-b", """
        function on_init() rug.set_state('same_key', rug.get_state('launch')) end
        """);
    await scheduler.RefreshPluginsAsync();
    var pluginA = StateScope.Plugin("scoped-a");
    scopedBus.SetState(pluginA, "level", "before");
    Guid[] scoped = await Task.WhenAll(
        scheduler.StartTaskAsync("scoped-a", 0, new Dictionary<string, object?> { ["launch"] = "one" }),
        scheduler.StartTaskAsync("scoped-a", 0, new Dictionary<string, object?> { ["launch"] = "two" }),
        scheduler.StartTaskAsync("scoped-b", 0, new Dictionary<string, object?> { ["launch"] = "three" }));
    var scopedContexts = scoped.Select(id => scheduler.Instances.Single(item => item.InstanceId == id)).ToArray();
    await WaitUntilAsync(() => scoped.All(id => scopedBus.Contains(
        scopedContexts.Single(item => item.InstanceId == id).InstanceScope, "same_key")));
    Check(scopedBus.GetState(scopedContexts[0].InstanceScope, "same_key", "") == "one" &&
          scopedBus.GetState(scopedContexts[1].InstanceScope, "same_key", "") == "two" &&
          scopedBus.GetState(scopedContexts[2].InstanceScope, "same_key", "") == "three" &&
          scopedContexts[0].RunGeneration != scopedContexts[1].RunGeneration &&
          scopedContexts.All(item => item.TargetBindingId is null && item.TargetHwnd == 0),
          "windowless runs shared state or identity");
    Check(scopedBus.GetState(scopedContexts[0].InstanceScope, "config_at_start", "") == "before" &&
          scopedBus.GetState(scopedContexts[1].InstanceScope, "config_at_start", "") == "before",
          "plugin config startup snapshot was not isolated");
    scopedBus.SetState(pluginA, "level", "after");
    Check(scopedBus.GetState(scopedContexts[0].InstanceScope, "config_at_start", "") == "before",
          "running instance observed later plugin config write");
    await Task.WhenAll(scoped.Select(scheduler.StopTaskAsync));
    Check(!scopedBus.Contains(scopedContexts[0].InstanceScope, "same_key") &&
          scopedBus.GetState(pluginA, "level", "") == "after", "stop did not clear instance state or keep plugin config");
    try { scopedBus.SetState(scopedContexts[0].InstanceScope, "late", true); throw new Exception("old run wrote after stop"); }
    catch (ObjectDisposedException) { }
    Guid fresh = await scheduler.StartTaskAsync("scoped-a", 0);
    TaskExecutionContext freshContext = scheduler.Instances.Single(item => item.InstanceId == fresh);
    Check(freshContext.RunGeneration > scopedContexts[1].RunGeneration &&
          !scopedBus.Contains(freshContext.InstanceScope, "launch"), "restart inherited old launch inputs");
    await WaitUntilAsync(() => scopedBus.GetState(freshContext.InstanceScope, "config_at_start", "") == "after");
    await scheduler.StopTaskAsync(fresh);

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
    await WaitUntilAsync(() => anomalyContext.TaskStatus == AutomationTaskStatus.AwaitingHuman &&
        Directory.Exists(anomalyDirectory) && Directory.GetFiles(anomalyDirectory, "*.json").Length == 1);
    Check(!events.Contains("Lua: must_not_resume"), "human wait returned to mainline");
    await scheduler.StopTaskAsync(anomaly);
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
              !record.GetProperty("scriptContext").GetString()!.Contains("on_tick") &&
              record.GetProperty("recoveryId").GetGuid() == anomalyContext.Snapshot.Recovery!.RecoveryId &&
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
    Check(anomalyContext.LastError is null && anomalyContext.TaskStatus == AutomationTaskStatus.Stopped &&
          events.Any(e => e.Contains("[Anomaly Detected]")) &&
          events.Contains("Lua: anomaly_stop") && !events.Contains("Lua: must_not_resume") &&
          events.Contains("capture-stop:306"), "human wait did not preserve/stop/clean up safely");

    var delayedCapture = new FakeCapture
    {
        StartGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
    };
    var delayedScheduler = new RugTaskScheduler(
        new PluginManager(NullLogger<PluginManager>.Instance), schedulerRoot,
        () => delayedCapture, () => new FakeInput(),
        (c, i) => new LuaRuntime(c, new FakeOcr(), i, luaLogger),
        NullLogger<RugTaskScheduler>.Instance, anomalyLogger, authorization: schedulerAuthorization, inputArbiter: TestAuthorizationHost.InputArbiter);
    Task<Guid> opening = delayedScheduler.StartTaskAsync("life", 404);
    await WaitUntilAsync(() => delayedCapture.StartCalls == 1);
    await delayedScheduler.DisposeAsync();
    try { await opening; throw new Exception("disposed scheduler accepted a pending start"); }
    catch (OperationCanceledException) { }
    Check(delayedCapture.StopCalls == 1 && delayedCapture.Disposed, "pending start leaked capture");

    await WritePluginAsync("hot", """
        function on_init() rug.log('hot_v1') end
        function on_stop() rug.log('hot_stop') end
        """);
    string hotDirectory = Path.Combine(schedulerRoot, "hot");
    string hotUi = Path.Combine(hotDirectory, "ui.json");
    await File.WriteAllTextAsync(hotUi, """{"type":"Page","id":"hot-page","title":"Before"}""");
    await scheduler.RefreshPluginsAsync();
    Task<Guid> blockedStart;
    await using (var lease = await scheduler.AcquirePluginReloadAsync("hot"))
    {
        blockedStart = scheduler.StartTaskAsync("hot", 708);
        await Task.Delay(50);
        Check(!blockedStart.IsCompleted, "plugin start bypassed reload lease");
    }
    await scheduler.StopTaskAsync(await blockedStart);
    Guid hotOriginal = await scheduler.StartTaskAsync("hot", 707);
    await WaitUntilAsync(() => events.Contains("Lua: hot_v1"));
    var reloadSignals = new ConcurrentQueue<string>();
    await using (var reloader = new HotReloadService(schedulerRoot, scheduler,
        NullLogger<HotReloadService>.Instance, TimeSpan.FromMilliseconds(300)))
    {
        using var uiBinding = reloader.RegisterUiHandler("hot", (snapshot, token) =>
        {
            new SchemaParser().Parse(snapshot.UiJson!);
            return Task.FromResult<IPluginUiUpdate>(new TestPluginUiStage());
        });
        reloader.Reloaded += (_, e) =>
        {
            if (e.PluginId == "hot") reloadSignals.Enqueue(e.Status.ToString());
        };
        reloader.ReloadFailed += (_, e) =>
        {
            if (e.PluginId == "hot") reloadSignals.Enqueue("failure");
        };
        reloader.Start();
        await reloader.Ready;
        await File.WriteAllTextAsync(Path.Combine(hotDirectory, "main.lua"), """
            function on_init() rug.log('hot_v2') end
            function on_stop() rug.log('hot_stop') end
            """);
        await WaitUntilAsync(() => reloadSignals.Contains("RuntimePending"));
        Check(scheduler.Instances.Any(instance => instance.InstanceId == hotOriginal) && !events.Contains("Lua: hot_v2"),
              "runtime update changed active instance or replayed init");

        await File.WriteAllTextAsync(hotUi, "{ invalid JSON");
        await WaitUntilAsync(() => reloadSignals.Contains("failure"));
        Check(scheduler.Instances.Any(instance => instance.InstanceId == hotOriginal), "invalid update stopped old instance");
        await File.WriteAllTextAsync(hotUi, """{"type":"Page","id":"hot-page","title":"Recovered"}""");
        await WaitUntilAsync(() => reloadSignals.Count(signal => signal == "RuntimePending") >= 2);
        await scheduler.StopTaskAsync(hotOriginal);
        reloader.RequestReload(hotDirectory);
        await WaitUntilAsync(() => reloadSignals.Contains("RuntimeApplied"));
        await scheduler.StartTaskAsync("hot", 707);
        await WaitUntilAsync(() => events.Contains("Lua: hot_v2"));
    }
    await Task.WhenAll(scheduler.Instances.Where(instance => instance.PluginId == "hot")
        .Select(instance => scheduler.StopTaskAsync(instance.InstanceId)));

    async Task WritePluginAsync(string id, string script, string permissions = "")
    {
        string directory = Path.Combine(schedulerRoot, id);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
            "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"version\":\"1.0.0\",\"targetProcess\":\"Demo.exe\",\"permissions\":[" + permissions + "]}");
        await File.WriteAllTextAsync(Path.Combine(directory, "main.lua"), script);
        TestAuthorizationHost.ApproveCatalog(schedulerAuthorization, schedulerRoot);
    }
}
finally
{
    string full = Path.GetFullPath(schedulerRoot);
    string output = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    if (!full.StartsWith(output, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(full).StartsWith("scheduler-test-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing to delete a path outside the test output.");
    Directory.Delete(full, recursive: true);
}

var stateStore = new StateStore();
int stateNotifications = 0;
stateStore.StateChanged += (_, _) => Interlocked.Increment(ref stateNotifications);
await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
{
    for (int value = 0; value < 500; value++)
        stateStore.SetState($"worker_{worker}", value);
})));
Check(stateNotifications == 4000 && Enumerable.Range(0, 8)
    .All(worker => stateStore.GetState($"worker_{worker}", -1) == 499),
    "concurrent state writes lost values or notifications");
stateStore.SetState("worker_0", 499);
Check(stateNotifications == 4000, "unchanged state emitted a duplicate notification");
stateStore.SetState<string?>("nullable", null);
Check(stateStore.GetState("nullable", "fallback") is null &&
      stateStore.GetState("absent", "fallback") == "fallback",
      "null and absent state were confused");
stateStore.StateChanged += (_, e) =>
{
    if (e.Key == "parent") stateStore.SetState("child", true);
};
stateStore.SetState("parent", true);
Check(stateStore.GetState("child", false), "state listener could not re-enter the store");

var tenantA = StateScope.Instance("alpha", Guid.NewGuid());
var tenantB = StateScope.Instance("alpha", Guid.NewGuid());
var tenantC = StateScope.Instance("beta", Guid.NewGuid());
stateStore.OpenInstance(tenantA);
stateStore.OpenInstance(tenantB);
stateStore.OpenInstance(tenantC);
stateStore.SetState(StateScope.Plugin("alpha"), "level", "alpha-only");
stateStore.SetState(StateScope.Plugin("beta"), "level", "beta-only");
Check(stateStore.GetState(StateScope.Plugin("alpha"), "level", "") == "alpha-only" &&
      stateStore.GetState(StateScope.Plugin("beta"), "level", "") == "beta-only",
      "plugin configurations shared a key");
Check(StateScope.Plugin("ALPHA") == StateScope.Plugin("alpha"),
      "plugin scope casing diverged from catalog identity");
var tenantEvents = new ConcurrentQueue<StateChangedEventArgs>();
stateStore.StateChanged += (_, change) => tenantEvents.Enqueue(change);
await Task.WhenAll(Enumerable.Range(0, 3).Select(index => Task.Run(() =>
{
    StateScope scope = new[] { tenantA, tenantB, tenantC }[index];
    for (int value = 0; value < 100; value++) stateStore.SetState(scope, "shared", value);
})));
Check(new[] { tenantA, tenantB, tenantC }.All(scope =>
    stateStore.GetState(scope, "shared", -1) == 99) &&
    tenantEvents.Count(change => change.Key == "shared" && change.Scope == tenantA) == 100 &&
    tenantEvents.Count(change => change.Key == "shared" && change.Scope == tenantB) == 100 &&
    tenantEvents.Count(change => change.Key == "shared" && change.Scope == tenantC) == 100,
    "structural scopes mixed values or events");
Check(tenantEvents.Where(change => change.Scope == tenantA && change.Key == "shared")
    .Select(change => change.Revision).Distinct().Count() == 100,
    "scope revisions were not unique");
stateStore.SetState<string?>(tenantA, "explicit_null", null);
Check(stateStore.Contains(tenantA, "explicit_null") && !stateStore.Contains(tenantB, "explicit_null") &&
      stateStore.GetState(tenantA, "explicit_null", "fallback") is null &&
      stateStore.GetState(tenantA, "missing", "fallback") == "fallback",
      "scoped null and missing semantics are wrong");
string[] mutable = ["original"];
stateStore.SetState(tenantA, "array", mutable);
mutable[0] = "changed";
string[] detached = stateStore.GetState(tenantA, "array", Array.Empty<string>());
detached[0] = "changed again";
var detachedSnapshot = stateStore.Snapshot(tenantA);
((string[])detachedSnapshot["array"]!)[0] = "changed in snapshot";
Check(stateStore.GetState(tenantA, "array", Array.Empty<string>())[0] == "original" &&
      tenantEvents.Last(change => change.Key == "array").Value is JsonElement,
      "mutable state leaked through a snapshot or event");
try { stateStore.SetState(tenantA, "too_large", new string('x', StateStore.MaxValueBytes));
    throw new Exception("oversized state was accepted"); }
catch (ArgumentOutOfRangeException) { }
try { stateStore.SetState(tenantA, "unsupported", (Action)(() => { }));
    throw new Exception("unsupported state was accepted"); }
catch (ArgumentException) { }
object deep = "leaf";
for (int depth = 0; depth < StateStore.MaxDepth + 2; depth++) deep = new object[] { deep };
try { stateStore.SetState(tenantA, "too_deep", deep); throw new Exception("deep state was accepted"); }
catch (ArgumentException) { }

var uiCallbacks = new ConcurrentQueue<Action>();
bool uiAccess = false;
using var bus = new CommandBus(stateStore,
    action => { uiCallbacks.Enqueue(action); return true; },
    () => uiAccess);
int busNotifications = 0, callbackThread = 0;
bus.StateChanged += (_, e) =>
{
    if (e.Key == "ui_value")
    {
        Interlocked.Increment(ref busNotifications);
        callbackThread = Environment.CurrentManagedThreadId;
    }
};
await Task.Run(() => bus.SetState("ui_value", 1));
Check(bus.GetState("ui_value", 0) == 1 && busNotifications == 0 && uiCallbacks.Count == 1,
    "background state write did not enqueue exactly one UI notification");
Check(uiCallbacks.TryDequeue(out Action? uiAction), "queued UI callback was lost");
uiAccess = true;
int uiThread = Environment.CurrentManagedThreadId;
uiAction!();
Check(busNotifications == 1 && callbackThread == uiThread,
    "state notification was not delivered on the UI thread");
bus.SetState("ui_value", 2);
Check(busNotifications == 2 && uiCallbacks.IsEmpty,
    "UI-originated write did not notify synchronously");
uiAccess = false;
await Task.Run(() => bus.SetState("ui_value", 3));
await Task.Run(() => bus.SetState("ui_value", 4));
uiAccess = true;
while (uiCallbacks.TryDequeue(out Action? callback)) callback();
Check(busNotifications == 3 && bus.GetState("ui_value", 0) == 4,
    "delayed UI notification applied an obsolete state revision");
int scopedNotifications = 0;
int otherScopeNotifications = 0;
using var scopedSubscription = bus.SubscribeState(tenantA, (_, change) =>
{
    if (change.Scope == tenantA && change.Key == "queued") scopedNotifications++;
});
using var otherScopeSubscription = bus.SubscribeState(tenantB, (_, change) =>
{
    if (change.Key == "queued") otherScopeNotifications++;
});
uiAccess = false;
await Task.Run(() => bus.SetState(tenantA, "queued", 1));
await Task.Run(() => bus.SetState(tenantA, "queued", 2));
await Task.Run(() => bus.SetState(tenantB, "queued", 3));
uiAccess = true;
while (uiCallbacks.TryDequeue(out Action? callback)) callback();
Check(scopedNotifications == 1 && otherScopeNotifications == 1 &&
      bus.GetState(tenantA, "queued", 0) == 2,
    "late UI notification overwrote a scoped revision or crossed instances");
uiAccess = false;
await Task.Run(() => bus.SetState(tenantA, "queued", 3));
bus.CloseInstance(tenantA);
var replacementScope = StateScope.Instance("alpha", Guid.NewGuid());
bus.OpenInstance(replacementScope);
int replacementNotifications = 0;
using var replacementSubscription = bus.SubscribeState(replacementScope, (_, change) =>
{
    if (change.Key == "queued") replacementNotifications++;
});
bus.SetState(replacementScope, "queued", 4);
uiAccess = true;
int beforeDrain = scopedNotifications;
while (uiCallbacks.TryDequeue(out Action? callback)) callback();
Check(scopedNotifications == beforeDrain && replacementNotifications == 1 &&
      !bus.Contains(tenantA, "queued") && bus.GetState(replacementScope, "queued", 0) == 4,
    "stopped instance delivered a queued callback or changed the replacement run");
bus.CloseInstance(replacementScope);
try { bus.SetState(tenantA, "late", true); throw new Exception("closed instance accepted a late write"); }
catch (ObjectDisposedException) { }
int partitionsBeforeChurn = stateStore.ActivePartitionCount;
for (int run = 0; run < 1000; run++)
{
    StateScope scope = StateScope.Instance("alpha", Guid.NewGuid());
    bus.OpenInstance(scope);
    bus.SetState(scope, "value", run);
    bus.CloseInstance(scope);
}
Check(stateStore.ActivePartitionCount == partitionsBeforeChurn,
    "closed partitions or revisions accumulated after repeated runs");
int routed = 0;
using (bus.Subscribe("save", async (parameter, ct) =>
{
    await Task.Yield();
    ct.ThrowIfCancellationRequested();
    Interlocked.Add(ref routed, (int)parameter!);
}))
{
    await bus.ExecuteCommandAsync("save", 7);
}
Check(routed == 7, "async command was not routed to its subscriber");
try { await bus.ExecuteCommandAsync("save", 1); throw new Exception("unsubscribed command ran"); }
catch (KeyNotFoundException) { }

string stateScript = Path.Combine(AppContext.BaseDirectory, "state_bus_test.lua");
bus.SetState("host_only", true);
await File.WriteAllTextAsync(stateScript, """
    function on_tick()
      assert(rug.get_state('host_only') == nil)
      rug.set_state('auto_sell', true)
      assert(rug.get_state('auto_sell') == true)
      rug.sleep(20)
      assert(rug.get_state('auto_sell') == false)
    end
    """);
await using (var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), new FakeInput(),
    NullLogger<LuaRuntime>.Instance, commandBus: bus))
{
    var identity = new RunIdentity("state-test", Guid.NewGuid(), 1, null, 0);
    runtime.ConfigureState(identity, new Dictionary<string, object?>(), new Dictionary<string, object?>());
    await TestAuthorizationHost.InitializeAsync(runtime, stateScript, new PluginManifest([Permission.Timer]));
    Check((await runtime.StepAsync()).State == ScriptState.Yielded &&
          bus.GetState(identity.InstanceScope, "auto_sell", false), "Lua set_state did not update its C# instance state");
    bus.SetState(identity.InstanceScope, "auto_sell", false);
    Check((await runtime.ResumeAsync()).State == ScriptState.Stopped,
        "Lua get_state did not observe the latest C# write");
}

// Use the same cloned JSON launch inputs as host.start_windowless, with real NLua execution.
Dictionary<string, object?> jsonLaunchInputs;
using (var launchDocument = JsonDocument.Parse("""
    {"label":"中文启动", "count":42, "fraction":1.25, "negative":-3,
     "enabled":true, "disabled":false, "empty":null,
     "custom.timeline":[{"Id":"launch","Title":"Structured step","Status":"Succeeded","Thumbnail":null}],
     "options":{"enabled":false,"nested":[1,null,"value"]}}
    """))
{
    jsonLaunchInputs = launchDocument.RootElement.EnumerateObject()
        .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
}
string jsonLaunchScript = Path.Combine(AppContext.BaseDirectory, "json_launch_test.lua");
await File.WriteAllTextAsync(jsonLaunchScript, """
    function on_tick()
        assert(type(rug.get_state('label')) == 'string' and rug.get_state('label') == '中文启动')
        assert(type(rug.get_state('count')) == 'number' and rug.get_state('count') == 42)
        assert(type(rug.get_state('fraction')) == 'number' and rug.get_state('fraction') == 1.25)
        assert(type(rug.get_state('negative')) == 'number' and rug.get_state('negative') == -3)
        assert(type(rug.get_state('enabled')) == 'boolean' and rug.get_state('enabled') == true)
        assert(type(rug.get_state('disabled')) == 'boolean' and rug.get_state('disabled') == false)
        assert(type(rug.get_state('empty')) == 'nil' and rug.get_state('empty') == nil)
        assert(rug.get_state('missing') == nil)
        if rug.get_state('disabled') then
            error('JSON false must not enter the true branch')
        else
            rug.set_state('false_branch', true)
        end
        assert(rug.get_state('custom.timeline') ~= nil)
        local options = rug.get_state('options')
        assert(type(options) == 'table' and options.enabled == false)
        assert(type(options.nested) == 'table' and options.nested[1] == 1 and options.nested[2] == nil)
        assert(options.nested[3] == 'value' and options.GetType == nil)
        local timeline = rug.get_state('custom.timeline')
        assert(type(timeline) == 'table' and timeline[1].Title == 'Structured step')
        assert(timeline.GetType == nil and timeline[1].GetType == nil)
        assert(type(rug.get_config('structured')) == 'table' and rug.get_config('structured').GetType == nil)
        local ok, err = pcall(rug.get_state, '')
        assert(not ok and type(err) == 'string', 'safe host error: ok=' .. tostring(ok) .. ', type=' .. type(err))
        ok, err = pcall(rug.set_state, '', true)
        assert(not ok and type(err) == 'string', 'safe host error: ok=' .. tostring(ok) .. ', type=' .. type(err))
        ok, err = pcall(rug.get_config, {})
        assert(not ok and type(err) == 'string', 'safe host error: ok=' .. tostring(ok) .. ', type=' .. type(err))
    end
    """);
await using (var runtime = new LuaRuntime(new FakeCapture(), new FakeOcr(), new FakeInput(),
    NullLogger<LuaRuntime>.Instance, commandBus: bus))
{
    var identity = new RunIdentity("json-launch-test", Guid.NewGuid(), 1, null, 0);
    runtime.ConfigureState(identity, jsonLaunchInputs, new Dictionary<string, object?>
    { ["structured"] = new Dictionary<string, object?> { ["approved"] = false } });
    await TestAuthorizationHost.InitializeAsync(runtime, jsonLaunchScript, new PluginManifest([Permission.Timer]));
    Check((await DriveToEndAsync(runtime)).State == ScriptState.Stopped, "JSON launch Lua script completed");
    Check(bus.GetState(identity.InstanceScope, "false_branch", false), "JSON false takes the Lua else branch");
    SchemaStep[] launchSteps = bus.GetState<SchemaStep[]>(identity.InstanceScope, "custom.timeline", []);
    Check(launchSteps.Length == 1 && launchSteps[0].Title == "Structured step" &&
        launchSteps[0].Status == "Succeeded", "JSON launch arrays remain readable by StepFlow bindings");
    JsonElement launchOptions = bus.GetState<JsonElement>(identity.InstanceScope, "options");
    Check(launchOptions.GetProperty("enabled").ValueKind == JsonValueKind.False &&
        launchOptions.GetProperty("nested")[1].ValueKind == JsonValueKind.Null,
        "JSON launch objects retain their nested structure");
}
await GatewayTests.RunAsync();
await RecoveryTests.RunAsync();
await CheckpointTests.RunAsync();
await CheckpointTests.RunPluginUpdatesAsync();
await CheckpointTests.RunRuntimeUpdatesAsync();
await CheckpointTests.RunRuntimeSwitchesAsync();
await CheckpointTests.RunObservationsAsync();
await CheckpointTests.RunObservedClicksAsync();
await CheckpointTests.RunExecutionTracesAsync();
await CheckpointTests.RunTraceReviewAsync();
await LifecycleTests.RunAsync();
await AuthorizationTests.RunAsync();
await InputArbitrationTests.RunAsync();
await InputInterventionTests.RunAsync();
await RawInputTests.RunAsync();
await NativeInputTakeoverTests.RunAsync();
Console.WriteLine("Lua bridge, scheduler and state/command bus tests passed");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task<ScriptExecutionResult> DriveToEndAsync(LuaRuntime runtime)
{
    ScriptExecutionResult result = await runtime.StepAsync();
    for (int step = 0; result.State == ScriptState.Yielded && step < 128; step++)
        result = await runtime.ResumeAsync();
    if (result.State == ScriptState.Faulted)
        throw new Exception("Visual Lua script faulted", result.Error);
    return result;
}

static CapturedFrame VisualFrame(bool visible)
{
    var pixels = new byte[4 * 3 * 4];
    if (visible) pixels[(2 * 4 + 2) * 4] = 200;
    return new CapturedFrame(pixels, 4, 3, 16);
}

static CapturedFrame LeakFrame()
{
    var pixels = new byte[64 * 64 * 4];
    pixels[0] = 200;
    return new CapturedFrame(pixels, 64, 64, 256);
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

sealed class SequenceCapture(Func<int, CapturedFrame?> frameFactory) : ICaptureService
{
    public int GrabCalls { get; private set; }
    public bool IsCapturing => true;
    public Task StartAsync(nint hwnd, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(frameFactory(++GrabCalls));
    }
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeTemplateMatch : ITemplateMatchService
{
    public int MatchCalls { get; private set; }
    public int LastWidth { get; private set; }
    public int LastHeight { get; private set; }
    public Task<IReadOnlyList<TemplateMatchResult>> MatchAsync(string targetImagePath,
        string templateImagePath, float threshold = 0.8f,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<TemplateMatchResult>> MatchFrameAsync(CapturedFrame frame,
        string templateImagePath, float threshold = 0.8f,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MatchCalls++;
        LastWidth = frame.Width;
        LastHeight = frame.Height;
        if (Path.GetFileName(templateImagePath) == "broken.png")
            throw new RugNativeException("Rug_MatchTemplate", RugStatus.ErrInvalidParam);
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
                if (frame.Pixels[y * frame.Stride + x * 4] == 200)
                    return Task.FromResult<IReadOnlyList<TemplateMatchResult>>([new(x, y, 0.95)]);
        return Task.FromResult<IReadOnlyList<TemplateMatchResult>>([]);
    }
}

sealed class FakeOcr : IOcrService
{
    public CapturedFrame? LastFrame { get; private set; }
    public string? LastLanguage { get; private set; }
    public OcrEngineType LastEngine { get; private set; }
    public Task<IReadOnlyList<OcrTextBlock>> RecognizeAsync(string imagePath, OcrEngineType engineType, string? modelId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OcrTextBlock>>([]);
    public Task<IReadOnlyList<OcrTextBlock>> RecognizeFrameAsync(CapturedFrame frame, OcrEngineType engineType, string? modelId = null, CancellationToken cancellationToken = default)
    {
        LastFrame = frame;
        LastLanguage = modelId;
        LastEngine = engineType;
        return Task.FromResult<IReadOnlyList<OcrTextBlock>>([new("ready", 1, new Rect(0, 0, 1, 1))]);
    }
    public IReadOnlyList<string> ListPaddleModelIds() => [];
}

sealed class FakeInput : IInputService
{
    public Func<Task>? OnMove,OnDown,OnUp=null;
    public int MouseDownCalls,MouseUpCalls;
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
    { X = x; Y = y; MoveCalls++; return OnMove?.Invoke()??Task.CompletedTask; }
    public Task MoveMouseRelativeAsync(int dx, int dy, TrajectoryType trajectory = TrajectoryType.CubicBezier, bool smooth = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClickAsync(MouseButton button = MouseButton.Left, int holdMs = 0, CancellationToken cancellationToken = default)
    { Button = button; return Task.CompletedTask; }
    public Task MouseDownAsync(MouseButton button, CancellationToken cancellationToken = default) {MouseDownCalls++;return OnDown?.Invoke()??Task.CompletedTask;}
    public Task MouseUpAsync(MouseButton button, CancellationToken cancellationToken = default) {MouseUpCalls++;return OnUp?.Invoke()??Task.CompletedTask;}
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
