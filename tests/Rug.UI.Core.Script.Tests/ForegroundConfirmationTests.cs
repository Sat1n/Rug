using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Security;
using Rug.UI.Core.Services;

internal static class ForegroundConfirmationTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("Foreground confirmation: " + message); }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
    private sealed class Windows : IInputWindowValidator
    {
        public bool Valid = true;
        public InputTarget Bind(nint hwnd) => new(hwnd, 1);
        public bool IsValid(InputTarget target) => Valid;
    }

    public static async Task RunAsync()
    {
        foreach (string scenario in new[] { "already", "delayed", "declined", "unconfirmed", "cancelled", "revoked", "target", "takeover" })
            await ScenarioAsync(scenario);
        Console.WriteLine("Foreground confirmation: bounded asynchronous activation, live checks, leases and Gateway deduplication passed");
    }

    private static async Task ScenarioAsync(string scenario)
    {
        var clock = new Clock();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        nint foreground = scenario == "already" ? 123 : 456;
        int activations = 0;
        var activation = new ForegroundWindowActivation(() => foreground,
            _ => { activations++; return scenario != "declined"; }, clock,
            async (_, ct) => { waiting.TrySetResult(); await resume.Task.WaitAsync(ct); });
        var windows = new Windows();
        var arbiter = new InputArbiter(windows);
        var manifest = new PluginManifest([Permission.ControlInput])
            { Id = "foreground-test", PluginDirectory = AppContext.BaseDirectory, TargetWindow = 123 };
        var authorization = new PluginAuthorization();
        TestAuthorizationHost.Approve(authorization, manifest);
        var gate = new PermissionInterceptor(manifest, NullLogger.Instance, authorization);
        var input = new FakeInput();
        await using var bridge = new InputBridge(input, gate, arbiter, activation.ConfirmAsync);
        await bridge.BindAsync(123, InputDeliveryMode.Win32SendInput);
        var identity = new RunIdentity(manifest.Id, Guid.NewGuid(), 1, Guid.NewGuid(), 123);
        var gateway = new AutomationGateway(NullLogger.Instance,
            getPlugin: (_, _) => Task.FromResult(manifest), authorization: authorization);
        Exception? failure = null;
        gateway.RegisterRun(identity, manifest, CancellationToken.None, async (_, _, ct) =>
        {
            try { await bridge.PressKeyAsync(66, 1, ct); return null; }
            catch (Exception error) { failure = error; throw; }
        });
        try
        {
            var caller = await gateway.CreateContextAsync(manifest.Id, "trusted-test-host", AutomationSource.PluginUI);
            var request = new OperationRequest(AutomationOperation.PressKey,
                JsonSerializer.SerializeToElement(new { key = 66, durationMs = 1 }), Guid.NewGuid(), scenario,
                OperationTarget.ForRun(identity), DateTimeOffset.UtcNow.AddSeconds(10));
            using var cancellation = new CancellationTokenSource();
            Task<OperationResult> pending = gateway.ExecuteAsync(caller, request, cancellation.Token);
            Task<IInputLease>? contender = null;
            Task? takeover = null;
            if (scenario is not ("already" or "declined"))
            {
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!pending.IsCompleted && input.KeyDownCalls == 0 && arbiter.Snapshot.Active == 1,
                    scenario + " sent input before confirmation or released its lease");
                contender = arbiter.AcquireAsync(InputOwner.ForDevTools("contender"), arbiter.BindTarget(123),
                    InputDeliveryMode.Win32PostMessage, () => { });
                Check(!contender.IsCompleted && arbiter.Snapshot.Waiting == 1,
                    scenario + " allowed conflicting input during foreground wait");
                if (scenario == "delayed") foreground = 123;
                if (scenario == "unconfirmed") clock.Advance(ForegroundWindowActivation.ConfirmationTimeout);
                if (scenario == "cancelled") cancellation.Cancel();
                if (scenario == "revoked") authorization.Revoke(PluginInstallation.From(manifest), [Permission.ControlInput]);
                if (scenario == "target") windows.Valid = false;
                if (scenario == "takeover") takeover = arbiter.TakeOverAsync();
                resume.TrySetResult();
            }
            OperationResult result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            bool success = scenario is "already" or "delayed";
            Check(success ? result.Status == OperationStatus.Success && input.KeyDownCalls == 1 && input.KeyUpCalls == 1
                : result.Status != OperationStatus.Success && result.OutcomeUncertain && input.KeyDownCalls == 0 && input.KeyUpCalls == 0,
                scenario + " incorrect delivery or outcome certainty");
            if (scenario is "declined" or "unconfirmed")
                Check(result is { Status: OperationStatus.Failed, Error: OperationError.ExecutionFailed } &&
                    failure?.Message.Contains("activationAccepted=" + (scenario == "declined" ? "False" : "True")) == true &&
                    failure.Message.Contains("foreground=0x1C8") && failure.Message.Contains(scenario == "declined" ? "declined" : "timed-out"),
                    scenario + " lost activation return/observed-window evidence");
            OperationResult duplicate = await gateway.ExecuteAsync(caller, request);
            // Revoked callers are rejected before looking up results; no service retry is allowed.
            bool retained = scenario == "revoked"
                ? duplicate is { Status: OperationStatus.Rejected, Error: OperationError.PermissionDenied, OutcomeUncertain: false }
                : duplicate.Status == result.Status && duplicate.OutcomeUncertain == result.OutcomeUncertain;
            Check(retained && gateway.RetainedOperationCount == 1 &&
                activations == (scenario == "already" ? 0 : 1) && input.KeyDownCalls == (success ? 1 : 0),
                scenario + " duplicate retried activation/input");
            if (contender is not null)
            {
                try { using var lease = await contender.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception error) when (error is OperationCanceledException or InputUnavailableException) { }
            }
            if (takeover is not null)
            {
                await takeover.WaitAsync(TimeSpan.FromSeconds(3));
                Check(arbiter.Snapshot.State == InputControlState.ManualControl, "cancelled activation blocked takeover");
            }
            // Gateway can return cancellation before its handler finishes. Observe its actual drain.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (gateway.ActiveWorkCount != 0) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            Check(arbiter.Snapshot is { Active: 0, Waiting: 0 }, scenario + " leaked resources");
        }
        finally { gateway.EndRun(identity.InstanceId); }
    }
}
