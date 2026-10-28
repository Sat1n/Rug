---
id: rug_automation_gateway
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: [rug_ui_core]
tags: [automation, identity, permissions]
---

# Automation Gateway (Task 3.5-03)

`IAutomationGateway.ExecuteAsync` is the plugin automation admission point.
`TaskScheduler.Gateway` binds the verified catalog and internal lifecycle methods;
Lua registers an executor over its own existing capture/visual/input bridges.
The scheduler still owns initialization, hot reload and cleanup. Those internal
lifecycle calls do not reenter the gateway. Standalone Lua tests use the same
gateway with a local run registration. No C-ABI changes are involved.

## Request and caller contract

Host-only [runtime update switching](RuntimeSwitch.md) uses
`SwitchRuntimeUpdate {updateId,candidateVersion,revision}` alongside prepare/query/
cancel. It shares identity/grants/parameter checks, bounded dedup and operation
records. A retained group-commit boundary preserves committed results after
request cancellation; query separately reports restore completion or group failure.

`OperationRequest` contains an operation enum, detached JSON object parameters,
nonempty operation GUID, correlation string (1–128 characters), structural target
and absolute UTC deadline. Parameters are capped at 64 KiB. Targets contain
plugin ID, instance ID, generation and nullable binding ID; they contain no HWND.
Start uses only a plugin target, and returns the scheduler-created `RunIdentity`.
Except for trusted plugin-scoped checkpoint/update controls, other operations require the exact live run and binding, including a null
binding for windowless runs. Unknown/stopped, wrong-plugin, stale-generation and
wrong-binding requests fail before service dispatch.

`AutomationCallContext` has an internal constructor and is not request data.
Trusted host composition code uses `CreateContextAsync` to verify the plugin
catalog, bind a principal/source, copy capability ceilings and installation-bound authorization tickets, and optionally pin a run.
Only that host API may bind a startup HWND or pre-run ready callback. External
adapters must receive a context issued by their trusted host, never expose this
factory or bind principal/source/grants from client JSON. Contexts from another
gateway are invalid. Lua contexts are run-pinned and cannot issue lifecycle
commands; UI/Agent/MCP contexts may be plugin-scoped or run-pinned.

`IAutomationPermissionChecker` is replaceable through the scheduler or gateway
constructor. Live grants are additionally enforced through
[IPluginAuthorization](Authorization.md); the default operation policy checks
exact declarations in both caller and live-run ceilings: `timer`, `vision.capture`,
`vision.ocr`, `vision.match`, `input`. Start requires all current declarations to
be explicitly granted; Stop cleanup is independent of revoked grants.
Pause validates the live identity and returns request acceptance, not confirmed
Paused. Resume requires confirmed Paused and current declarations within the
caller's live grant/capability ceiling; invalid phases return
`InvalidLifecycleState`. The serial driver revalidates grants, the original
target and input control before advancing Lua. See [06A lifecycle](Lifecycle.md).
Stopping/Stopped/Faulted reject new business/control requests with
`InvalidLifecycleState` at receipt and actual dispatch, independently of token
propagation. Already queued input rechecks the owning lifecycle before execution;
required releases and shared Stop cleanup remain allowed. No-start rejection keeps
`OutcomeUncertain=false`; effects already begun retain the existing uncertainty.
Existing bridge checks remain
defense in depth: external
templates require `filesystem`, symlink/path restrictions and client-coordinate,
button, key and duration validation remain unchanged. A replacement policy can
restrict admission; it cannot relax those existing service checks.

## Migrated paths and parameter shapes

* UI `host.start_windowless`, `host.stop_selected`, `host.pause_selected`,
  `host.resume_selected` and demo start/automatic completion-stop use Gateway.
  Start parameters are the launch-input object; ready callbacks are host-only.
* Lua `sleep {ms}`, `capture {}`, `ocr {region?, language?, engine?}`,
  `find_image {path, threshold?, region?}`, `click {x,y,button?,mode?}` and
  `press_key {key,durationMs?}` yield requests. Regions use managed `Rect` JSON
  (`X`,`Y`,`Width`,`Height`); engine/button/mode use enum numeric values.
  Lua argument tables are copied under the Lua gate and never retained by Gateway.
  Numeric strings, booleans, missing required integers and fractional integers
  are rejected rather than coerced by Lua argument parsing.
  Capture returns `CapturedFrameMetadata` (width/height/stride), preserving Lua's
  frame table. Pixel buffers stay in the per-run visual bridge; deduplication
  does not retain previous screenshots.
* `wait_image` is still Lua composition, with every capture/match/sleep request
  admitted separately. Lua requests receive a ten-minute maximum operation
  deadline; UI lifecycle requests receive thirty seconds.
* State/config/log keep their scope rules. Explicit `resolve_anomaly` uses
  [verified recovery](Recovery.md) outside ordinary Gateway business dispatch.
  `VerifyRecovery {recoveryId}` is a trusted PluginUI-only Gateway control, with
  normal identity/install/grant/deadline/dedup checks. Lua/Agent/MCP cannot approve.
  Recovering/AwaitingHuman reject ordinary business/Pause/Resume; Stop remains open.
  DevTools host diagnostics remain outside plugin automation.

## Results, cancellation and deduplication

`OperationResult` separates Success, Rejected, Cancelled, TimedOut and Failed.
`OperationParameterValidator` checks required fields, JSON scalar types, enum
values, integer ranges, finite thresholds and complete positive regions before
dispatch. Startup inputs are checked against StateStore constraints before
creating services. Frame/window bounds remain checked by their owning bridge.
Malformed parameters return Rejected/InvalidParameters with no uncertainty or
service calls. This does not reclassify service `InvalidOperationException`s:
they remain Failed/ExecutionFailed.

