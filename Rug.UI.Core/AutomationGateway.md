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

`OperationRequest` contains an operation enum, detached JSON object parameters,
nonempty operation GUID, correlation string (1–128 characters), structural target
and absolute UTC deadline. Parameters are capped at 64 KiB. Targets contain
plugin ID, instance ID, generation and nullable binding ID; they contain no HWND.
Start uses only a plugin target, and returns the scheduler-created `RunIdentity`.
All other operations require the exact live run and binding, including a null
binding for windowless runs. Unknown/stopped, wrong-plugin, stale-generation and
wrong-binding requests fail before service dispatch.

`AutomationCallContext` has an internal constructor and is not request data.
Trusted host composition code uses `CreateContextAsync` to verify the plugin
catalog, bind a principal/source, copy manifest grants, and optionally pin a run.
Only that host API may bind a startup HWND or pre-run ready callback. External
adapters must receive a context issued by their trusted host, never expose this
factory or bind principal/source/grants from client JSON. Contexts from another
gateway are invalid. Lua contexts are run-pinned and cannot issue lifecycle
commands; UI/Agent/MCP contexts may be plugin-scoped or run-pinned.

`IAutomationPermissionChecker` is replaceable through the scheduler or gateway
constructor. The default checks exact manifest names in both caller and live-run
grants: `timer`, `vision.capture`, `vision.ocr`, `vision.match`, `input`. Plugin
lifecycle operations use the host's plugin capability and require no additional
manifest permission. Existing bridge checks remain defense in depth: external
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
  Capture returns `CapturedFrameMetadata` (width/height/stride), preserving Lua's
  frame table. Pixel buffers stay in the per-run visual bridge; deduplication
  does not retain previous screenshots.
* `wait_image` is still Lua composition, with every capture/match/sleep request
  admitted separately. Lua requests receive a ten-minute maximum operation
  deadline; UI lifecycle requests receive thirty seconds.
* State/config/log keep their existing scope rules. `resolve_anomaly` retains
  its current permission-gated incident capture/fault behavior, outside Gateway.
  DevTools host diagnostics remain outside plugin automation.

## Results, cancellation and deduplication

`OperationResult` separates Success, Rejected, Cancelled, TimedOut and Failed.
Codes include invalid parameters/caller, permission denial, unknown instance,
stale run, binding mismatch, unsupported operation, duplicate conflict, deadline,
cancellation, execution failure and capacity backpressure. No internal exception
or stack is returned to adapters. Lua maps successful values through its existing
frame/vision table conversion, permission rejection to `PermissionDeniedException`,
cancellation to stop, and other outcomes to a script error. An operation failure
does not automatically retry.

The first admitted caller token, deadline and instance token cancel execution.
Expired/pre-cancelled work never starts. Duplicate callers share the original
terminal task; later duplicate cancellation/deadline/correlation values do not
replace the first request's execution policy. Stop cancels the instance itself,
so its own completion wait is governed by caller/deadline rather than the token
it is cancelling. Request deadlines are limited to at most 24 hours.

Already-started work may ignore cancellation. Gateway returns promptly with
`OutcomeUncertain=true`, observes/discards its late result/failure, and never
resumes stopped Lua or writes into a replacement instance. This flag is
conservative, including operations without physical side effects. Cancellation
cannot retract input already emitted. Native work is not forcibly aborted.
Service dispatch runs on a worker outside the registry lock, so even a service
that blocks before returning its Task cannot block the caller, timeout delivery
or admission of another request. Tests cover that synchronous prefix as well as
an asynchronously pending noncooperative capture.

Deduplication keys include host principal/source, plugin, pinned-caller instance,
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

## Verification

The [script suite](../tests/Rug.UI.Core.Script.Tests/README.md) executes real Lua,
shared dual-instance services, UI lifecycle requests and simulated Agent/MCP
adapters. It covers forged grant data/capabilities, denial with zero capture calls,
identity failures, concurrent duplicates/conflicts, pre-expiration/cancellation,
noncooperative work, late Lua results, retention/capacity and repeated run cleanup.
Actual plugin UI paths are also exercised by the ordinary instance and demo
WinUI regressions described in [UI tests](../Rug.UI.Tests/README.md).
