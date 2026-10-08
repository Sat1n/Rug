using System.Diagnostics;

// Diagnostic harness only. Caller owns resources and cleans them on any failure.
internal sealed class HookRawStartup
{
    internal sealed record Step(string Name, long BeforeTicks, long AfterTicks, bool Succeeded, string? ErrorType);
    private readonly List<Step> _steps = [];
    private int _started;
    internal Step[] Snapshot => _steps.ToArray();
    internal static void Validate(string stage, string module, string? order)
    {
        if (order is null) return; // Existing diagnostic paths retain immediate Hook installation.
        if (stage != "keyboard-native-callback" || module != "callback" || order is not ("hook-first" or "raw-first"))
            throw new ArgumentException("--startup-order hook-first|raw-first requires keyboard-native-callback and --hook-module callback");
    }
    // @shape At most two detached phase records; second phase starts only after first readiness succeeds.
    // @source Rug.UI.Core/Native/RawInputDiagnosticWindow.cs#class:RawInputDiagnosticWindow
    internal async Task StartAsync(string order, Func<Task> installHook, Func<Task> registerRaw)
    {
        Validate("keyboard-native-callback", "callback", order);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) throw new InvalidOperationException("Startup evidence cannot be reused");
        async Task Run(string name, Func<Task> action)
        {
            long before = Stopwatch.GetTimestamp();
            try { await action().WaitAsync(TimeSpan.FromSeconds(5)); _steps.Add(new(name, before, Stopwatch.GetTimestamp(), true, null)); }
            catch (Exception error) { _steps.Add(new(name, before, Stopwatch.GetTimestamp(), false, error.GetType().Name)); throw; }
        }
        if (order == "hook-first") { await Run("hook-install", installHook); await Run("raw-register-ready", registerRaw); }
        else { await Run("raw-register-ready", registerRaw); await Run("hook-install", installHook); }
    }
    internal void Print(string order)
    {
        foreach (var (step, index) in _steps.Select((s, i) => (s, i)))
            Console.WriteLine($"STARTUP PHASE: order={order} ordinal={index + 1} phase={step.Name} beforeTicks={step.BeforeTicks} afterTicks={step.AfterTicks} frequency={Stopwatch.Frequency} succeeded={step.Succeeded} errorType={step.ErrorType ?? "None"}");
    }
}
