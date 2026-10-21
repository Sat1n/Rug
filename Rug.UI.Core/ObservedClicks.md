---
id: rug_observed_clicks
type: logic_node
inputs: [rug_ui_core, rug_core]
outputs: []
tags: [input, observation, gateway, safety]
---

# Observed clicks (3.5-08A-02B)

Contract fixed before implementation: `AutomationOperation.ClickObserved` and
`rug.click_observed(requestJson)` are independent of legacy `rug.click`.
The bounded JSON object contains frameReference, observationId, frameId,
runGeneration, x/y, coordinateSpace, button, mode and policyId; optional maxAgeMs
can only tighten the host policy. Unknown fields (including supplied matrices,
timestamps or verified=true) reject. IDs resolve only through the owning run's
current bounded frame/observation records. Late recognition retains provenance
but cannot register an old frame for action again.

The built-in `roi-consistency-v1` policy uses maximum age 1000 ms, radius 8 client
pixels (17×17 ROI), maximum per-channel difference 8, mean absolute RGB difference
2, and verification deadline 750 ms. Trusted host registrations may choose tighter
or other bounded policies, with hard age ceiling 2000 ms. Callers cannot choose
ROI/tolerances. The ROI must fit wholly in the client area; padding/borders and
invalid mapping reject, never clamp. Supported spaces are capture-image-pixels,
roi-local-pixels (recognition only) and client-physical-pixels. Native input gets
client physical pixels, without another DPI/screen conversion.
PostMessage positions above 32767 are explicitly rejected rather than wrapping
the signed 16-bit mouse-message coordinates. Already offset
OCR blocks/match positions use capture-image-pixels (see ActualResultCoordinateSpace).
roi-local-pixels is for positions still local to the recorded ROI; it is not an
additional offset to apply to an already offset result.

Static parsing/source checks precede queues. After the existing input lease:
recheck source/age/current geometry, fresh ROI comparison, foreground confirmation
and another comparison for SendInput, move, recheck and fresh ROI comparison,
then final validation and MouseDown/mandatory MouseUp. No fallback/retry/relocalize.
Foreground activation, movement and button-down share one final side-effect guard.
After its potentially blocking geometry query returns, it rechecks the live input
lease/grant, original caller cancellation/deadline, run/capture admission, current
QPC age and source qualification before consuming the source and marking input.
No further geometry query follows those final checks. Cancellation/expiry during
the query therefore cannot authorize the next side effect. Necessary releases
remain outside this guard; refusal after activation or movement stays uncertain.
Current QPC is sampled through trusted native code; arbitrary TimeProvider and
AgeAtNativeSample cannot establish execution-time age. Window identity and all
supported capture mapping facts are rechecked independently of source freshness.

The host explicitly configures `CaptureService(exactBoundWindow: true)` in the
positive controlled path; old factories are not silently changed. Production
observed mapping/move/button calls enter and restore PMv2 on the actual Native
call thread, including required release. This keeps client coordinates physical
even in a DPI-unaware test host, without rescaling them or changing legacy input.
validation always compares newly captured pixels; no injectable success verifier
is used. Capture/recognition IO runs outside lifecycle/frame/arbiter locks.
Private verification frames and source leases share existing global limits;
private captures neither replace cache nor mint a new public action reference.

Each current frame has at most 64 registered observation IDs (capture plus 63
recognitions); registration overflow is CapacityExceeded, with the current frame
retained. A new explicit capture starts a new bounded registry. New captures retire
old qualification. Input side effects invalidate action qualification globally in
the shared pool, including legacy click/key and DevTools bridges. An executing
observed transaction can finish using its own consumed source, but subsequent
requests/queued transactions cannot. Cache replacement or another input epoch
still invalidates that transaction. Frames captured during input or across an
input epoch change never become action-qualified. Ordinary recognition may retain
source information without restoring qualification.

Results contain bounded source IDs, policy, client point and up to three
verification frame summaries, never pixels. `completed` means input API completion,
not business success. Expected validation rejection is a programmable Lua table;
permission, lifecycle Stop, cancellation, actual service and cleanup errors retain
existing failure semantics. Foreground activation/movement count as side effects;
rejection after either retains OutcomeUncertain. Mandatory releases bypass source
qualification checks. Deduplication is the existing Gateway operationId contract.

ROI consistency checks only the declared local pixels, not full business semantics.
Windows/external actors can still race after the last check. No complete action
atomicity, automatic recovery, VLM/MCP or generic model preprocessing is claimed.

## API and error details

Both `input` and `vision.capture` must be declared, explicitly granted and present
in the caller capability ceiling. Modes are 0=SendInput, 1=PostMessage; buttons
0=left, 1=right, 2=middle. No permissive mode/policy supplied by a plugin is trusted.
Example after an explicitly configured exact-window capture:

```lua
local c = rug.capture()
local o = c.observation
local request = string.format(
  '{"frameReference":"%s","observationId":"%s","frameId":"%s","runGeneration":%d,"x":90,"y":80,"coordinateSpace":"client-physical-pixels","button":0,"mode":1,"policyId":"roi-consistency-v1"}',
  o.frameReference, o.observationId, o.frameId, o.run.runGeneration)
local result = rug.click_observed(request)
if result.status == 'rejected' then rug.log(result.reason) end
```

Lua validation `status` is completed/rejected. Gateway cancellation/timeout/failure
also retains the last bounded trusted summary (with its actual operation status);
it does not turn those errors into a successful Lua return. Reasons are RoiConsistent, InvalidReference, Stale,
TimeUnavailable, GeometryChanged, WindowInvalid, SceneChanged,
ValidationUnavailable, CoordinateInvalid or PolicyUnavailable. Gateway exposes
corresponding Observation* errors (WindowInvalid shares ObservationGeometryChanged).
Malformed JSON/shape is InvalidParameters; actual capture/IO failure is
ExecutionFailed, not a scene mismatch. Permission/lifecycle errors are not converted
to successful Lua values. A cleanup failure retains the existing arbiter
CleanupFailed quarantine; Gateway cancellation can arrive before actual cleanup
failure, which remains observed/reported by the host. No subsequent input is allowed.

Source age and verification elapsed time are rechecked using current Windows boot
QPC, frequency and rational 100ns conversion, including after actual IO. Timer and
linked cancellation callbacks are not the final authority. Caller cancellation
and deadline are checked directly through the existing observation call boundary.
Verification requires its frame render time not precede its own capture request.
Missing native facts, unknown lifetime, unsupported/non-exact geometry, future
time, inaccessible/changed window or stale frame always reject. The default
17x17 neighborhood must remain visually stable through post-move capture; hover
animations inside it are unsupported and reject. No retry, waiting for a stable
scene or alternate locator is attempted.

Pause closes new input admission. Already admitted work follows 06A and drains
including releases before pause confirmation. Resume does not revive consumed
observations. Stop/revocation/takeover retain priority and actual work ownership.

## Verification record (2026-10-21)

Commands and final results are recorded in the
[test index](../tests/Rug.UI.Core.Script.Tests/README.md). Deterministic evidence,
actual PostMessage/WGC evidence and any human acceptance are kept separate.
The independent Hook observation gap remains open.

Review of `5487170` found a missing post-geometry cancellation/age check, despite
the earlier passing suite. The follow-up regression calibrates the final geometry
query before each actual side effect in a control run, then injects original-token
cancellation with linked propagation blocked, or a two-second QPC advance, at that
query in fresh runs. Both modes cover movement/down; SendInput also covers activation.
These are deterministic Gateway/Fake-input checks, not real SendInput acceptance.
