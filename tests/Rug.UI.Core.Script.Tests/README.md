---
id: rug_script_tests
type: logic_node
inputs: [rug_ui_core]
outputs: []
tags: [lua, tests, input]
---

# Lua coroutine bridge tests

08A-01 adds `--observation-tests` (also in the full suite): host-only identity,
frame/reference/cache provenance, held A while B publishes, padded stride/ROI,
global byte/operation/frame/session limits, clock jumps, cancellation/Stop/late
capture and actual Lua metadata compatibility/cleanup. Existing 07A tests retain
the frame through paused UI-only updates; switching tests require a new frame.
`--observation-native` separately uses actual WGC, WinRT OCR and OpenCV on one
isolated window with matching source IDs; no fake or human-input verdict is used.
See [observation contract](../../Rug.UI.Core/Observations.md) for limits and commands.

07B-02 adds `--runtime-switch-tests` (also in the full suite): actual Lua/real files,
single/double AFTER-only, host rule/config/permission/node/hash/action checks,
preparation rollback, post-stop scene failure, real fault preservation, direct
original-token cancellation barriers, concurrent/deduplicated switches, paused
continuation, uncooperative capture lifetime, UI staging failures and takeover.
The suite also holds UI disposal while a restored member faults and another is
still restoring, and holds shutdown cancellation propagation across the final
publication boundary. Run either deterministic race independently with
`--runtime-switch-race ui-cleanup-fault` or `--runtime-switch-race shutdown-commit`.
Peers stop before UI disposal finishes; shutdown prevents group publication and
replacement input. Tests use actual source/parent identities, not enumeration order.
`--runtime-switch-ui` uses real WinUI/WGC for success, inherited Pause and red-scene
failure. `--runtime-switch-manual` is the self-contained user fixture with update/
red/green/done commands. See [contract/manual steps](../../Rug.UI.Core/RuntimeSwitch.md).

07B-01 adds `--runtime-update-tests` to the default full suite: actual Lua
BEFORE/checkpoint/AFTER with real files and Fake services, history exclusion,
complete multi-run reservation, timeout rollback, Pause/Stop/revoke/candidate
and late IO barriers, original cancellation propagation and repeated cleanup.
`--runtime-update-late-tests` isolates cancelled-A/new-B validation races for
rejected, timed-out, cancelled and committed results. B stays waiting with the
same revision, then freezes on its own next valid checkpoint; cancellation resumes
the same Lua coroutine once and releases plan monitors/subscriptions.
`--runtime-update-ui` separately runs actual WinUI/real WGC against a fixture-owned
green target; no human/input-source acceptance is inferred. See
[runtime preparation](../../Rug.UI.Core/RuntimeUpdates.md).

07A adds --plugin-update-tests and the default suite: immutable bounded snapshots,
compile-only prevalidation, failed-update old-run/UI/catalog retention, UI-only
pause/state preservation, pending config/code/permission updates, revocation,
real recovery-state barriers, fixed startup code, rollback/subscription disposal,
late-save and post-validation disk-change gates, entry compilation and queue limits.
See [snapshot update contract](../../Rug.UI.Core/HotReload.md); no update restarts Lua.

06C-01 adds `--checkpoint-tests` and the default full suite: actual Lua, trusted
Fake observation, real filesystem first-create/replace/integrity and separate
same-process/child-process reopen evidence. Controlled barriers check Stop before
and after publish, noncooperative capture/IO, genuine faults, old-record retention,
cancel/timeout and late results. Source/install/config/code identities, quota,
corruption/partial/temp/version/JSON tests and shipped exact-code node are included.
See [checkpoint contract and commands](../../Rug.UI.Core/Checkpoints.md).
Original-token barriers block cancellation propagation before/after publication
admission: pre-decision cancellation retains the old file without invoking the
publisher; post-decision cancellation waits for and preserves actual publication.
Exact-number regressions distinguish tiny/high-precision/large-integer config
values, equivalent decimal forms and checksum-preserving file tampering. Reopen
keeps lexical numeric data; legacy v1 is unsupported and not silently replaced.
Real-file version tests cover 2147483648, 2.5, wrong types/missing fields and
unchanged bytes; valid unknown integers remain unsupported, and a real exclusive
file lock must surface IOException rather than a corruption result.
06C-02 adds [checkpoint startup](../../Rug.UI.Core/CheckpointStartup.md): actual Lua
on_restore/new identity/origin; no init/BEFORE replay; source/descendant guards,
dedup/conflicts; fresh observation and late result/Stop/cancel/version checks.
`--checkpoint-restore-tests` runs the targeted suite and an actual restarted child
with fresh explicit test grants and Fake capture/input (not WGC/human evidence).
Disk-config barriers cover changed/created/deleted defaults, absent versus empty,
the final startup boundary, and genuine exclusive-lock IO failure: zero AFTER
input, preserved saved record, and fresh selection reparses current defaults.
`--checkpoint-restore-manual` owns a persistent controlled target while restarting
WinUI, preserving files and requiring new grants/binding. Human checks remain separate.
The independent Hook gap is unchanged.

