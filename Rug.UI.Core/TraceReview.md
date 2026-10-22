---
id: rug_trace_review
type: logic_node
inputs: [rug_ui_core, rug_execution_traces]
outputs: [rug_ui]
tags: [trace, incident, viewer, bounded-query]
---

# Read-only trace review and incident links — 3.5-08B-02

## Host entry and queries

The host's **轨迹与异常** button opens one read-only window. Select a current
retention window or a local file; select an event for full white-listed metadata.
Refresh is manual. Event pages have at most 256 entries (UI requests 100); Next
replaces the page. `operationId` accepts only a GUID; it is a filter, not authority.
The catalog shows full session/trace/run identities, including control traces and
unknown identities on damaged records. Memory and disk choices are distinct.
No query starts a task, changes grants or acquires a window/frame/input lease.

[TraceReviewReader](Services/TraceReviewService.cs#class:TraceReviewReader) uses the
same host-root `TraceFileStore` as the recorder, never a plugin path. Host
`RUG_TRACE_ROOT` and `RUG_TRACE_DISABLED=1` retain their 08B-01 meanings.
`RUG_ANOMALY_ROOT` optionally sets the incident root; the existing default remains
`Environment.CurrentDirectory/logs/anomalies`. Neither root is a Lua parameter.

The [broker](Services/TraceReviewService.cs#class:TraceReviewService) has at most
four logical views, **one actual background read and one replaceable latest pending
read host-wide**. Per view, at most one dispatch callback and one latest completed
result are retained. A revision rejects results from old selections/refreshes.
Closing/unloading unsubscribes immediately; it does not wait for or claim to cancel
physical file IO. One hung read can delay all newer queries, but cannot create
additional reader workers or block Stop/host closing. Queries never call flush.
Pages/collections are replaced, not accumulated; event JSON display is capped at
65536 characters. No live recorder subscriptions or thumbnails are retained.

Catalog pages advance the current and disk windows together, with at most 100 files plus at most 100 current retained
traces. Incident pages contain at most 100 summaries, scanning at most 4096 file
names; detail reads are bounded to 1 MiB. Directory paging is a current bounded
view, not a stable snapshot across eviction or concurrent writes. A corrupt file
has its own status and does not poison other incident rows. Disk IO/access errors
become `ReadFailed / exception-type`; healthy diagnostic zeros are not invented.

## Honest display semantics

Memory is always **当前保留窗口**, never complete history. Offline statuses remain
`Valid`, `Incomplete`, `Corrupt`, `UnsupportedFormat`, `NotFound`, and
`CapacityExceeded`. An open/unsealed session normally reads `Incomplete`; viewing
does not force sealing. Recorder enabled/closing state, dropped events, write
failures, queued events/bytes, busy writer and incomplete shutdown flush are shown.
Task outcome is separate from file integrity. A Valid trace can describe a fault.
Unknown `OutcomeUncertain` and cleanup success display **未知**, not false.

Event details retain actual authorization/revision, source/validation frames,
validation stage, operation/execution IDs, caller terminal, actual-work terminal,
and cleanup events. Sequence is recorder order, not a full concurrency causality
proof. Validation-frame presence is not ROI approval. Input API completion is not
target delivery/business success. Caller return is not actual-work completion;
cleanup attempts completed does not prove isolated resources recovered.

## Incident JSON v1 and association boundary

[AnomalyLogger](Services/AnomalyLogger.cs#class:AnomalyLogger) creates:

| Field | Contract |
| --- | --- |
| schemaVersion / incidentId | Integer 1 / newly host-generated GUID. |
| timestamp, pluginId, instanceId, runGeneration, targetBindingId | Existing archive time and trusted run identity, not client-supplied trace identity. |
| reason / scriptContext / screenshotPath | Existing archive payload and local PNG basename; never copied into generic trace events. |
| recoveryId, recoveryGeneration, verification, agent_resolution | Existing recovery/agent data remains intact. |
| screenshotCaptureKind | `independent-anomaly-capture`: this PNG is a separate capture, never the operation source or validation frame. |
| traceLink | Detached bounded memory snapshot described below; no pixel/lease or disk wait. |

`traceLink` is captured **on entering LogAnomalyAsync, before capture, PNG encoding
or archive IO**. It contains hostSessionId, traceId, trusted run identity, session
sequenceUpperBound and boundaryUtc. This is a recorder boundary, not a claimed
precise fault instant or a durability watermark. `persistence=NotConfirmed` means
no trace flush/receipt was awaited. Only a matching current actual-operation scope
adds operationId/executionId and its known observationId/frameId. A Start/control
scope inherited by a new run does not match; outside an operation these are null,
never guessed from the last completed operation or current cache. Screenshot
completion cannot relabel the earlier snapshot.

Status is `AssociatedInMemory`, `Disabled`, `CapacityRejected`, `Unavailable`,
`Closed`, `WriterFailed` or catch-all `Unknown`. Available historical IDs can remain
on Closed/WriterFailed links; this does not promise persisted events. A stuck writer
can still allow an in-memory association, always NotConfirmed. Trace faults never
fail an otherwise valid PNG/JSON archive. Actual capture/encoding/archive IO errors
retain their original fault semantics.

No schemaVersion means **Legacy**, with no invented incidentId/link. Other integer
versions are UnsupportedFormat; invalid field types/values are Corrupt.
`UpdateRecoveryAsync` refuses unsupported formats and changes only `verification`
in a bounded parsed JSON object: incident identity/link, original screenshot and
legacy/extra fields (including agent_resolution) remain. No migration or bulk
rewrite occurs. Archives above the reader limit are CapacityExceeded rather than
silently truncated; existing archive writing semantics are not replaced by traces.

## Incident selection and locating history

Refresh incidents, select one, then **定位关联轨迹**. It uses recorded host IDs, optional
operationId, and a bounded event page starting near sequenceUpperBound (40 earlier
session sequence numbers). Large interleavings/gaps can place the exact event
outside that page; the bound is shown and manual paging/filtering remains available.
A retired/deleted trace says **关联轨迹不可用** without hiding the incident.
Legacy, missing link, damaged and unsupported files are distinct. PNG is displayed
only as a validated basename, not opened or loaded. JSON paths are never trusted;
only immediate-root `.json` basenames are accepted, file reparse points are rejected.
No history read restores grants, instances, old bindings or live frames.

## Verification and limits

[Test commands/results](../tests/Rug.UI.Core.Script.Tests/README.md#08b-02-trace-viewer-and-versioned-incidents)
cover actual Gateway boundary links, real PNG/JSON, recovery field preservation,
legacy/unknown/corrupt records, disabled/closed/failed/hung writers and bounded
latest-only read/dispatch queues. Actual WinUI automation uses an isolated target,
fresh WGC, one vision/agent run and a separately restarted host reading the same
files. It does not exercise global SendInput or establish new human acceptance.
The independent Hook gap remains open. No replay, MCP, model usage, screenshot
warehouse, automatic retry/recovery or historical migration is delivered.
