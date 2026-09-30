---
id: rug_plugin_authorization
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: [rug_ui_core]
tags: [authorization, permissions, revocation]
---

# Explicit plugin grants (Task 3.5-04)

[IPluginAuthorization](Rug.UI.Core/Abstractions/IPluginAuthorization.cs#interface:IPluginAuthorization)
is a trusted host management contract. The default
[PluginAuthorization](Rug.UI.Core/Services/PluginAuthorization.cs#class:PluginAuthorization)
stores grants only in process memory, outside all StateStore domains. Scripts,
schema commands and external request parameters cannot invoke management methods.
Host composition supplies verified catalog manifests; adapter code receives a
host-created caller, never a grant/principal supplied by request JSON.

## Identity and effective permissions

`PluginInstallation` consists of plugin ID and absolute normalized installation
directory, compared case insensitively on Windows. Another directory with the
same ID has independent grants. This is location binding, not a signature,
content hash or physical-directory identity resistant to directory replacement.
The host verifies the installation through PluginManager, not request data.

Permission names are exact and case sensitive: `timer`, `vision.capture`,
`vision.ocr`, `vision.match`, `input`, `filesystem`, `network`, `agent`. Unknown
names cannot be granted. There are no wildcards. Effective access intersects
current catalog declarations, the run's captured declarations, the host-issued
caller capability ceiling, current grants and host policy. A grant cannot exceed
the catalog declaration. The optional authorization policy is a pure synchronous
predicate; it must not invoke services, dialogs or block. Gateway's replaceable
operation policy additionally restricts admission, never enlarges permissions.

`Register`, `Query`, `Grant` and `Revoke` return detached read-only snapshots with
requested, granted and effective lists and an installation-local revision.
Actual declaration/grant changes increment revisions. Changed notifications run
outside the authorization lock; consumers must compare revisions or query current
state because concurrent notifications can arrive out of order. Observer failures
cannot interrupt cancellation or other observers. There is no persistence.
Installation records remain for the host session to preserve grants and revocation
stamps; they do not grow per run. Disposable admission leases are removed on
operation completion and instance cleanup, without per-run event subscriptions.

## Startup and dispatch

All current requested permissions must be granted before starting. Gateway checks
the current requested list, not an old caller snapshot. Scheduler independently
enforces this before capture/input/runtime factories and capture binding, including
direct internal starts and hot-reload restarts. Standalone Lua initialization also
enforces it. Standalone hosts must provide a verified nonempty plugin ID and
installation directory, and explicitly supply/manage authorization through the
runtime or shared Gateway. Zero requested permissions need no confirmation.

Caller permissions are only ceilings. Tickets bind the issuer, installation and
per-permission revocation stamps. Narrowed callers cannot use a runtime's broader
permissions. Gateway performs admission validation and, on the worker immediately
before dispatch, acquires an authorization lease atomically under the grant lock.
Queued requests revoked before that boundary never enter the handler. The service
call runs after releasing the lock. Revocation after admission cancels the lease;
an already admitted operation may have begun side effects and is not retroactively
classified as never executed. The optional host dispatch-wait delegate allows
testing queued admission without invoking handlers under a lock.

`PermissionInterceptor` uses the same live source and issuer/location checks for
capture, OCR, matching, input, external templates (`filesystem`) and the existing
anomaly hook (`agent`). External-path permission is also checked against the
Gateway caller ceiling. Existing symlink, coordinate, parameter and identity checks
remain. State/config/log keep their existing scope contracts. Lua reads of
structured state/config use ordinary Lua tables (one-based arrays, JSON null
as nil), not CLR userdata that could expose reflection. Managed snapshots and
StepFlowView retain their structured JSON data. Writing permission-like state
keys cannot affect the separate authorization store. State/config/log callback
failures are converted by a private protected-call wrapper to Lua strings, so
catching an error cannot return a CLR exception userdata either.

Permission rejections use `PermissionDenied`, `DeniedPermission` (first missing
name) and a read-only `MissingPermissions` list. No grant or internal exception
stack is accepted from/returned to adapters. Identity failures retain their own
stable codes; ended runs are rejected before service access.

## Revocation and reload

Revoke first removes grants and increments revocation stamps under the grant
lock. After releasing it, it cancels affected leases. The scheduler's full-declaration
lease cancels the instance token, causing watchdog interruption, `on_stop` and
ordinary capture/input/runtime cleanup. Other installations and runs not declaring
the revoked permission continue. KeyUp and resource cleanup do not require grants.
Stop remains available through the trusted scheduler and the UI lifecycle path.

Old leases stay cancelled and old tickets for revoked capabilities stay invalid
even after regrant. A fresh context and normal start create a fresh RunIdentity.
No delayed result can resurrect Lua or publish a cancelled screenshot. Existing
noncooperative-work observation, cancellation/uncertain-result and bounded
deduplication behavior remains. Revocation does not independently clear dedup
records: ordinary ended-run eviction is safe because that identity cannot be
re-admitted, while start records retain their normal bounded TTL.

Ordinary code updates at the same installation keep existing grants. Catalog
refresh removes grants for deleted declarations and invalidates their stamps.
New/re-added permissions are not auto-granted. Hot reload still stops/replaces
runs; an expanded manifest fails restart with a permission error and waits for
explicit foreground host approval. Background reload and Agent/MCP never show
dialogs. This task does not add transactional reload or persistent approvals.

## Foreground UI and verification

`PluginPermissionConsent` owns a host ContentDialog showing installation, plugin
ID, exact permissions, purposes and current grants. Explicit foreground Start
asks if needed; Allow grants only the displayed declaration then continues.
Deny/close does not start. Manifest changes while the dialog is open cannot
expand that approval. The main-window **Plugin permissions** button displays
current grants and can revoke all of them. The dialog runs on DispatcherQueue
outside Gateway/scheduler and authorization locks, with dismissal as the default.

The script suite uses explicit `TestAuthorizationHost` approvals; production has
no auto-approve switch. `AuthorizationTests` covers no-grant factory counts, caller
ceilings/forgeries, installations/issuers/unknown names, external paths, queue and
active revocation, siblings/other plugins, noncooperative results, regrant, expanded
hot reload and repeated-run lease cleanup. Actual UI Automation covers denial,
confirmation, revocation and regrant, plus instance switching and retained demo
completion timelines. Native Minecraft/OCR coverage remains separately opt-in.
