---
id: rug_plugin_updates
type: logic_node
inputs: [rug_ui_core]
outputs: [rug_ui]
tags: [plugins, snapshot, validation, hotreload]
---

# Candidate snapshots and UI-only updates (3.5-07A)

[Pipeline](Services/PluginUpdatePipeline.cs#class:PluginUpdatePipeline) implements
Prepare -> Validate. The [watcher](Services/HotReloadService.cs#class:HotReloadService)
debounces and serializes publication; neither stops, restarts or resumes Lua.
Runtime switching, transactional installation and checkpoint migration are deferred.
The [07B-01 host preparation](RuntimeUpdates.md) can explicitly reserve all Running
checkpoint-capable sources and freeze new successful submissions. Saving files
does not arm this flow; newer candidates cancel old plans without switching code.
Input detection and the independent Hook observation gap remain unchanged.

## Snapshot and validation

PrepareAsync captures manifest, optional config/ui (explicit absent entries), entry
regardless of extension, and recursive Lua inventory. Inventory membership records
Lua presence/deletion; empty and absent differ. Each detached file has text, exact
raw-byte SHA-256 and byte length. Decode once using strict UTF-8 with BOM detection;
preserve newlines. Reject links, linked directories and escaping entry paths.
PluginCandidate exposes immutable strings/records/read-only maps. PluginSnapshot's
manifest/config getters return independent copies.
The watcher monitors JSON/Lua paths plus the last validated entry path even if its
extension is not .lua; unrelated asset writes do not trigger reconstruction.

ContentVersion hashes ordinal-path-sorted JSON tuples (path, existence, file hash).
RuntimeVersion uses the same rule excluding only ui.json. Any other change is
conservatively runtime-affecting, including manifest display metadata/formatting.
Config defaults are parsed from captured text with PluginManager's shared rules;
bounded JSON values are checked before publication. Validation does not mutate the
manager's scanned/config cache. It checks metadata, identity, safe entry, exact known
permissions, typed config, SchemaParser and every Lua file plus the entry.
Lua.LoadString compiles/disposes functions without calling them: no top-level code,
lifecycle, input, service initialization or permission approval is executed.

Limits: 1 MiB/file, 4 MiB/candidate, 128 captured entries, 128 directories, depth 8,
4096 enumerated files; 64 effective plugins, one in-flight candidate, 64 queued
directories (one latest slot each), 128 revision slots and 128 UI handlers.
Overflow is observable. Pending runtime results retain summaries rather than an
unbounded archive of candidate bodies. Disposal clears queues/handlers and cancels
Ready if startup did not finish.

## Effective catalog and results

Start()/Ready establish the initial validated catalog before WinUI activation.
GetPluginSnapshotsAsync supplies navigation and UI; the shell no longer rescans disk.
Once enabled, RefreshPluginsAsync cannot replace this catalog with raw file contents.
Normal startup uses applied manifest/config and exact captured entry string through
InitializeLoadedAsync. Legacy hosts can scan before enabling this mode; production
hosts enable it before first startup. Checkpoint startup rejects unapplied runtime
file changes instead of using disk to bypass the effective catalog.

The existing per-plugin start/reload lease serializes classification/publication.
Running tasks and other plugins continue. No lifecycle lock performs validation,
disk IO, UI callbacks or waits.

| Status | Effect |
| --- | --- |
| ValidationFailed / ReloadFailed | Retain old UI, subscriptions, catalog and runs; includes UI construction/commit failure |
| Unchanged | No reconstruction/restart; clear obsolete failure notice |
| UiApplied | Replace from captured UI; rebind same run/state/fallback; preserve coroutine and pause |
| RuntimePending | Active instances keep old UI/code/config/permissions; show 更新待应用, not success |
| RuntimeApplied | No active instance: publish snapshot; next explicit start uses normal consent |
| Superseded | Discard older validation/staging; no publication |

Active includes Starting, Running, Paused, Recovering, AwaitingHuman and Stopping.
No on_init/on_stop runs during an update. Pending permission changes do not alter
effective requests or grant anything. Explicit revocation still immediately stops
affected old runs through the existing path. After stopping, another save or trusted
RequestReload(directory) retries Prepare/Validate; this unit never automatically
restarts. Additional explicit starts while pending use last-applied code.

Every observed save receives a monotonic process-local revision; bursts merge over
400ms. A newer revision observed before the final publication decision discards the
older result. After publication begins, the single worker completes it before
processing newer saves, so old work cannot overwrite a newer publication. Applying
never rereads validated content, even if disk has changed. The captured set is per-file,
not an OS-wide atomic directory snapshot; later observed changes create another candidate.

## UI transaction

RegisterUiHandler receives the PluginSnapshot and returns IPluginUiUpdate
(CommitAsync/RollbackAsync/DisposeAsync). Build and bind every replacement before
removing any old tree. Failed staging disposes only new subscriptions. Failed commits
roll back previous hosts; old factories remain owned until catalog publication.
Success releases old subscriptions before the applied notification. UI defaults
are bounded/staged during construction and published only on success. Later controls
read a shared key's first staged default with committed-state JSON type semantics;
incompatible bindings reject the candidate before replacing the old UI or config.
DynamicUIHost checks its view generation on the dispatcher so navigation/unload
cannot apply an obsolete tree. Callbacks/observers run outside watcher/lifecycle
locks; shutdown awaits actual dispatched work. Reloaded is a result event: callers
must inspect Status, rather than treating RuntimePending as success.

## Validation

```powershell
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --plugin-update-tests
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64
pwsh -NoProfile -File Rug.UI.Tests/HotReloadUiSmoke.ps1
pwsh -NoProfile -File Rug.UI.Tests/GenericInstanceUiSmoke.ps1
pwsh -NoProfile -File Rug.UI.Tests/LifecycleUiSmoke.ps1
pwsh -NoProfile -File Rug.UI.Tests/Phase3E2E.ps1
```

Build app and test projects Debug/Release x64 with -warnaserror; repeat noninteractive
suites with Release. Actual Lua/barriers cover invalid files, identity/unknown
permissions, paused state, real Recovering/AwaitingHuman, compile-only chunks,
pending config/code/permissions, revocation, fixed startup code, UI rollback,
cleanup, ordering, post-validation disk mutation and bounded queue/candidate sizes.
UI automation checks retained paused timelines, actual UIFactory type-binding build
failure (including conflicting defaults on a missing key and a title-only retry),
unchanged UI tree/run generation and unpolluted config after failure,
corrections without reapplication, lifecycle/grants
and completed demo state. These are not new human/input-source acceptance results.

Snapshots are process-local, not persisted installations. Invalid updates do not
roll back disk files; restarting the app does not recover an old in-memory snapshot.
No active-run code switch, automatic migration, checkpoint replay, cross-process
publication or arbitrary trusted-host callback transaction is claimed.

## Recorded verification (2026-10-19)

SDK 10.0.402; Debug/Release x64 app (including Core), script-test and UI-test
builds: zero warnings/errors. Both configurations passed the new actual-Lua
snapshot-update suite and Schema/state/real-watcher tests. Existing full script
regressions passed in both configurations and cover Gateway, authorization/revocation, recovery/checkpoints,
lifecycle, arbitration and intervention; Hook diagnostics are not modified.
Real WinUI automation passed HotReloadUiSmoke (UI update 472ms, error recovery
449ms, unchanged correction 453ms), GenericInstanceUiSmoke (paused timeline and
actual build-failure retention), LifecycleUiSmoke (same paused run retained), and
Phase3E2E (completed timeline preserved, update 512ms). These are automation
results; no new real-human keyboard/mouse acceptance is claimed or required here.
