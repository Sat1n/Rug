---
id: rug_execution_traces
type: logic_node
inputs: [rug_ui_core]
outputs: []
tags: [trace, storage, gateway, lifecycle]
---

# Structured execution traces — 3.5-08B-01

## Host contract and composition

`ExecutionTraceRecorder` is a sealed best-effort recorder, not a decision service.
The WinUI composition root enables it by default at `%LOCALAPPDATA%/Rug/traces`.
Host environment `RUG_TRACE_ROOT` selects another root; `RUG_TRACE_DISABLED=1`
disables recording. Neither setting is exposed to Lua, Gateway JSON or plugin config.
Library hosts inject a recorder into `TaskScheduler(..., traces: recorder)`;
omitting it uses `ExecutionTraceRecorder.Disabled()`. A recorder with no root is
bounded memory-only and cannot report durable history. An invalid root degrades
to memory and exposes diagnostics. Invalid trusted limits fail configuration.

The scheduler owns one recorder session and closes it after actual run cleanup.
Sharing that recorder with another scheduler is unsupported. The follow-up
[08B-02 viewer and incident contract](TraceReview.md) adds read-only UI and versioned
incident links; no MCP surface exists. Archive IO failure semantics are preserved.

## Event fields and identity

`ExecutionTraceEvent` schemaVersion=1 has the following white-listed fields:

| Fields | Meaning |
| --- | --- |
| hostSessionId, traceId, sequence | Host-generated session/trace IDs; sequence increases across this session in recorder-lock order, including lost events. Not UTC order or a concurrency causality proof. |
| run | Host-resolved pluginId, instanceId, runGeneration, targetBindingId. No HWND. Null on control/unresolved-target events. |
| origin | Parent instance, checkpoint ID/sequence/node; optional runtime update ID and compatibility rule. New runs have their own trace, never the parent's frames or identity. |
| operationId, correlationId | Existing request join keys, bounded; never path or authority. |
| executionId | Host-generated dedup execution attempt ID. Dispatch/WorkStarted establish actual activity, not ID allocation alone. Immediate refusals have no execution ID. |
| caller, source, operation, requestedTarget | Bounded host capability principal/source and operation; requested target is distinct from resolved run. Invalid targets stay in control trace. |
| kind, status, error, reason, outcomeUncertain | Typed event/stable error or exception type; bounded reason. Null uncertainty means not applicable/unknown, not success. |
| utc, monotonicTicks, monotonicFrequency, clockDomain | UTC display only; duration uses the recorder's session TimeProvider ticks/frequency. Never subtract Native QPC frame time from these ticks. |
| authorization | Decision, exact required names at that check, optional missing names, revision from the actual successful admission lease. Unknown revisions are null; completion never queries current grants as history. |
| observation | Source observation/frame IDs, policy, mapped client point and at most three validation frame IDs. No pixels or frame lease. |
| observationState, validationState | NotApplicable / NotCollected / Absent / Present. Availability only, not a validation verdict. Present validation frames do not imply ROI passed. |

Kinds: Request, RepeatedRequest, Rejected, Authorized, WorkStarted, Dispatch,
Observation, RunLinked, Validation, InputSideEffect, CallerReturned,
WorkCompleted, ReleaseFailed, RunCreated, Lifecycle, StopRequested,
CleanupFailed, CleanupCompleted, HostClosing. JSON enums use their declared numeric
values; the committed sample explains them using names.

`CallerReturned` is recorded immediately before publishing the terminal Gateway
completion (or returning an immediate result), not when a remote client renders it.
`WorkCompleted` is emitted by the real service Task continuation, including late
faults/cancellation, after actual-work lifetime disposal. Thus caller timeout can
precede service completion and cleanup. Repeated requests reference the original
execution and do not emit another WorkStarted/Dispatch. Start requests initially
use the control trace; RunCreated/RunLinked join them to the new run.

OCR/template input-leased events identify the frame actually held even on no hit
or later recognition failure. Successful returned recognition carries its own
observation ID. `Validation` distinguishes source resolution, fresh comparison
frame obtained and `*:roi-passed`; only that last reason means the local ROI
comparison passed. `completed`/Success remains input API completion, not business
success. InputSideEffect records the existing activation/move/down boundary;
no human keys, mouse movements, OCR text, Lua context or configuration is collected.

08C-01 adds bounded template-result facts using the existing Observation kind and
schema: reason `template-match:result`, status Matched/NoMatch and the actual held
source IDs. Matched uses the returned recognition observation; NoMatch uses the
input capture observation. No template path/pixels or Lua state is serialized.
These are recognition results, not generic business-success declarations; the
[controlled sample](../samples/controlled-visual/README.md) records its separate confirmation.

Lifecycle events carry revision/pause state. The first observed closed-run cause
is retained: HostStop, ManualTakeover, AuthorizationRevoked, HostShutdown,
ExecutionFault or Unknown. A later cleanup error is separate and must not rewrite
that cause. CleanupCompleted means the host finished its cleanup attempts; it
does not promise a quarantined Native/input resource was recovered.

## Capacity and failure policy

| Resource | Default |
| --- | --- |
| Event UTF-8 envelope including newline | 8192 bytes; conservative escaped-byte estimate on producer, exact check on writer |
| Text / principal-correlation-plugin / status-error / policy | 192 / 128 / 64-96 / 64 UTF-16 code units |
| Permission names / validation frame IDs | At most 16 / 3; current known permission set fits |
| Memory history | 1024 events, 2 MiB conservative budget; evict oldest, query explicitly says window/incomplete |
| Pending writes | 512 events, 1 MiB conservative budget, plus one current writer event |
| Retained trace bookkeeping | 128 (including control); retire only closed/drained histories; refuse new trace capability when all slots active |
| Event file / total component files / file count | 1 MiB / 64 MiB / 256 |
| Retention / close flush | 7 days / 250 ms |