Task 3.5-06B-01 adds `--recovery-tests` to the default suite: actual Lua init/tick
locals and explicit result tables; frozen mainline; missing/false validators;
Fake suggestions, timeouts/late results; Stop versus advice/validation/native
work; concurrent/deduplicated continuation; identities/grants/target/reload;
planned handoff, new epoch and unchanged unexpected takeover; repeated cleanup.
`--recovery-native` uses real Rug.Core/PostMessage, production RawInputEventSource
and a self-owned red/green window with fresh WGC validation and PNG/JSON. The host
changes its panel; this does not count as human acceptance. `--recovery-ui` adds
actual WinUI consent/crosshair/selection/Verify/Stop automation via
`Rug.UI.Tests/RecoveryUiSmoke.ps1`; `--recovery-manual` leaves actions to the user.
Green validation must succeed in one request, without host retry loops. Fake
monotonic clock/barriers cover red/red/green, bounded persistent-red rejection,
cancel/Stop/deadline, capture faults, late green frames and retained deduplication.
Final-registry barriers test cancellation, validation/request expiry without timer
propagation, Stop, validator replacement, real lookup failure and successful commit
against actual Lua/Fake input. Cancelled commitments send zero keys and retain
needs_human metadata. Paused-wake barriers separate Stop from explicit resume and
retain genuine failures; the actual scheduler stops once without resume validation.
See [commands, contracts and exact human steps](../../Rug.UI.Core/Recovery.md).
Frozen reversal binaries/records are unchanged. Independent Hook gap stays open.

Task 3.5-06A adds `--lifecycle-tests` (also in the normal suite): actual Lua and
controlled barriers for init/tick, sleep/vision, input/KeyUp, admitted queues,
late native work after Gateway timeout, Stop priority, independent identities,
resume authority/target checks, startup stop and cleanup failures. Assertions
distinguish request acceptance from confirmed Paused. Epoch cancellation and
unexplained Lua Stopped regressions remain. See
[lifecycle contract and UI/manual verification](../../Rug.UI.Core/Lifecycle.md).
Stop-admission barriers hold the Stopping notification before token cancellation:
new UI/Lua/Agent/MCP requests and previously queued bridge/arbiter/Gateway work
must not execute. Tests retain necessary KeyUp, reject late Resume/terminal work,
deduplicate refused requests and check cancellation callbacks from another thread
can query/admit without a lock deadlock. Only Fake services send input in these tests.
Repeated Gateway Stop tests retain noncooperative work and final-disposal barriers,
verify different IDs join one cleanup and same IDs share original pending/timeout
results. Forged identities/installations and live policy denial still reject
before deduplication; terminal picker removal cannot shortcut host completion,
and finished runs retain no lifecycle/entry/lease records.
Real Lua Stop-classification barriers cover synchronous and awaited admission
refusals and pause-deferred waits without token propagation. Lua/permission errors,
unrelated service cancellation and KeyUp cleanup failure retain Faulted semantics;
host Stop refusals cleanly stop without another action.
The anomaly admission barrier blocks Stop notification before token propagation
and then lets actual Lua reach resolve_anomaly: zero frame calls, one on_stop and
normal Stopped. Permission denial remains Faulted with no capture; a real
AnomalyLogger directory/file IO failure remains Faulted after capture. Previously
admitted anomaly work still drains through the existing regression.

