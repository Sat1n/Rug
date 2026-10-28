---
id: rug_host_shutdown
type: logic_node
inputs: [rug_ui, rug_ui_core]
outputs: []
tags: [shutdown, lifecycle, diagnostics]
---

# Orderly host shutdown — 3.5-EXIT-01

## Root cause and evidence

The empty-plugin, no-task, no-viewer Debug host reproduced the CLR assertion and
natural exit `-1073740286 / 0xC0000602` on Windows App SDK **1.5.240311000**.
Temporarily disabling notification registration alone, removing the UISettings
subscription alone, using Application.Exit alone, and clearing XAML content alone
each retained the failure. These were diagnostic builds, not shipped feature flags.
See the [preserved evidence](../tests/evidence/host-exit/README.md).

A local debugger caught C0000602 before natural process exit. Native stack/PDB
resolution shows `Microsoft.UI.Xaml.Controls.dll` resolving
`weak_ref<IApplicationFeature_UwpSupportApi>::get+0x44` during CRT on-exit / DLL
process detach, then entering an already torn-down CLR thread. The following
revoker symbol has a large displacement: it is supporting symbol information,
not an exact source-line attribution. The public 1.5 source has a global
Application.Suspending revoker in RuntimeProfiler, revoked from static teardown;
the first stable 1.6 source removes that Application subscription/revoker.
This strongly localizes the crash to the native WinUI static event teardown;
notification/theme disposal alone did not fix it. We do not claim that every
possible shutdown error is this fault, or that .NET itself was proved defective.

Only the explicit production dependency Microsoft.WindowsAppSDK changes to
**1.6.240829007** (first stable 1.6). The version-only empty-host contrast exits 0
with the same actual **.NET 10.0.13** runtime. SDK pin remains 10.0.402, framework,
Core/native binaries and other direct package versions remain unchanged. The
explicit WindowsSdkPackageVersion **10.0.22621.57** equals the reference already
resolved by SDK 10.0.402; it satisfies 1.6's old build guard, which does not recognize
the renamed .NET 10 Windows framework reference. This is not a Windows SDK upgrade.
Stale UI bin/obj MSIX content must be cleaned when changing the self-contained SDK;
root x64 native/frozen diagnostic binaries are not rebuilt for that purpose.

Primary evidence sources:

- [1.5 RuntimeProfiler source](https://github.com/microsoft/microsoft-ui-xaml/blob/4c6485cd706a43ede492deb3a544ae6c39f74b5a/controls/dev/Telemetry/RuntimeProfiler.cpp)
- [first stable 1.6 RuntimeProfiler source](https://github.com/microsoft/microsoft-ui-xaml/blob/56eeab6f5295e0efcf95d0494ca8fcb924e7cee1/src/controls/dev/Telemetry/RuntimeProfiler.cpp)
- [.NET discussion of invalid entry after TLS destruction](https://github.com/dotnet/runtime/issues/117538)

Full dumps/PDBs and diagnostic tools remain ignored local artifacts; they contain
process memory and are not committed. The committed stack excerpt contains no dump.

## Close ownership and order

[MainWindow](MainWindow.xaml.cs#class:MainWindow) cancels the native Closing event
until one shared close sequence finishes; repeated requests do not destroy the
window early. UI admission closes first. The scheduler closes a shared host
business gate under its existing lifecycle gate, before cancelling tokens outside
that gate. All existing instances, queued input rechecks and startup paths reject
new business even if another cancellation callback blocks propagation.

[HostShutdownSequence](Helpers/HostShutdownSequence.cs#class:HostShutdownSequence)
runs fixed stages outside its gate, on the UI owner context. Sibling unsubscribe
and cleanup actions are all attempted; failed stages are reported and later stages
still run. Failure sets Environment.ExitCode=1, without forcing process exit.

1. Close scheduler and already-created DevTools admission synchronously. Do not
   resolve services merely to dispose them.
2. Disable new UI work; detach theme, command, scheduler, input, demo and hot-reload
   subscriptions; cancel owned checkpoint preparation.
3. Drain entered UISettings callbacks. Queued callbacks recheck close state and do
   not touch a closed window.
4. Notification owner unsubscribes NotificationInvoked and unregisters before
   window teardown. Drain entered callbacks, suppress old queued UI deliveries,
   hide and await the owned active notification dialog. No finalizer owns this work.
   At most one notification UI callback is queued/active.
5. Close an existing viewer; its existing broker closes UI queries without waiting
   indefinitely for disk. Dispose hot reload and intervention sessions.
6. Await DevTools preview start/capture/OCR and active commands, then stop capture.
   Do not release the capture service while native work still uses it. Detached
   pages reject late FrameReady/log deliveries.
7. Await scheduler starts, all normal Stop paths, actual work, necessary releases,
   bounded on_stop, run resources and trace close. Repeated scheduler disposal
   shares the same completion task. One failing shutdown action does not skip
   other cleanup attempts.
8. Detach dynamic UI/demo, Stop the Generic Host, asynchronously dispose its
   container, then Close the main window naturally.

DI owns singleton destruction. Pre-close operations quiesce already-created
services through their idempotent shutdown/disposal contracts; container disposal
later observes completed cleanup. Per-run resources remain scheduler-owned.
Native capture/input ownership and bounded trace flush are unchanged. A genuinely
noncooperative Native operation keeps shutdown pending; the test timeout may kill
only its own child and must record failure. No production forced exit, forced GC,
fixed-delay workaround, hidden assertion or permanently disabled feature is used.

## Repeatable validation

From repository root, build matching native prerequisites first if absent:

```powershell
dotnet build Rug.UI/Rug.UI.csproj -c Debug -p:Platform=x64 -warnaserror
dotnet build tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -warnaserror
.\tests\run_host_exit_probe.ps1 -Configuration Debug -Count 5
.\tests\run_host_exit_probe.ps1 -Configuration Debug -Viewer -RepeatClose
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build -- --host-exit-ui
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c Debug -p:Platform=x64
.\tests\run_controlled_visual_sample.ps1 -Configuration Debug
.\tests\run_controlled_recovery_sample.ps1 -Configuration Debug
```

Repeat with Release. The empty probe records actual loaded module paths/product
versions, ready/close/exit phases, stdout/stderr and natural exit codes. Readiness
polling is bounded; it is not a shutdown delay. `--host-exit-ui` starts three
independent owned hosts: live WGC preview; active PostMessage observed-click run;
AwaitingHuman retained Lua. It checks real UIA interaction, received counts,
actual-work/cleanup trace summaries and process exit. It does not synthesize human
acceptance. Callback/preview failure and close races use actual implementation
source with deterministic test doubles, separately from real WinUI evidence.

Historical 08C-01/02 failure reports remain unchanged. EXIT evidence records the
new successful commands rather than retrospectively marking old commands passed.
Cancelled classified as ValidationTimedOut remains a separate known issue; the
independent Hook observation gap is unchanged. No new real SendInput or human
intervention acceptance is claimed.

## Independent Release review: business timing remains separate

The CLR close repair is independently confirmed. Subsequent Release recovery
runs reported TimeUnavailable/Stale. The [2026-10-23 directed investigation](../tests/evidence/host-exit/time-rejections/README.md)
records actual native-time/current-QPC evidence, old/new-code comparison and all
nonzero commands. A pre-existing future-valued capture-time condition is observed
on both versions; original spontaneous Stale exact ages were not collected.
Do not interpret the earlier successful sample runs as universal business
acceptance or weaken input time protection to make them green. This follow-up
changes diagnostics only, not capture/input/recovery decisions.