Permission rejection includes structured missing names, rather than exception
text. Codes include invalid parameters/caller, permission denial, unknown instance,
stale run, binding mismatch, unsupported operation, duplicate conflict, deadline,
cancellation, execution failure and capacity backpressure. No internal exception
or stack is returned to adapters. Lua maps successful values through its existing
frame/vision table conversion, permission rejection to `PermissionDeniedException`,
cancellation to stop, and other outcomes to a script error. An operation failure
does not automatically retry.
For a host-closed run only, Lua maps InvalidLifecycleState admission refusal to
normal Stop cancellation (including synchronous/awaited results and paused
deferral). Genuine Lua/permission/cleanup failures remain faults; Gateway results
and input uncertainty are not rewritten by this Lua classification.

Execution failures retain the original exception in trusted host error logs with
operation/correlation IDs, source, target identity and operation type. Adapter and
Lua results keep the stable error code; they do not receive the exception or stack.
Retained duplicates neither execute again nor produce another failure log.

The first admitted caller token, deadline and instance token cancel execution.
Expired/pre-cancelled work never starts. Duplicate callers share the original
terminal task; later duplicate cancellation/deadline/correlation values do not
replace the first request's execution policy. Stop cancels the instance itself,
so its own completion wait is governed by caller/deadline rather than the token
it is cancelling. Request deadlines are limited to at most 24 hours.
Stop's own cancellation cannot mask its caller deadline as Cancelled: a deadline
returns TimedOut while real work/cleanup continue and the run stays Stopping.
Scheduler-owned lifecycle records survive Lua Stop/Faulted until host cleanup
completion. Different Stop IDs join that same completion, including after picker
removal; a same-ID retry returns the original task/result even after its timeout.
Full identity/installation and current permission-policy checks remain before
deduplication. Retaining a lifecycle record never reopens business input.

Already-started work may ignore cancellation. Gateway returns promptly with
`OutcomeUncertain=true`, observes/discards its late result/failure, and never
resumes stopped Lua or writes into a replacement instance. This flag is
conservative, including operations without physical side effects. Cancellation
cannot retract input already emitted. Native work is not forcibly aborted.
Capture additionally checks cancellation after its underlying await and inside
the frame publication lock. A timed-out/cancelled capture cannot update the
visual cache even if it returns after a newer successful capture on a live run;
subsequent OCR/matching continue reading that newer frame.
Service dispatch runs on a worker outside the registry lock, so even a service
that blocks before returning its Task cannot block the caller, timeout delivery
or admission of another request. Tests cover that synchronous prefix as well as
an asynchronously pending noncooperative capture.

Deduplication keys include host principal/source, plugin installation, pinned-caller instance,
target instance/generation/binding and operation ID. Fingerprints use operation
type plus exact JSON text; different property order is conservatively a conflict.
Correlation/deadline are transport metadata rather than parameters. Completed
entries remain for two minutes; active physical work pins its entry beyond that
TTL. At most 4,096 retained entries and 256 active underlying operations are allowed;
capacity is rejected rather than evicting an uncertain request. Ended runs remove
their registrations and completed entries; terminal in-flight requests remove
their entries once reported after run removal. Active underlying work still
consumes capacity until it settles. Start entries have no target run and remain
capacity/TTL bounded. Reusing IDs after retention is a new admission, so clients
must not retry uncertain operations based solely on expiration.
Run validity is checked before deduplication: a retry against an ended run is
rejected, even if an earlier request succeeded, and can never restart that run.

The last 256 minimal `OperationRecord`s include operation/correlation ID,
principal/source, target identity, type and terminal status/code/uncertainty.
`ILogger` receives the same metadata; permission denial adds a security warning.
Records omit parameters and exception stacks. Active run/entry/work counters
support bounded-resource tests. The gateway holds no state/event subscriptions.

## Input arbitration (Task 3.5-05A)

Click/PressKey dispatch uses the host [shared arbiter](InputArbitration.md),
including UI/Agent/MCP callers of the Lua runtime handler. Queuing is not an
input side effect: the flow-local `InputExecutionBoundary` marks the attempt to
activate the foreground window, or the first native input call for background
input. Foreground confirmation may fail after changing focus, so its attempt
conservatively counts as started. Every non-success input result uses that
progress flag, including target/permission/parameter rejection after movement
or activation; its status/error still describes the reason execution ended.
Pre-execution rejection and waiting cancellation report no uncertainty. The bridge keeps its lease/native service until
actual work and release finish, even after Gateway returns early. Closed input,
invalid lifetime or a full input queue returns Rejected/InputUnavailable.
Authorization, run identity, parameter checks and dedup remain in force.

Task 3.5-05B-02 adds the host [candidate-input coordinator](InputIntervention.md).
Input runs register whole lifetimes before service construction; Lua and external
adapter input share the arbiter's live detector guard. Detector failure or candidate
takeover closes admission and cancels sessions/queues, preserving actual native work
and cleanup leases after early Gateway return. Pre-input rejection stays certain;
already-started outcomes retain uncertainty and duplicates never replay the action.
Return control and session/detector policy remain trusted host operations, not
request parameters. Timer/vision-only runs do not become input sessions.

## Verification

The [script suite](../tests/Rug.UI.Core.Script.Tests/README.md) executes real Lua,
shared dual-instance services, UI lifecycle requests and simulated Agent/MCP
adapters. It covers forged grant data/capabilities, denial with zero capture calls,
identity failures, concurrent duplicates/conflicts, pre-expiration/cancellation,
noncooperative work, late Lua results, retention/capacity and repeated run cleanup.
Actual plugin UI paths are also exercised by the ordinary instance and demo
WinUI regressions described in [UI tests](../Rug.UI.Tests/README.md).
