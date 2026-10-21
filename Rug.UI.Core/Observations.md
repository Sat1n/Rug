---
id: rug_observations
type: logic_node
inputs: [rug_ui_core]
outputs: []
tags: [vision, capture, provenance, lifecycle]
---

# Run observations and frame ownership (3.5-08A-01 / 08A-02A)

[ObservationEnvelope](Models/Observation.cs#class:ObservationEnvelope) accompanies
host-run capture, OCR and template-match results. This is provenance, **not** an
authorization to act or an action freshness check. Capture-time sampled geometry
is available only on the explicitly supported path below.
The low-level ICaptureService/IOcrService/ITemplateMatchService contracts remain
run-independent; the [visual bridge](Services/VisualEngineBridge.cs#class:VisualEngineBridge)
associates their actual inputs with trusted host RunIdentity.

## Version 2 envelope (additive fields; legacy result numbers unchanged)

| Field | Meaning |
| --- | --- |
| schemaVersion | 2; old width/height/stride, match, text and blocks fields retained |
| observationId | New ID for a successful capture publication or successful recognition |
| frameId | New ID **only** when a capture result is successfully published; cache reuse and recognition retain it |
| frameReference | Opaque, process-local reference scoped to this bridge/run's current cache, not a grant |
| run | pluginId, instanceId, runGeneration, targetBindingId; host-assigned, no HWND |
| image | Actual source width, height, stride, byteCount including row padding, BGRA8; capture-image-pixels |
| time | Clock ID/frequency/source, host request/receipt/publication UTC and monotonic timestamps; native acquisition fields nullable |
| geometry | Verified sampled mapping only for the supported exact-window path; otherwise explicit unavailable status and null transforms |
| captureFacts | Optional final-frame native time, separate content/buffer extent, opaque bound/captured window lifetimes, sampling brackets/DPI and geometry status |
| processing | Recognition kind, actual effective ROI, actual inference input width/height/stride, crop offsets/scales, monotonic start/completion and legacy output coordinate space |

ObservationId and frameId are different identifiers even for capture. OCR of A,
followed by matching A, has two observation IDs with the same frameId and capture
time record. Replaying a deduplicated Gateway request returns its original result,
not a new capture/recognition. Metadata records are immutable; copied/altered
records do not modify stored state or authorize reference use.

The additive native C-ABI now supplies WGC SystemRelativeTime as compositor render
QPC time in `time.nativeTime`, with the corresponding native frequency and sample.
It is not UTC or user action time. NativeAcquiredUtc/NativeAcquiredTimestamp retain
null for compatibility; no untagged number changes clock domain. Request/receipt/
publication fields still use this pool's TimeProvider and clock ID. UTC may jump.
`HostReceiptAge` still means time since host receipt. NativeFrameTime.AgeAtNativeSample
uses exact integer rational conversion in the Windows boot QPC domain; arbitrary
TimeProvider timestamps cannot be passed as native clock evidence. Unknown/future/
unexplained native time remains null; no replacement by current time.

Native pixels/time/ContentSize belong to one final candidate, including stale-frame
draining, black-frame retries and resize pool recreation. Metadata is copied with
the frame into the private pool and retained by recognition leases. If OCR holds A
while B publishes, A's time, geometry and native capture facts remain attached to A.
No new frameId is minted by recognition or by reading native metadata.

`CaptureService()` preserves legacy same-process window selection and full-surface
pixels. `CaptureService(exactBoundWindow: true)` opts into capturing only the bound
window and bounded retries for a post-sample frame. Supported normal overlapped
window mapping uses DWM bounds plus physical client origin/extent and lifetime/DPI/
style/monitor checks, not size equality alone. ContentSize is recorded separately
from buffer size and later client queries. Raw HWNDs never enter public observations;
opaque native lifetime tokens and SameWindow describe capture selection while the
host run.targetBindingId remains authoritative. Missing lifetime tokens are null.

See [native facts and precise rejection rules](../Rug.Core/CaptureFacts.md) for
ABI layout, timing, valid regions, sampling limits and native ownership.
`geometry.imageToClient` maps **source image** to physical client pixels;
`geometry.clientToScreen` maps to screen physical pixels. Neither is present for
unverified geometry. Old OCR/match coordinates are not remapped: ROI offsets are
still added exactly once to recognition-local boxes/hits, and the historical
physical-client interpretation remains only a legacy assumption on full-window
captures. Processing.Offset/Scale describe ROI→source-image, not a verified action
coordinate. The historical ResultCoordinateSpace field is retained, while the
additive InputCoordinateSpace=roi-local-pixels and ActualResultCoordinateSpace=
capture-image-pixels state the actual input/output spaces unambiguously. Consumers
must explicitly chain a supported source-image transform.
`PixelTransform` also expresses crop/positive scale/letterbox relations for pure
calculation; padding, nonfinite/outside points and out-of-bounds rounding reject,
without clamp. There is no model preprocessing pipeline or action safety gate.

Before/after window samples do not prove there was no intermediate change or that
geometry is still current when used later. A destroyed/reused window invalidates
native lifetime checks. Losing/changing a previously known bound or captured
window lifetime during acquisition/copy rejects the whole native frame, rather
than publishing geometryStatus=2 with stale identity. CaptureService receives the
existing capture-failed/null result; no new observation/frame ID is published.
Unknown-at-start lifetimes remain unknown. Stop/late host publication guards are unchanged. UI-only
reload retains the run; a code switch starts a new session/reference identity.
Recovery/checkpoint validators still request fresh capture, never this normal cache.

## Host pool and leases

[ObservationFramePool](Services/ObservationFramePool.cs#class:ObservationFramePool)
defaults to one shared process-wide instance across all Lua runtimes/plugins and
run generations. A trusted host may inject one pool into LuaRuntime constructors
(including its runtime factory) to configure limits and a clock; do not create a
different pool for every run and claim its limits are global. Defaults:

| Limit | Default |
| --- | --- |
| MaxFrameBytes | 64 MiB, including stride padding; native managed-copy ingress has the same hard maximum |
| MaxResidentBytes | 256 MiB cached/leased pixel bytes **plus** operation reservations |
| MaxFrames | 64 retained frames plus capture slots |
| MaxOperations | 16 capture/recognition operations across the pool |
| MaxSessions | 256 host runtime sessions |

Admission rejects immediately, with no frame-pool waiting queue or automatic
retry. Each capture reserves 2 * MaxFrameBytes **before** invoking the producer,
covering its managed output and a detached private host copy. Publication converts
that reservation into actual retained bytes. ROI recognition additionally reserves
the packed crop bytes before allocating it. No-ROI recognition reuses the private
source buffer under a lease. Conservative reservations/slots can refuse a capture
even if its eventual small result would have fit, including replacing a cache at
the frame-count limit. This is deliberate rejection, not unsafe eviction. Limits
can be configured together; MaxResidentBytes must permit two maximum frames.

Usage reports ResidentBytes, ReservedBytes, Frames, Operations and Sessions under
one lock. Counts concern owned/reserved host pipeline pixels, not GC heap peaks,
GPU/WGC frame pools, native inference scratch space, metadata/results, or buffers
retained by trusted low-level callers outside this run cache. Lower-level capture
still enforces its 64 MiB managed-copy cap (or the smaller pool-requested cap
through the internal bounded-ingress adapter) and keeps its SafeHandle reference
through actual native completion. Direct diagnostic/validator services remain
trusted lower-layer consumers, not a public script pixel store.

The cache owns one reference. A recognition atomically leases the selected entry,
so replacing A with B releases only cache ownership of A. Active OCR/matching
continues reading A; its returned envelope refers to A, never whichever cache is
current at completion. The last release removes byte/frame accounting and clears
the pool's pixel reference. Noncooperative actual tasks retain their leases and
reservations even if Gateway has already returned timeout/cancellation. The
scheduler's actual-work permits likewise delay disposal of services/native handles.

References are valid only for the issuing bridge's current cache. Replacing the
cache, successful no-frame capture (which clears it), Stop or bridge disposal
invalidates them for **new** operations. Existing leases can finish safely but do
not make expired references usable again. Copying/guessing another run's reference,
including the same plugin/window or another generation, cannot select its frame.
There is no pixel-read operation for Lua/Gateway and no file path or identity
parameter in these APIs. The C# bridge's optional frameReference selector is for
trusted host callers and is still checked against that bridge's own cache.

Capture attempts have a per-session request order. A result from an older attempt
cannot publish after a newer attempt has started, even if it eventually succeeds.
Such superseded work is cancelled without minting source IDs; a failed latest
attempt leaves the previous cache intact. Successful no-frame results clear it.
Publication rechecks original execution cancellation, permissions, session and
host lifecycle under the Stop transition boundary. A run already Stopping cannot
republish even while cancellation callbacks are blocked. No service/validator or
pixel copy runs under the lifecycle/pool publication lock.
Gateway carries the original caller token and authoritative request deadline in
a trusted AsyncLocal boundary; final publication checks these directly, rather
than depending on linked-token callbacks or timer propagation. Expiry is reported
as TimedOut/DeadlineExceeded; caller cancellation remains Cancelled. A final check
that wins before subsequent cancellation is the publication decision, not a claim
that already-published metadata can be undone.

Production CaptureService implements the internal bounded-ingress protocol, so a
smaller injected MaxFrameBytes is checked before its managed Marshal.Copy buffer
allocation, with the native buffer still freed on rejection. A custom trusted
capture adapter that does not implement this protocol owns/bounds its temporary
producer allocation itself; the pool validates its returned layout/size before
making any cache copy or publication. Such external allocations are not silently
claimed as pool-owned memory.

## API, Lua compatibility and errors

CapturedFrameMetadata retains width/height/stride and adds non-null Observation.
ImageFindResult retains x/y/similarity and adds Observation. VisionOcrResult retains
text/blocks and adds Observation. Lua exposes this as the extra `observation`
table using camelCase names; OCR's numeric array entries and `blocks` remain.
There is no CLR userdata, byte array, Base64 or native handle in these returns.

```lua
local captured = rug.capture()
if captured then
  local result = rug.ocr({x=0, y=0, width=100, height=40}, 'en-US')
  assert(result.observation.frameId == captured.observation.frameId)
  -- This association is evidence of the input image, not permission to click.
end
```

No frame from capture remains nil; a template miss remains nil; neither invents
a successful observation. OCR of an empty-text image still returns its genuine
source envelope. Recognition before capture/expired/foreign reference throws
ObservationReferenceException (InvalidOperationException); malformed frame layout
throws InvalidDataException, bad ROI throws ArgumentOutOfRangeException, and
capacity exhaustion throws ObservationCapacityException. Gateway maps capacity
to Rejected/CapacityExceeded. Existing permission, service failure and cancellation
semantics remain distinct; genuine OCR/capture/native faults are not converted
into empty results. No raw pixels are added to StateStore, checkpoints or ordinary
operation logs (existing explicit incident-image archival is separate).

UI-only updates retain the same runtime/cache/run identity and paused state.
Runtime switching creates a new bridge/cache and new host identity; no reference
or frame is inherited. Recovery/checkpoint validators continue their independent
**fresh** capture route, with existing work/grant/drain rules, never ordinary
cached observations. This unit does not change recovery validation policy.

## Verification

```powershell
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --observation-tests
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --observation-native
```

The deterministic suite is also in the full script regression. Actual Lua, host
Gateway and barriers cover owner/generation isolation, held A/replaced B, padded
stride/crops/offsets, clock jumps, no-frame/miss/error/invalid layout, shared byte/
operation/frame/session limits, cancellation, blocked Stop propagation, late
capture suppression and cleanup counters. 07A tests additionally retain a paused
run's frame across UI-only application; switching tests require a new source frame
and owner in the replacement run.

The separate native smoke owns/cleans an isolated recovery test window, runs real
WGC -> WinRT OCR + OpenCV matching through the production visual bridge, and
compares their source IDs to the capture metadata. A small temporary BMP matching
fixture is built by the trusted test host from the actual source and deleted on
exit. No fake OCR/matching or synthetic human acceptance is claimed. It loads the
existing x64/Debug Rug.Core DLL and reports NOT RUN if unavailable. Build/test and
native results are recorded separately; no UI display or action-safety acceptance
is implied. Independent Hook limitations remain unchanged.

## Recorded verification (2026-10-20)

SDK 10.0.402. Six managed projects (including WinUI) built Debug/x64 and
Release/x64 with zero warnings/errors. Final sequential full script runs passed
in both configurations: observations, actual Lua/Gateway, authorization, input
arbitration/intervention, lifecycle, recovery/checkpoint (including existing child
restart checks), 07A and 42 runtime-switch scenarios. Schema/state/real-watcher
regressions passed in both configurations. Commands:

```powershell
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Release -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Release -p:Platform=x64 --no-build
```

`--observation-native` passed against actual Debug Rug.Core: WinRT OCR returned
three blocks and OpenCV found the temporary template, both referencing the exact
published WGC frame; run/binding assertions passed and all pool counters returned
to zero. A real capture with a four-byte ingress limit was rejected before a
managed frame copy. This is agent Native automation evidence, not simulated recognition or
new human-input acceptance. No UI rendering changes require a separate manual
check. Manual UI/input acceptance: NOT RUN this unit; earlier acceptance and the
independent Hook observation gap are unchanged. Native release/configuration and
cross-screen/DPI/action-gate experiments were not claimed by this smoke.

The first parallel Release full run failed once in the existing repeated-Gateway-
Stop assertion (`joined Stop failed after real cleanup`). An isolated Release
lifecycle run and the subsequent sequential Release full run passed without
changing that Stop branch. This one-off failure is retained here; its cause was
not established as an environment-only issue. Observation-specific and final
complete runs passed; this record does not erase the initial failure.

## 08A-02A verification addition

[Native capture verification](../Rug.Core/CaptureFacts.md) records final candidate,
QPC/layout/window-change tests and actual WGC marker/resize/cross-monitor DPI
144→96 evidence. The 08A-01 historical record above remains historical. Version 2
metadata, actual Lua compatibility, held A/native time while B publishes, UTC jumps,
capacity/Stop/late publication and full Debug/Release script/UI-core regressions
pass. WGC→OCR/matching retains final native time and source IDs. No action gate,
input strategy change, Hook root-cause closure or new human acceptance is claimed.

## 08A-02B action eligibility extension

[Observed clicks](ObservedClicks.md) resolve capture, OCR and match observation IDs
against the current owning frame. Provenance remains readable after qualification
is retired; it is not an action capability by itself. Up to 64 IDs per current
frame, shared resident/work limits and one-shot input epochs bound the registry.
Verification uses private fresh captures with separate leases; it never replaces
the script cache. Existing v2 metadata and old recognition coordinates are retained.
