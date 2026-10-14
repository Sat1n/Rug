---
id: rug_checkpoints
type: logic_node
inputs: [rug_ui_core]
outputs: [rug_ui_core]
tags: [checkpoint, persistence, validation]
---

# Verified business checkpoints — 3.5-06C-01

This unit delivers saving and reading checkpoints. Starting a new instance from
one, recovery entry UI, migration and transactional hot reload are NOT delivered.
06B coroutine recovery and 05B takeover behavior remain unchanged. The independent
Hook observation gap and existing evidence limits remain open.

## API, authority and node position

Lua yields `rug.checkpoint.commit(nodeId, stateJson, completedActionIdsJson)`.
Data arguments must be JSON strings, not tables/userdata. The asynchronous result
is an explicit Lua table: status=committed/rejected/timed_out/cancelled,
error=stable CheckpointError, optional checkpointId/submissionSequence.
Missing/false validation rejects; deadlines time out; an independently cancelled
host call cancels. Host Stop cancels the yielded Lua call without mainline return.
Permission/capture/IO faults remain exceptions and Faulted, not validation failure.
After a rejected result scripts must decide how to handle it; there is no automatic
recovery or retry. Mainline cannot advance while validation/publication is pending.

The exact `checkpoint` capability requires declaration, live explicit host grant
and the owning run's ceiling. Existing plugins gain no permission automatically.
All declarations are required at startup, and revoking checkpoint stops its runs.
State writes cannot grant it. Submission accepts no paths, instance IDs or metadata.
Host-only [coordinator](Abstractions/ICheckpointService.cs#interface:ICheckpointCoordinator),
node registry/validator and store interfaces are composed in App; Lua has no store access.

Nodes register exact installation/pluginVersion/contentHash/nodeId/state version
and declared action IDs. Claims must match that node's action set, then a trusted
validator verifies the postcondition using fresh owning-run capture with live
vision.capture authority. Observation exposes no input tools, HWND, context or
configuration. It bypasses the visual cache and retains real capture work until
completion. No validator means no valid checkpoint; model/API success is not evidence.

The shipped checkpoint_probe's panel-ready/v1 node means a large green controlled
panel was observed and its business position is after-panel-ready. Host schema
validation requires exactly position="after-panel-ready", ready=true and action
ID panel-ready. The registry is pinned to the embedded shipped main.lua, version
1.0.0 and configured installation; editing code does not automatically re-register
the node. Arbitrary plugins have no default success validator. Future restart
must re-observe the scene and continue AFTER the confirmed node. Action IDs are
business confirmation records, not cross-process exactly-once OS-action guarantees.

## Immutable identity and bounded data

[Record](Models/CheckpointModels.cs#class:CheckpointRecord) fields use camelCase:
schemaVersion=1, checkpointId, submissionSequence, timestampUtc, installationId,
pluginId, pluginVersion, contentHash, sourceInstanceId/sourceRunGeneration,
nodeId/nodeStateVersion, configRevision/configHash, explicit state, completedActionIds.
All metadata comes from the host. No implicit InstanceState/config/log copy, Lua
stack, CLR object, HWND/native handle or authorization material is persisted.

Installation ID is SHA-256 of normalized host PluginInstallation (uppercase full
directory and plugin ID). It does not store the directory. Source addresses also
include plugin, instance GUID and generation. Different installations/instances
therefore share neither record paths nor sequence streams.

Code identity covers ONLY the actually compiled entry text: JSON-encoded array
["rug-entry-v1", relative entry name with '/' separators, source text], UTF-8,
SHA-256 lowercase hex. Snapshot/hash use the SAME immutable string assigned to
Lua load, never submission-time reads. TextReader decodes BOM; no newline or
whitespace normalization. `require`, loadfile and dynamic loading stay disabled,
so other Lua/UI/manifest files are not executed-code inputs. pluginVersion is
the startup manifest value. The snapshot source itself is never persisted.

Configuration is the exact startup defaults plus PluginConfig snapshot. Its
revision and values are captured under the same state-partition lock. Hash is
bounded canonical JSON: ordinal object keys, original array order, normalized
finite numbers, explicit null. Revision is process-local metadata, never the
cross-process compatibility criterion; configHash must match. Launch inputs are
InstanceState and are excluded. Config data itself is not automatically stored.

Submitted AND normalized encoded state must be <=64KiB UTF-8, with <=16 container
levels. Missing keys differ from explicit null (including a null state root).
Duplicate keys, nonfinite numeric values, malformed/over-limit JSON reject.
JSON-string-only input excludes cycles and unserializable CLR values; config
serialization also rejects those. Known internal/credential names (password,
secret, apiKey, accessToken, credentials, authorization, HWND/nativeHandle,
LuaStack/ClrObject) reject. This is not a semantic secret detector: trusted node
validators must allow only their business schema; do not submit disguised secrets.
Completed action IDs: <=128 distinct nonempty ASCII identifier strings, <=64
characters each (letters/digits/dot/underscore/dash), exactly matching the node.
Whole integrity envelope <=128KiB. Returned JSON/list snapshots are detached and
read-only; changing a copied record cannot affect storage.

## IO, quotas and cancellation

[FileCheckpointStore](Services/FileCheckpointStore.cs#class:FileCheckpointStore)
owns a fixed root (%LOCALAPPDATA%/Rug/checkpoints by default). Names are SHA-256
of structured source addresses, never user paths. Linked roots/files are rejected.
One latest .json per source; SHA-256 checksum covers canonical record JSON.
Checksums detect corruption, not malicious host-level tampering or signatures.

Source gate serializes commits; sequences reflect admission order and may have
gaps after rejection. Older/equal sequence publication rejects. Bounded admission:
16 per source, 32 store writers and 32 actual validator tasks. Timed-out advice
is observed/discarded. Noncooperative capture/IO retains real-work permits and
input freeze until actual completion. Stop does not wait for human advice.

Writers take an exclusive root writer file lease, write a random same-directory
.tmp with CreateNew, flush, then reopen/verify complete metadata/checksum. First
publication uses Move; replacement uses File.Replace. Readers open with delete
sharing and only inspect the hashed published .json, never temp files. Default
caps: 256 published records, 32MiB total record/temp bytes, 64 temps. Capacity or
sequence failure preserves old records; root writer conflict/IO errors are faults.
Crash leftovers consume capacity until trusted host cleanup, never become records.

The FINAL publish decision is the token/deadline/Stop and metadata-reference check
under the run lock, after temp validation. No disk IO, verifier, notification or
cancellation runs under that lock. Before decision, cancellation/deadline preserves
old data. After decision, publish ignores cancellation: it completes or fails,
and a host caller can obtain the actual committed result. Stop closes admission,
prevents old Lua continuation and waits for actual IO before disposal/Stopped.
IO failure is recorded before releasing the real-work permit, even during Stop.
Atomic files do not make OS actions transactional and are not absolute power-loss
safety. Crash after action/before publish requires fresh verification, not blind replay.

## Reading and compatibility

ReadAsync(host address, host compatibility) distinguishes Absent, Valid, Corrupt,
UnsupportedFormat, Oversized, InstallationMismatch, PluginMismatch, ContentMismatch
(pluginVersion or contentHash), NodeVersionMismatch and ConfigIncompatible.
Only a complete checksum-valid supported record is returned. ConfigRevision may
differ if configHash matches. No implicit migration, task creation, permission
restoration, source revival or target binding restoration occurs on read.

## Commands and evidence

```powershell
dotnet build tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-restore
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --checkpoint-tests
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64
dotnet build Rug.UI/Rug.UI.csproj -c Debug -p:Platform=x64 --no-restore
```

CheckpointTests uses actual Lua, controlled Fake pixels/validators and the REAL
filesystem. It separately reports same-process reopening and child-process read
of a published checkpoint (child creates no tasks/grants/windows). Tests cover
frozen mainline, retained previous records, publication/cancel/Stop/late IO,
isolation, stale sequences, code/config identities, corruption/temps/version/
capacity limits and bounded JSON. Final run results are recorded below.
Actual WGC sample/human input are not required for this deterministic/filesystem
unit and must not be inferred from Fake observation tests.

Native capture now throws distinct non-capture resource/status errors and malformed
success layouts instead of silently treating them as empty frames. Existing ABI
ErrCaptureFailed still conflates no-frame and generic capture failure; it remains
bounded no-frame polling/timeout, not a verified postcondition. Richer native
failure discrimination would require a later ABI contract; no C-ABI was changed.

### Recorded results — 2026-10-14

PASS: actual Lua and shipped exact-code sample using trusted Fake pixels; real
filesystem publication, same-process reopen and distinct child-process read.
PASS: all checkpoint barriers/retention/JSON/identity/quota/corruption/version
cases, same-source serialized sequence and dual-instance/different-install isolation.
PASS: final complete script regression including 06B recovery, lifecycle, Gateway,
authorization, arbitration, takeover, late-frame cache, state/hot reload and
existing self-owned native PostMessage release fixture. Frozen reversal binaries
and ON/OFF/ON experiments were not rebuilt/repeated.
PASS: Schema/state/hot reload suite; Debug/x64 Core/script/UI-test/WinUI builds,
0 warnings/errors. No dependency or C-ABI changes.
NOT RUN: checkpoint sample against real WGC hardware, new human-input scenarios
or power-loss tests. Fake capture is not hardware evidence; the real-filesystem
and subprocess read assertions are actual IO/process evidence.
DELIVERED: checkpoint save and read. NOT DELIVERED: starting a new run from checkpoint.
