---
id: rug_script_tests
type: logic_node
inputs: [rug_ui_core]
outputs: []
tags: [lua, tests, input]
---

# Lua coroutine bridge tests

Opt-in `--hook-isolation-stage load|target|keyboard|both|both-raw|both-raw-split|target-raw|keyboard-raw|keyboard-split|keyboard-pump|keyboard-direct|keyboard-messages` supports the
[controlled comparison](../NativeInputProbe/ControlledStages.md). It sends no
input; both-raw modes add Raw Input after both Hooks; target-raw adds it without
managed Hooks. All report software/candidate
sample deltas and clean registration/subscription teardown. Normal diagnostics
retain both Hooks.
both-raw prints its HWND/PID and aggregate target counts for the native
ManagedTarget comparison. Its query protocol lives in the diagnostic Native
window; it sends no input and changes no production C-ABI.
both-raw-split moves both Hooks to a message-only window on a separate thread,
keeping the visible target and its metric protocol unchanged. THREAD TOPOLOGY
prints different target/Hook thread IDs; TARGET and Hook statuses/cleanup are
independent. Tests check actual thread separation, target delivery/protocol,
post-start Hook faults with live target/source, and three rounds of registration,
buffer and subscription cleanup. Message barriers do not certify Hook delivery.
target-raw keeps the split mode's visible target, idle message-only receiver
thread, Raw Input source and metric protocol, with neither managed Hook
installed. Its zero Hook counters are expected; only the native observer supplies
Hook verification. Real lifecycle tests cover both registration selections,
post-ready receiver faults, target messages and three cleanups per selection.
keyboard-raw uses the identical split target/receiver/source setup, adding only
the managed keyboard Hook; mouse Hook remains uninstalled. It prints the selected
Hooks, preserves keyboard deltas and intentionally reports zero managed mouse
Hook counters. Tests cover real keyboard-only startup/protocol, post-ready faults
with live target/source and three cleanup rounds, alongside both/no-Hook cases.
keyboard-split preserves keyboard-raw's visible target, independent keyboard-only
Hook receiver, metric protocol and native sender, but creates no Raw Input source.
It reports omitted collection and verifies zero registrations at startup/stop;
absence of raw counters is not successful software attribution. Three additional
real lifecycle rounds cover target delivery, receiver faults and cleanup without
Raw Input, giving 12 split lifecycles total. Manual AUTO remains separately required.
keyboard-pump retains the original receiver window/thread/timer/message loop but
skips device registration and ownership checks; it uses an internal diagnostic
seam, not IInputEventSource Ready. Native thread/window identity, timer/input/device
message counts and post-start faults remain visible; no capture/candidate counters
are claimed. Three real lifecycles assert thread separation, timer handling,
idempotent startup/disposal, restart generations, faults and preservation of another
receiver's registrations. See [steps and result branches](../NativeInputProbe/ReceiverRegistrationControl.md).
keyboard-direct uses the same diagnostic pump with normal registration/reading/
ownership/cleanup, but counts copied observations directly without RawInputEventSource,
its queue, consumer or subscriptions. Snapshots include owned registrations and
software/candidate deltas. Three registered lifecycles cover conflicts, malformed
WM_INPUT faults, generations and cleanup; direct aggregation tests preserve down/up,
merged sample counts, concurrency and detached snapshots. Real AUTO is NOT RUN by
the agent. Normal event-source diagnostics retain their original delivery path.
keyboard-messages retains direct-mode registration/ownership/window/thread/timer
and WM_INPUT DefWindowProc cleanup, omitting packet reading/parsing/tracking,
observation callbacks and device classification. Raw evidence is null/NOT COLLECTED,
never zero-candidate evidence. Message/cleanup totals and phase deltas remain
visible. Three real lifecycles check count-only malformed foreground/sink messages,
device messages, conflicts, visible ownership loss, replacement-owner protection,
generations and idempotent cleanup. Interactive AUTO remains NOT RUN by the agent.

Run `dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64` with the root-pinned .NET SDK `10.0.401`.
The console test uses fake capture, OCR and input services, so no native Rug.Core
DLL or target window is required. It verifies that sleep and capture yield without
blocking, resume carries frame/OCR values into Lua, stop cancels pending sleep,
pause preserves completed work, a bound input target receives click/key arguments,
and manifest permissions reject capture.

