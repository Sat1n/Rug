---
id: rug_env01_validation
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: []
tags: [environment, application-control, regression]
---

# 3.5-ENV-01 — 2026-10-28

Baseline: `3c723829e9697cfe84d044826f0a51a93fe8f2aa`. **Regression is restored in
the current environment; permanent trust under the former enforced policy is
not established.** No business logic, assertion, SDK, Windows App SDK, Native
dependency or security setting was changed. SDK=10.0.402; App SDK=1.6.240829007.

## Reproduction and attribution

- One bounded capture attempt in execution isolation at 14:56:45–14:56:48
  started the requested EXE/managed assembly and loaded the Native chain, then
  failed `Rug_CreateCapturer`, status -4 (`RUG_ERR_CAPTURE_FAILED`), CLI exit
  -532462766. This is not 0x800711C7. Complete exception: `initial-isolated.log`.
- The same `dotnet run --no-build -- --capture-time-probe` entry in the ordinary
  user desktop at 14:57:30–14:57:38 returned 0: 128 captures, zero input, stopped
  capture. Its token was **not administrator**. No project 3077 occurred during
  these initial attempts. The isolation failure was not retried or relabeled.
- Historical records confirm three different loading stages. The current files
  below remain `NotSigned` and match the historical event's Flat SHA256 exactly:

| Stage / affected repository-relative file | 2026-10-23 evidence |
| --- | --- |
| Process: `tests/Rug.UI.Core.Script.Tests/bin/x64/Debug/net10.0/Rug.UI.Core.Script.Tests.exe` | 10:25:08, 3077 record 329 |
| Managed: `tests/Rug.UI.Core.Script.Tests/bin/x64/Release/net10.0/Rug.UI.Core.Script.Tests.dll` | 10:27:28, record 377; ten retained blocks |
| Native: `x64/Debug/Rug.NativeRawReceiver.dll` | 10:26:44, record 337 |
| Transitive dependency: `x64/Release/jpeg62.dll` | 09:28:40, record 309 |

Original policy: `VerifiedAndReputableDesktop`, GUID
`{0283ac0f-fff1-49ae-ada1-8a933130cad6}`, status `0xC0E90002`, requested/validated
signing levels 2/1. `historical-block-details.json` retains exact timestamps,
paths, SHA256 and correlation IDs; file inventories retain current signatures.

**Current environment differs:** the first state read at 14:59:34 already found
`VerifiedAndReputablePolicyState=0`. Defender independently reports SAC **Off**;
DeviceGuard reports user-mode enforcement=0, kernel enforcement=2. Old policy
files and 3099 activation records still exist; this does not prove current
user-mode enforcement. Last retained SAC activation: 2026-10-23 09:27:59.
When/why/by whom SAC became Off is unknown. This unit made no such change.

Supported conclusion: **temporarily recovered; cause of the environment state
change undetermined**. No trust rule, signing certificate, exclusion,
Unblock-File, administrator test token, alternative load entry or test bypass
was used. Success here is not acceptance under the former enforced policy.

## Commands and exit codes

Each command below ran with `<Configuration>` set to Debug and Release. Build
and verification ran 14:58:50–15:04:31, in the normal nonadministrator desktop.

```powershell
dotnet build tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <Configuration> -p:Platform=x64 -warnaserror
dotnet build Rug.UI/Rug.UI.csproj -c <Configuration> -p:Platform=x64 -warnaserror
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <Configuration> -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c <Configuration> -p:Platform=x64
.\tests\run_host_exit_probe.ps1 -Configuration <Configuration> -Count 2 -OutputRoot <separate-evidence-directory>
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <Configuration> -p:Platform=x64 --no-build -- --capture-time-probe <separate-capture-directory>
```

Both builds were then repeated with `-t:Rebuild`; new processes repeated the
host-exit and capture commands. All **20** matrix commands returned 0, with no
forced termination or new project 3077. The initial isolation failure is
preserved separately, not included in that passing count.

