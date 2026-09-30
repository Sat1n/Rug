using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

internal static class GatewayRepairTests
{
    public static async Task RunAsync(string root)
    {
        await LateFrameAsync(root, timeout: true);
        await LateFrameAsync(root, timeout: false);
        await InvalidParametersAsync(root);
        Console.WriteLine("Gateway late-frame cache and parameter-boundary regressions passed");
    }
    private static OperationRequest Request(AutomationOperation operation, RunIdentity identity, object parameters) =>
        new(operation, JsonSerializer.SerializeToElement(parameters), Guid.NewGuid(), "gateway-repair",
            OperationTarget.ForRun(identity), DateTimeOffset.UtcNow.AddSeconds(5));
    private static async Task LateFrameAsync(string root, bool timeout)
    {
        var capture = new LateCapture();
        var ocr = new FakeOcr();
        var matcher = new FakeTemplateMatch();
        var manifest = new PluginManifest([Permission.Capture, Permission.VisionOcr, Permission.VisionMatch])
            { Id = "late-frame", PluginDirectory = root };
        var identity = new RunIdentity(manifest.Id, Guid.NewGuid(), 1, null, 0);
        var gateway = new AutomationGateway(NullLogger.Instance, getPlugin: (_, _) => Task.FromResult(manifest));
        await using var runtime = new LuaRuntime(capture, ocr, new FakeInput(), NullLogger<LuaRuntime>.Instance, templateMatcher: matcher);
        runtime.ConfigureGateway(gateway);
        runtime.ConfigureState(identity, new Dictionary<string, object?>(), new Dictionary<string, object?>());
        string script = Path.Combine(root, "cache.lua");
        await File.WriteAllTextAsync(script, "function on_tick() end");
        await runtime.InitializeAsync(script, manifest);
        var caller = await gateway.CreateContextAsync(manifest.Id, "ui", AutomationSource.PluginUI);
        using var cancellation = new CancellationTokenSource();
        var first = Request(AutomationOperation.Capture, identity, new { });
        if (timeout) first = first with { Deadline = DateTimeOffset.UtcNow.AddMilliseconds(200) };
        Task<OperationResult> pending = gateway.ExecuteAsync(caller, first, cancellation.Token);
        await UntilAsync(() => capture.Calls == 1);
        if (!timeout) cancellation.Cancel();
        Expect((await pending).Status == (timeout ? OperationStatus.TimedOut : OperationStatus.Cancelled), "A did not terminate as expected");
        Expect((await gateway.ExecuteAsync(caller, Request(AutomationOperation.Capture, identity, new { }))).Status == OperationStatus.Success,
            "B capture failed on still-live instance");
        capture.Late.TrySetResult(new CapturedFrame([0, 0, 0, 255], 1, 1, 4));
        await UntilAsync(() => gateway.ActiveWorkCount == 0);
        var ocrResult = await gateway.ExecuteAsync(caller, Request(AutomationOperation.Ocr, identity, new { language = "zh-CN" }));
        Expect(ocrResult.Status == OperationStatus.Success && ReferenceEquals(ocr.LastFrame, capture.Fresh), "late A overwrote B before OCR");
        await File.WriteAllBytesAsync(Path.Combine(root, "cache-template.png"), [137, 80, 78, 71]);
        var match = await gateway.ExecuteAsync(caller, Request(AutomationOperation.FindImage, identity, new { path = "cache-template.png" }));
        Expect(match is { Status: OperationStatus.Success, Value: ImageFindResult { X: 2 } } && matcher.LastWidth == 3,
            "matching did not retain fresh B after late A");
        Expect(runtime.State == ScriptState.Ready && gateway.RegisteredRunCount == 1, "test accidentally stopped the instance");
    }
    private static async Task InvalidParametersAsync(string root)
    {
        var capture = new LateCapture();
        var ocr = new FakeOcr();
        var matcher = new FakeTemplateMatch();
        var input = new FakeInput();
        var manifest = new PluginManifest([Permission.Timer, Permission.Capture, Permission.VisionOcr, Permission.VisionMatch, Permission.ControlInput],
            TargetWindow: 123) { Id = "parameter-test", PluginDirectory = root };
        var identity = new RunIdentity(manifest.Id, Guid.NewGuid(), 1, Guid.NewGuid(), 123);
        var gateway = new AutomationGateway(NullLogger.Instance, getPlugin: (_, _) => Task.FromResult(manifest));
        await using var runtime = new LuaRuntime(capture, ocr, input, NullLogger<LuaRuntime>.Instance,
            new FakeCoordinateMapper(), _ => true, matcher);
        runtime.ConfigureGateway(gateway);
        runtime.ConfigureState(identity, new Dictionary<string, object?>(), new Dictionary<string, object?>());
        await runtime.InitializeAsync(Path.Combine(root, "cache.lua"), manifest);
        var caller = await gateway.CreateContextAsync(manifest.Id, "adapter", AutomationSource.Mcp);
        (AutomationOperation Operation, object Parameters)[] cases =
        [
            (AutomationOperation.Sleep, new { ms = "bad" }), (AutomationOperation.Sleep, new { }),
            (AutomationOperation.Sleep, new { ms = true }), (AutomationOperation.Sleep, new { ms = (object?)null }),
            (AutomationOperation.Sleep, new { ms = -1 }), (AutomationOperation.Sleep, new { ms = 1.5 }),
            (AutomationOperation.Sleep, new { ms = long.MaxValue }),
            (AutomationOperation.Click, new { x = 1 }), (AutomationOperation.Click, new { x = "bad", y = 2 }),
            (AutomationOperation.Click, new { x = -1, y = 2 }), (AutomationOperation.Click, new { x = 1, y = 1.5 }),
            (AutomationOperation.Click, new { x = 1, y = 2, button = "left" }),
            (AutomationOperation.Click, new { x = 1, y = 2, button = 999 }),
            (AutomationOperation.Click, new { x = 1, y = 2, mode = 999 }),
            (AutomationOperation.PressKey, new { }), (AutomationOperation.PressKey, new { key = "27" }),
            (AutomationOperation.PressKey, new { key = 0 }), (AutomationOperation.PressKey, new { key = 256 }),
            (AutomationOperation.PressKey, new { key = 27, durationMs = "100" }),
            (AutomationOperation.PressKey, new { key = 27, durationMs = -1 }),
            (AutomationOperation.PressKey, new { key = 27, durationMs = 60001 }),
            (AutomationOperation.FindImage, new { }), (AutomationOperation.FindImage, new { path = 1 }),
            (AutomationOperation.FindImage, new { path = " " }),
            (AutomationOperation.FindImage, new { path = "x", threshold = "bad" }),
            (AutomationOperation.FindImage, new { path = "x", threshold = 1.1 }),
            (AutomationOperation.FindImage, new { path = "x", threshold = double.MaxValue }),
            (AutomationOperation.FindImage, new { path = "x", region = "bad" }),
            (AutomationOperation.Ocr, new { language = 42 }), (AutomationOperation.Ocr, new { engine = "winrt" }),
            (AutomationOperation.Ocr, new { engine = 999 }), (AutomationOperation.Ocr, new { region = new { } }),
            (AutomationOperation.Ocr, new { region = new { X = "0", Y = 0, Width = 1, Height = 1 } }),
            (AutomationOperation.Ocr, new { region = new { X = 0, Y = 0, Width = 0, Height = 1 } }),
            (AutomationOperation.Ocr, new { region = new { X = -1, Y = 0, Width = 1, Height = 1 } }),
            (AutomationOperation.Ocr, new { region = new { X = int.MaxValue, Y = 0, Width = 1, Height = 1 } }),
            (AutomationOperation.Capture, "wrong root"), (AutomationOperation.Stop, "wrong root")
        ];
        int bindings = input.TargetModes.Count;
        var original = Request(AutomationOperation.Sleep, identity, new { ms = 0 });
        Expect((await gateway.ExecuteAsync(caller, original)).Status == OperationStatus.Success, "valid timer failed");
        var inconsistent = original with { Parameters = JsonSerializer.SerializeToElement(new { ms = "bad" }) };
        Expect((await gateway.ExecuteAsync(caller, inconsistent)).Error == OperationError.DeduplicationConflict,
            "parameter validation weakened existing duplicate-conflict semantics");
        foreach (var test in cases)
        {
            var request = Request(test.Operation, identity, test.Parameters);
            OperationResult result = await gateway.ExecuteAsync(caller, request);
            Expect(result is { Status: OperationStatus.Rejected, Error: OperationError.InvalidParameters, OutcomeUncertain: false },
                "incorrect parameter outcome: " + test.Operation + " " + request.Parameters);
        }
        Expect(capture.Calls == 0 && ocr.LastFrame is null && matcher.MatchCalls == 0 && input.MoveCalls == 0 &&
            input.Key == 0 && input.KeyDownCalls == 0 && input.KeyUpCalls == 0 && input.TargetModes.Count == bindings && gateway.ActiveWorkCount == 0,
            "invalid parameters reached an instance service");
        foreach (string call in new[] { "rug.sleep('1')", "rug.sleep(nil)", "rug.sleep(true)", "rug.sleep(1.5)",
            "rug.click(nil, 2)", "rug.press_key(27, '100')", "rug.ocr(nil, 42)", "rug.find_image('cache-template.png', '0.8')" })
        {
            string script = Path.Combine(root, "invalid-args.lua");
            await File.WriteAllTextAsync(script, "function on_tick() " + call + " end");
            var luaIdentity = new RunIdentity(manifest.Id, Guid.NewGuid(), 2, Guid.NewGuid(), 123);
            var luaInput = new FakeInput();
            var luaOcr = new FakeOcr();
            await using var lua = new LuaRuntime(capture, luaOcr, luaInput, NullLogger<LuaRuntime>.Instance,
                new FakeCoordinateMapper(), _ => true, matcher);
            lua.ConfigureGateway(gateway);
            lua.ConfigureState(luaIdentity, new Dictionary<string, object?>(), new Dictionary<string, object?>());
            await lua.InitializeAsync(script, manifest);
            Expect((await lua.StepAsync()).State == ScriptState.Faulted && gateway.Records.Any(record =>
                record.Target.InstanceId == luaIdentity.InstanceId && record.Error == OperationError.InvalidParameters && !record.OutcomeUncertain),
                "Lua coercion bypassed parameter validation: " + call);
            Expect(capture.Calls == 0 && luaOcr.LastFrame is null && matcher.MatchCalls == 0 && luaInput.MoveCalls == 0 && luaInput.Key == 0,
                "invalid Lua arguments reached a service");
        }
        // A real service InvalidOperationException must remain an execution failure.
        capture.Fail = true;
        Expect((await gateway.ExecuteAsync(caller, Request(AutomationOperation.Capture, identity, new { }))) is
            { Status: OperationStatus.Failed, Error: OperationError.ExecutionFailed, OutcomeUncertain: true }, "service fault was hidden as a parameter error");
    }
    private static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private sealed class LateCapture : ICaptureService
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Fail { get; set; }
        public TaskCompletionSource<CapturedFrame?> Late { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CapturedFrame Fresh { get; } = new([0, 0, 0, 255, 0, 0, 0, 255, 200, 0, 0, 255], 3, 1, 12);
        public bool IsCapturing => true;
        public Task StartAsync(nint hwnd, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            if (Fail) throw new InvalidOperationException("Actual service failure");
            return call == 1 ? Late.Task : Task.FromResult<CapturedFrame?>(Fresh);
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