The same executable creates temporary plugin directories to check valid
`manifest.json`/`config.json` parsing, all four UI control types and typed defaults,
malformed JSON tolerance, entry path traversal rejection, sandbox global removal,
and audited `PermissionDeniedException` behavior.

Scheduler integration tests use real NLua with fake capture/input services to check
capture startup before `on_init`, periodic `on_tick`, `on_stop` cleanup, independent
pause/stop across three instances, and watchdog interruption of tight loops in
both `on_init` and `on_tick`. The script also attempts to replace the cancellation
callback, confirming the watchdog uses its private reference. A pending capture
startup is canceled and cleaned up when the scheduler is disposed.

Anomaly tests check `agent` permission denial, a Lua-triggered yield and task
fault, the per-instance warning and cleanup, UTF-8 reason/context plus Lua
traceback in JSON, a null `agent_resolution` placeholder, and the PNG's actual
RGBA pixel data decoded from its compressed image payload.

Task 2.2 tests use a real Win32 test HWND to compare client-to-screen mapping
against `MapWindowPoints`, and fake input services to verify permission denial,
client-coordinate forwarding, per-call SendInput/PostMessage mode selection,
manifest defaults, button selection, asynchronous key duration, cancellation
KeyUp, and rejection when foreground activation fails.

Task 2.3 tests use generated BGRA frames and fake OCR/template engines to verify
Lua hit coordinates, confidence, region offsets, OCR text/language, permission
denial, missing/invalid/escaping template paths, and coroutine polling for both
image appearance and timeout. A 1,000-cycle capture/match test confirms only the
current frame remains cached and older frames can be collected. The native
`TemplateMatchService.MatchFrameAsync` path pins the managed frame only for its
Rug.Core call. The fake-based test does not measure native heap usage; run the
`--visual-only` mode in [Rug.Core.CSharp.Tests](../Rug.Core.CSharp.Tests/README.md)
for a synthetic-BMP OpenCV match and 1,000 native calls with memory/handle checks.

Task 3.1.1 tests run concurrent state writers, exact change counts, deduplication,
null/default semantics, reentrant listeners, UI-dispatched versus synchronous UI
notifications, async named-command subscriptions, and a live NLua/C# round trip
through the same `CommandBus` instance.

Task 3.5-02 adds structural AppState/PluginConfig/InstanceState tests: same-plugin
and cross-plugin identical keys, concurrent scoped writes and revisions, explicit
null versus missing keys, detached arrays/events, oversized/deep/unsupported JSON,
stale UI notifications, and writes/callbacks after instance close. Scheduler tests
cover windowless launch inputs, per-plugin configuration startup snapshots,
generation/binding identity and a fresh state partition after restart.

Task 3.5-03 routes the existing real Lua suite through Automation Gateway.
`GatewayTests.cs` also exercises UI lifecycle and simulated Agent/MCP callers,
dual-instance services, permission policy replacement, forged grant data and
foreign capabilities, identity rejection with zero capture calls, 20 concurrent
duplicates and parameter conflicts, pre-expiration/pre-cancellation, ignored
cancellation and late Lua results. Tests cover bounded capacity, TTL expiry with
a controlled clock, pinned noncooperative work, sanitized failures and repeated
run cleanup. The windowless lifecycle test checks duplicate start, pause, resume
and stop through Gateway. See the [contract](../../Rug.UI.Core/AutomationGateway.md).

`GatewayRepairTests.cs` keeps an instance alive through capture A timeout or
explicit cancellation, successful capture B and late A completion, then checks
that OCR and matching still consume B. Parameter regressions cover missing
fields, wrong JSON types, integer/enum/threshold/region ranges and actual Lua
coercion attempts, asserting Rejected/InvalidParameters without uncertainty or
capture/OCR/matching/input calls. A real service InvalidOperationException remains
Failed/ExecutionFailed rather than being hidden by parameter classification.