| Check | Debug exit | Release exit | Local evidence prefix |
| --- | --- | --- | --- |
| Script-host build, warnings as errors | 0 | 0 | `Build-<Configuration>-scripts` |
| WinUI build, warnings as errors | 0 | 0 | `Build-<Configuration>-winui` |
| Complete default scripts | 0 | 0 | `Verify-<Configuration>-scripts` |
| UI suite, build and run | 0 | 0 | `Verify-<Configuration>-ui-tests` |
| Natural host exit, count 2 | 0; children 0/0 | 0; children 0/0 | `Verify-<Configuration>-host-exit` |
| Read-only Native capture | 0 | 0 | `Verify-<Configuration>-capture` |
| Script-host Rebuild | 0 | 0 | `Rebuild-<Configuration>-scripts` |
| WinUI Rebuild | 0 | 0 | `Rebuild-<Configuration>-winui` |
| Rebuilt host natural exit, count 2 | 0; children 0/0 | 0; children 0/0 | `Final-<Configuration>-host-exit` |
| Rebuilt capture, new process | 0 | 0 | `Final-<Configuration>-capture` |

All builds reported zero warnings/errors. Complete scripts exercised the native
Raw receiver lifecycles and isolated PostMessage cleanup; no new code assertion
or business failure occurred. Eight WinUI children exited naturally with empty
stderr, no CLR assertion, no kill. Rebuilt EXE/assembly hashes equal the first
built hashes; Native hashes are unchanged. No current intermittent block was
observed; this does not predict behavior if the former policy is re-enabled.

## Capture facts remain distinct from exit 0

| Pass | Samples | Future at Native | Managed time unavailable | No frame | Stopped / input |
| --- | --- | --- | --- | --- | --- |
| First Debug | 128 | 1 | 1 | 0 | true / 0,0 |
| First Release | 128 | 0 | 0 | 0 | true / 0,0 |
| Rebuilt Debug | 128 | 0 | 0 | 0 | true / 0,0 |
| Rebuilt Release | 128 | 0 | 0 | 0 | true / 0,0 |

The future/unavailable Debug frame remains recorded. Probe exit 0 only means
sampling and cleanup completed, not that all frames were actionable. The
preliminary user probe separately sampled 128 frames with all three counts zero.

## Evidence and outstanding limits

Local root: [artifacts/environment-recheck/env01-20261028](../../../artifacts/environment-recheck/env01-20261028).
Every command has `*.json` with exact executable/arguments, timestamps, exit,
forced state and event count, plus complete `*.stdout.log` / `*.stderr.log`.
`all-commands.json` and `summary.json` aggregate results;
`files-before-verification.json` / `files-after-rebuild.json` preserve full
paths, SHA256 and signatures. `natural-exit-summary.json` retains child
PIDs/exits; `capture-summary.json` links the four raw producer reports. First
captures use `artifacts/environment-recheck/debug` and `release`; rebuilt
captures have independent directories under the dated local root.

`initial-event-summary.json`, `historical-block-details.json`,
`user-environment.json`, `defender-status.json`, `deviceguard-status.json` and
`session-ci-status.json` preserve environment/event facts. Detailed system
material remains local/ignored; no binary, dump or policy payload is committed.
The [original review logs](../../../artifacts/review-time-rejections) and
[time-rejection evidence](../host-exit/time-rejections/README.md) are unchanged.

Owned capture-marker windows and isolated WinUI hosts were closed after testing.
No external game, real SendInput, human takeover acceptance, Hook-gap closure,
cancellation classification or business-time fix is claimed. The isolation-only
capture failure, capture-time anomaly and SAC Off provenance remain unresolved.
Permanent trust under an enforced policy remains **not accepted**; any future
trust/policy adjustment needs its own scoped basis, impact and rollback plan.

Microsoft references: [event/signature diagnosis](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/operations/appcontrol-debugging-and-troubleshooting),
[SAC state definitions](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/test-your-app-with-smart-app-control).

Documentation checks: changed local Markdown links and `git diff --check` passed.
The commit hook reported OmniAtlas CLI unavailable and skipped its documentation
sync check; that additional topology check was not completed.