Opt-in `--hook-isolation-stage load|target|keyboard|both|both-raw|both-raw-split|target-raw|keyboard-raw|keyboard-split|keyboard-pump|keyboard-direct|keyboard-messages|keyboard-messages-mouse|keyboard-messages-keyboard|keyboard-native-messages|target-native-messages|keyboard-native-callback` supports the
[controlled comparison](../NativeInputProbe/ControlledStages.md). It sends no
input; both-raw modes add Raw Input after both Hooks; target-raw adds it without
managed Hooks. Packet-reading stages report software/candidate deltas; count-only
or omitted collection stages report NOT COLLECTED. All check cleanup. Normal diagnostics
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
keyboard-messages-mouse retains the count-only setup and managed keyboard Hook,
registering only mouse usage 2. Snapshots show keyboard/mouse registration selection;
ownership queries require exactly one owned mouse and no keyboard or page-wide entry.
Three additional lifecycles verify this selection, conflicts, unexpected keyboard
registration faults, replacement protection and complete cleanup. Raw attribution
remains NOT COLLECTED; production Run always registers/reads both classes.
keyboard-messages-keyboard instead omits only mouse registration from
keyboard-messages, keeping usage 6 and the independent managed keyboard Hook.
Actual ownership must show one keyboard and no mouse/page-wide entry. Three more
lifecycles cover conflicts, unexpected mouse ownership faults and cleanup, giving
nine count-only lifecycles across both/single-class selections. Raw evidence stays
NOT COLLECTED; paired AUTO/HUMAN remains NOT RUN by the agent.
keyboard-native-messages replaces only the keyboard-only count receiver with a
[same-process native implementation](../NativeInputProbe/NativeReceiverControl.md).
It calls a test-only DLL on the same dedicated managed Thread topology, keeping
target/Hooks and sender intact. There are no native-to-managed message callbacks;
startup/barriers poll detached scalar snapshots. Three native lifecycles reuse
the managed assertions for flags/ownership, malformed count messages, conflicts,
foreign-registration preservation and cleanup; unstarted disposal is covered.
Build the DLL with tests/run_native_input_probe.ps1 -Mode Build; absent DLL means
these native tests are explicitly NOT RUN. Paired AUTO remains separate.
target-native-messages omits only the managed keyboard Hook installation from
keyboard-native-messages, retaining its idle message-only window/thread and the
same native keyboard count receiver. Both B Hook flags/counters must be false/zero;
only A supplies Hook evidence. Three additional native lifecycles assert thread
separation, target query/delivery, exact registration and full fault/cleanup behavior.
This is not disabling Hook verification in the original diagnostics or A.
keyboard-native-callback retains keyboard-native-messages' thread topology,
managed Hook thread/pump/target and unchanged receiver DLL, replacing only its
keyboard callback with a separate native count/forward DLL. See
[paired steps and branches](../NativeInputProbe/NativeHookCallbackControl.md).
Its default Hook hmod now uses the actual callback DLL, independently of the
window instance. `--hook-module original` selects the former main-module parameter
only in this stage; unsupported combinations fail before loading Core or creating
windows. [Module/send evidence control](../NativeInputProbe/InputEvidenceControl.md)
defines fresh original/callback/original runs. Automated tests cover three real
lifecycles per selection and v2 F8/B target down/up including system messages.
`--startup-order hook-first|raw-first` additionally requires this stage and
corrected callback hmod. Both conditions prepare target/Hook windows first and
post one installation command; the unchanged Raw phase includes receiver thread/
window startup and registration. [Phase contract and paired steps](../NativeInputProbe/StartupOrderControl.md)
define the matched baseline. HookRawStartupTests runs six real success and four
failure lifecycles, including registration conflicts, timestamps, duplicate
installation, foreign registration preservation and complete cleanup. No AUTO
is inferred from those tests; older modes retain immediate installation.
`--hook-observer` runs process C alone, with the same native keyboard callback
and corrected module on a message-only thread, no target/Core/Raw load or input.
Use `install` after B Raw readiness, then baseline/snapshot/done. Duplicate install
does not rehook; missing installation or EOF is NOT RUN. Five real separate-process
lifecycles check role/registration isolation, parent target preservation, module
binding and cleanup (`--observer-tests`, also included in the normal Raw suite).
See [three-process steps and branches](../NativeInputProbe/ExternalObserverControl.md);
this structural control is not original integrated or human acceptance.
`--manual-hook-install` in corrected keyboard-native-callback (no --startup-order)
prepares B target/Hook windows and native Raw registration without installing its
Hook. Start C prepared too, then issue install in B/C or C/B order; duplicate
commands do not reinstall. Pre-install baseline/snapshot are refused, missing
install/EOF remains NOT RUN, faults stay visible. [Manual order guide](../NativeInputProbe/ManualHookOrderControl.md)
defines nine-log steps and evidence. ManualHookInstallTests checks three real
paired subprocess lifecycles, three incomplete B cleanups and four fail-before-load
CLI cases (--manual-hook-tests, also in Raw/full suites). Physical AUTO is not inferred.
Native parser tests and three real lifecycles check classification, thread binding,
attachment conflicts, unsafe-unload rejection, receiver ownership and fault cleanup.
Paired HUMAN/AUTO is NOT RUN by the agent; this control is not original acceptance.