Task 3.5-04 uses explicit `TestAuthorizationHost` setup for legacy positive cases.
`AuthorizationTests.cs` separately verifies fail-closed startup, missing-name
results, installation/caller ceilings, forged and unknown permissions, live
revocation stamps, external templates, queue/active cancellation, other-plugin
isolation, new identities after regrant, expanded-manifest reload rejection and
zero live authorization leases after repeated stops. Production has no automatic
approval flag; see [Authorization](../../Rug.UI.Core/Authorization.md).

Real Lua state/config reads also check nested arrays, objects, nulls and false,
and confirm they are ordinary Lua tables without CLR GetType access. Managed
JSON snapshots and StepFlowView reads remain unchanged.

Task 3.5-05A adds `InputArbitrationTests.cs`: controlled barriers assert the
window/global conflict matrix, conflict FIFO/capacity, queued cancellation,
revocation/stopping/window invalidation with zero input, composite click/key
leases, Gateway timeout with live native work, late cleanup/handle lifetime,
fault quarantine, other-plugin preservation and repeated takeover/subscription
cleanup. `TestAuthorizationHost` explicitly supplies a replaceable window
validator for fake HWNDs; production does not accept them.

`NativeInputTakeoverTests.cs` loads `x64/Debug/Rug.Core.dll` when available and
creates a dedicated message-pumped isolated window. Real PostMessage mouse and
keyboard messages verify click and mandatory KeyUp before manual control. It
cleans up that window and controller; no game or installed plugin is involved.
Missing Windows/native DLL prints NOT RUN rather than recording a pass.
See [shared input contract](../../Rug.UI.Core/InputArbitration.md).

`InputArbitrationTests.GatewayOutcomeAsync` exercises the real runtime Gateway
handler and bridge with deterministic move callbacks and queue barriers. It
checks post-move window invalidation and permission rejection, foreground
click/key rechecks and failed activation, pre-input rejection, queued rejection
and cancellation. Exact status/error/uncertainty, zero unstarted input, retained
duplicate results and empty queues/leases are asserted in nine scenarios.

Task 3.5-05B-01 adds `RawInputTests.cs` (included in the normal suite, or run
`--raw-input-tests`): x86/x64 packet layouts, down/up/repeat, buttons/wheels,
invalid native data, startup/stop cancellation, new generations, detached values,
blocked/faulting subscribers, 10,000-motion coalescing, explicit critical overflow,
cleanup faults and real Windows registration conflict/replacement/repeated cleanup.

`--raw-input-self-test` uses a dedicated native window and the real `InputService`
DLL path for four independently evaluated rounds: SendInput/F8, SendInput/B,
PostMessage/F8 and PostMessage/B, each with move/click/key/release.
F8/B comparison distinguishes missing/interfered rounds, selective key failure
and both missing; it never substitutes for the per-round Hook checks. It checks actual target
messages, independently counts injected/unflagged hook activity, and reports
interference without calling it a self-input failure. Missing focus/native DLL,
incomplete evidence or source failure never pass silently.
`--raw-input-diagnostic` adds an interactive, same-session HUMAN BEFORE -> AUTO ->
HUMAN AFTER procedure with a 5-second focus-switch countdown and aggregate counts only.
Tag regressions cover x86/x64 keyboard/mouse, unknown tags, zero-device untagged
input and hook flags/extra-info layouts. A software PASS requires complete tagged
raw and hook evidence; consumer counts drain before evaluation.
Probe regressions exercise negative/non-action nCode without pointer reads,
independent injected/tagged intersections (including taggedUnflagged), null-data
parse errors, detached snapshots/phase deltas, separate processing/chain timing,
registration error preservation and post-Ready thread failure/cleanup/restart.
Stage output now splits raw software keyboard/mouse plus keyboard down/up and
prints the actual loaded core module path/process ID/size/file SHA-256 before
Hook startup. No hashing or module enumeration occurs in message callbacks.
See [paired native comparison audit](../NativeInputProbe/HookComparison.md) for
the user's passing reversed-order session and unresolved order correlation.
Stage output also
reports installation identity, error codes and thread exit/failure. Pump/barrier
success never implies hook health; no input emission changes are made here. Ordinary application
startup never enables it. See [commands, evidence and pending human acceptance](../../Rug.UI.Core/RawInputEvents.md).
