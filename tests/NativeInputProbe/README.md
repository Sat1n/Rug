---
id: rug_native_input_probe
type: logic_node
inputs: [rug_core]
outputs: []
tags: [diagnostics, input, hooks, native]
---

# Independent x64 native input probe

See [paired-session evidence and source audit](HookComparison.md) for the passing
reversed-order session and the unresolved install-order correlation. CoreInput
prints the actual loaded DLL path; the runner labels its pre-run SHA-256 separately.

[Pump](main.cpp#class:Pump) uses C++ Win32 callbacks, a dedicated hook message
thread and a separate isolated target-window thread. It does not use CLR,
managed diagnostic callbacks or register Raw Input devices. Hook installation
uses WH_KEYBOARD_LL/WH_MOUSE_LL with immediate per-call error capture. Negative
nCode is counted and forwarded without dereferencing lParam. Every event is
forwarded; no listener pause, ignore window or automatic reinstallation exists.

[Counter](main.cpp#class:Counter) reports entry/actionable counts, independent
injected/unflagged and tag/intersection counts, parse errors and separate own /
CallNextHookEx total/max QPC ticks. Maxima are lifetime values even in delta lines.
Native offsetof static assertions verify x64 keyboard/mouse flags and extra-info
layout. Snapshot reads are detached atomic aggregates, not cross-thread atomic
transactions. Only known **tagged synthetic** F8/B down/up counts are separated;
no human key sequences, text or full mouse trajectories are printed or saved.

Each receiver has a generation, installation OS thread ID, architecture, install
results and thread fault/exit reason. Successful installation or a message barrier
is not proof of continuing Hook health. Post-start failures survive readiness,
cleanup failures prevent success, and ordinary exit removes hooks/windows/classes
and joins threads. Input and waits run in the console harness, never a host UI.

[CoreInput](main.cpp#class:CoreInput) loads x64/Debug/Rug.Core.dll next to the probe
using the current **unchanged** C-ABI. Moves, click, F8/B down/up and button release
use real Rug.Core Win32InputController emitters with the existing 0x52554749 tag.
The probe never edits production input or injects into a game/existing app. The
interactive mode refuses foreground input unless its own isolated target is
already foreground; it does not bypass Windows activation policy. Background
PostMessage remains confined to that window. No host grants/takeover are changed.

## Build and modes

Requires Visual Studio v145 x64 C++ tools and Windows SDK. From repository root:

```powershell
.\tests\run_native_input_probe.ps1 -Mode Test
.\tests\run_native_input_probe.ps1 -Mode Human
```

The runner locates MSBuild via vswhere and builds only NativeInputProbe.vcxproj.
Output is ignored x64/Debug/Rug.NativeInputProbe.exe. It has no solution or
Rug.Core project reference, so it does not rebuild or change production DLLs.
Human/SelfTest need the existing x64/Debug/Rug.Core.dll and dependencies.

| Mode | Behavior |
|---|---|
| Build | Build only |
| Test | Deterministic keyboard/mouse classification, layout/timing, post-ready fault, generation and repeated cleanup tests; no synthetic input |
| Human (default) | HUMAN BEFORE -> AUTO F8/B in both modes -> HUMAN AFTER |
| SelfTest | Same native software rounds without human acceptance; denied foreground is NOT RUN |
| Observe | Passive independent native Hook observer, no synthetic input or DLL needed |

### First run alone

1. Close other probe processes and do not run other Rug input tasks.
2. Run `-Mode Human`. Use the **Rug independent native probe** white window for
   normal keyboard/mouse input, then another app; verify ordinary shortcuts work.
3. Return to its terminal and type `next`. In the 5-second countdown, activate
   the native white target by its title bar; release keys/mouse before AUTO.
4. Keep hands off throughout all four native software rounds. Each prints native
   keyboard/mouse deltas, tagged tested-key down/up and actual target down/up.
5. Repeat human keyboard/mouse input during HUMAN AFTER, type `done` and retain
   the output. Thread generations must stay unchanged and cleanup must be clean.
   Missing BEFORE human callback evidence keeps the session INCONCLUSIVE, even
   if software rounds continue for diagnosis; it cannot become accepted.

Native SendInput Hook verification requires actual target messages, tagged
injected key >=2, tagged injected mouse >=5, tested-key down/up >=1 each, no
parse/thread fault and no untagged-unflagged interference. PostMessage requires
target delivery and no tagged hook input; ordinary user interference is NOT RUN.
This is **independent Hook verification**, not Raw Input candidate acceptance.
Exit 0 means checks passed (Observe means clean passive observation only), 2 means
missing/inconclusive/interfered evidence; failures exit 1. Human actions are NOT
RUN by the development agent and cannot be replaced with fake events.

### Then compare against managed input in two terminals

1. Start fresh terminal A with `-Mode Observe`. Do not run native Human at the
   same time. The observer's spare target stays hidden and sends no input.
2. Start terminal B with the existing managed command:

   ```powershell
   dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --raw-input-diagnostic
   ```

3. Perform managed HUMAN BEFORE. In A type `baseline`, then return to B, type
   `next` and activate the **managed** white window within its 5-second countdown.
4. Wait hands off for managed AUTO to complete. In A type `snapshot`. Compare
   its tagged F8/B down/up and taggedInjected counters against B's per-key deltas.
   Observer phase unflagged totals naturally include terminal/focus human input;
   they are not an interference verdict for the managed AUTO round.
5. Complete managed HUMAN AFTER and `done`; verify observer still sees human
   callbacks, then type `snapshot` and `done` in A. Both processes must clean up.

A native observer that sees the *same managed sender's* tagged keyboard events
while the managed Hook does not narrows investigation to managed probe/thread or
hook-chain differences. If neither sees them while both see humans, investigate
shared input path/environment/Hook chain; do not simply blame Windows. If only
standalone native sending works, sender/process context is also a variable.
Initial comparison installs native before managed; any reversed-order comparison
must use fresh processes and be explicitly labeled. User-operated G HUB/PowerToys
comparisons change one condition at a time. No automatic reinstall masks failure.

## Current verification

Native strict x64 build and Test mode pass. Real PostMessage/F8 and PostMessage/B
pass with actual target down/up=1/1 and zero native Hook input. Agent SendInput
rounds are NOT RUN because the native target is not foreground. Human standalone
and two-process comparison remain NOT RUN / pending user operation. These results
do not locate the managed keyboard-probe cause or accept automatic takeover.