Run `dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64` with the root-pinned .NET SDK `10.0.402`.
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
Foreground-failure regressions retain the root exception and operation identity
only in host logs, including actual Lua dispatch; adapter results remain sanitized
and duplicate requests neither reexecute input nor repeat failure logs.
`ForegroundConfirmationTests.cs` uses controlled barriers and a monotonic test clock
to verify accepted-but-delayed activation, already-foreground targets, declined
activation, confirmation timeout, cancellation, permission revocation, invalid
targets and takeover. Conflicting input stays queued through confirmation; rejected
actions send no key, and retained Gateway duplicates never retry focus or input.
These tests do not certify actual Windows foreground permission or human acceptance.
The intervention suite also blocks cancellation propagation between Lua and the
scheduler using a controlled callback barrier. Actual Lua startup/tick waits must
stop cleanly when their original input epoch is cancelled, call `on_stop` once,
release capture/session resources and preserve timer runs. An unexplained runtime
stop still faults; returning control cannot revive the stopped run.

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
The [production acceptance procedure](ProductionSourceAcceptance.md) separates
specified F8/B target down/up, exact software move/button/key counts, per-class
foreground/background candidate counts and same-generation clean source shutdown.
ProductionInputEvidenceTests cover count deltas, merged samples, wrong/missing keys,
safety releases, excess/candidate input, parallel snapshots and continuity failures.
These synthetic regressions do not substitute for actual input or human acceptance.
The production acceptance record includes user session PID 33724: complete specific
target delivery, exact software attribution, human before/after counts and cleanup;
user feedback reports no human-test issue. Keyboard Hook observation remains incomplete.
Tag regressions cover x86/x64 keyboard/mouse, unknown tags, zero-device untagged
input and hook flags/extra-info layouts. Raw attribution and Hook verdicts are
separate; the retained legacy combined check and nonzero Hook-failure exit remain.
Consumer counts drain before evaluation; these diagnostic modes do not enable takeover.
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
startup never enables diagnostic Hooks. See [commands and evidence boundaries](../../Rug.UI.Core/RawInputEvents.md).

Task 3.5-05B-02 adds `InputInterventionTests` to the normal suite, or run
`--intervention-tests`. Controlled source/task barriers assert new action policy,
software/repeat/release exclusion, held/launch/return boundaries, generations and
delayed/coalesced events, waiting-period/multi-run/DevTools coverage, failed startup,
source/consumer fault blocking, queue cancellation, Gateway timeout with live input,
retained duplicates, cleanup failure and repeated session/handler disposal.
Actual RawInputEventSource barriers and net-zero merged motion are included.
`--intervention-manual` creates an isolated target and temporary input/timer/vision
plugins, launching actual WinUI with its production source. Explicit user grants
and human operations are required; no synthetic human input or diagnostic Hook is
installed. Only timer_probe exposes windowless Start; vision_probe/input_probe
show instructions to bind the isolated window and use the bottom host bound-task
button. The intervention suite parses all three schemas and checks these entries.
The input fixture waits 5 seconds before its first B and after each B, with
monitoring active throughout. Filtered Lua phase/scheduler-fault logs and passive
target B counters separate pre-input cancellation, API completion and actual
delivery; no terminal command is needed during input. Real Lua tests check the
startup yield, B down/up and cancellation of both waits.
See [exact human steps and boundaries](../../Rug.UI.Core/InputIntervention.md).
05B-02 human-triggered WinUI scenarios are user-confirmed passed (not performed
by the agent); see the acceptance record above. Existing 05B-01
acceptance and frozen native/Hook discrepancy evidence remain separate.