Trusted overrides have hard ceilings: 64 KiB event, 16384 history events/64 MiB,
4096 queued events/16 MiB, 1024 traces, 64 MiB event file, 1 GiB disk, 2048 files,
30 days retention, 5000 ms flush. Reader pages are 1..256 rows. No arbitrary request
or result serialization occurs on the producer. Truncation applies only to named
text summaries; too-large events are dropped and counted. No image, Base64,
checkpoint business state, full config, credential or service object is retained.
No usage/cost is collected; absence does not mean zero cost.

Queue pressure drops the incoming ordinary event. Termination, caller result,
actual-work completion, release/cleanup failures, cleanup completion and host close
first evict queued ordinary events; if only critical events remain, the incoming
event is also dropped. All such losses increment total and per-trace counts.
This is priority best effort, not lossless audit logging. A trace-capacity refusal
has traceId=Guid.Empty/no events and increments global loss diagnostics.

Only the worker serializes, hashes, opens files or calls `ITraceFileSink`; producers
do bounded copies/estimation and enqueue under a private short lock, without disk
waits or subscriber callbacks. The worker holds only detached event metadata.
It owns at most a directory quota lock plus one data/temp/read stream at a time.
Writer failures retain type/HResult diagnostics, increment loss/failure counters,
and never propagate to authorization, input, Stop, mandatory release or cleanup.
A stuck writer reports busy/queue counts; bounded close reports FlushIncomplete.
The worker may remain alive after that bound with its own IO resources, never run,
frame or input leases. No cancellation of Native work or automatic automation retry
is introduced. Diagnostic APIs are host-only, not Lua control flags.

## Files, persistence and completeness

Host IDs produce `rugtrace-{session:N}-{trace:N}.events` (JSONL checksum/payload
envelopes) and `.summary` (same envelope, same-directory `.summary.tmp` then atomic
create/replace). SHA-256 covers deterministic JsonElement serialization with exact
JSON number lexemes; it is integrity checking, not a signed audit log.
The worker is serial: an older summary cannot overtake a newer one. Summaries
describe **persisted** first/last/count, separately recorded count/last, dropped
events, actual-work balance, recording closed, cleanup completed and nullable
cleanupSucceeded. Cleanup failure/quarantine is false; loss makes success unknown.

A `.closed` session seal (empty trace GUID) is written only after normal worker
drain and successful final summaries. It also records session-wide dropped events,
including refused trace allocation; any session loss conservatively makes every
page Incomplete even if that trace's own persisted prefix has no known loss.
A reader requires this seal as well as exact
range/count consistency before Valid. During recording or after interrupted/stuck/
failed shutdown the result is Incomplete even if the persisted prefix is usable.
Trailing half-lines and residual summary temporaries are Incomplete; middle
corruption/checksum/identity/order errors are Corrupt; unknown schema is
UnsupportedFormat. Real reader IO/access errors remain exceptions to the host.

Reaching a single event-file cap refuses further appends to that trace and records
loss; there is no unbounded same-trace rotation. New runs naturally create new
files. On writes, old closed traces are evicted by retention or capacity, only for
exact recognized component names; unrelated files are untouched. Active traces
are never evicted to make room. No room means explicit write failure/incomplete
history. A writer-only directory lock prevents concurrent host quota decisions
racing; contention is a diagnostic write failure, not a control wait. Deleted
event histories are never resurrected by a later final summary.
Atomic summaries do not provide absolute power-loss durability or atomicity with
OS input. No persisted watermark can certify events never recorded by a crashed
producer. Existing Hook observation limitations remain open.

## Read-only APIs and verification

`recorder.Summaries`, `recorder.Diagnostics`, `recorder.Query(traceId,
operationId?, afterSequence, limit, throughSequence?)` are bounded detached views.
`new TraceFileStore(hostRoot).Discover(instanceId?, offset, limit)` lists identities
and compatibility/integrity status, including damaged/unknown records.
`Read(sessionId, traceId, operationId?, afterSequence, limit, throughSequence?)`
returns only checked data, persistence/completeness and a next-sequence cursor.
Sequence interval is `(afterSequence, throughSequence]`. Catalog offset paging is
over a current sorted directory view, not a transactional snapshot during eviction.
Memory retention is explicitly a window and not a substitute for disk completeness.
None of these APIs starts tasks, restores grants or retrieves live frame resources.

Commands/results: [test index](../tests/Rug.UI.Core.Script.Tests/README.md).
Actual controlled WGC/PostMessage example: [trace sample](../tests/samples/execution-trace/README.md).
The [08B-02 follow-up](TraceReview.md) delivers a read-only viewer and incident JSON
links. Replay, screenshot warehouse and model calls remain outside scope.
Input behavior and real SendInput/human acceptance are unchanged.

EXIT-01 follow-up adds optional `TraceObservation.TimeRejection`: bounded typed
QPC/native-time/age facts only at a rejected observed-click check. Old schema-v1
records have null. Source frame identity and unpublished verification candidates
remain distinct; diagnostic raw time never grants action eligibility. See
[time guard diagnostic contract](ObservedClicks.md). No retry or threshold change.
