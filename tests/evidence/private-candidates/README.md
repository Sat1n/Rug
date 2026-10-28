---
id: rug_private_candidate_validation
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: []
tags: [observed-click, candidates, verification]
---

# 3.5-08D-01 verification — 2026-10-28

Starting HEAD includes `5ac4442`. Only private observed-click candidate selection,
its bounded diagnostics and tests change. CaptureService, Native code, public
capture/recognition/cache, input delivery, arbitration, recovery, SDK/packages and
unfiltered CaptureTimeProbe are unchanged. No PR or environment policy changes.

## Contract and decision boundaries

Each existing phase gets <=3 sequential final ICaptureService candidates including
first, with one min(750ms, stricter host limit) timer/QPC deadline. Original source
IDs/coordinates/qualification/default1000ms or tightened age remain fixed. Guards
run before fetch, after actual return and before acceptance; original cancellation,
run/grant/source checks win. No abandoned service tasks, new input lock, request
replay, movement/down/up replay, source refresh or recognition retry.

Only structured candidate-local missing/invalid/future/not-newer time can discard.
Native invalid flags and invalid original native sample cannot recover when host
QPC catches up. Keep maximum positive discarded render timestamp; acceptance must
be strictly newer and after its own fetch request. Clock/domain/frequency anomalies,
old-but-valid request-time violations, geometry/ROI failure, null frame, real IO,
capacity or authority failure are terminal. Time-valid geometry/ROI candidates are
not searched past. Private publication occurs only after those checks and final
guard; failed/discarded candidates mint no private frame ID/public action reference.

Budget expiration maps to existing ValidationUnavailable, source expiry to Stale,
three time-only discards to TimeUnavailable. Post-move rejection stays uncertain;
mandatory releases retain original flow. Per-attempt existing pool reservation is
released before next fetch. Native noncooperation retains actual work/service/input
and byte ownership until real completion. Recording cannot change the decision.

New nullable schema-v1 TraceObservation.Candidate records bounded stage/attempt/
event/reason, alongside unchanged TimeRejection raw/source/QPC facts. Attempt0 is
pre-fetch; Acquiring is an attempt, not proof Native ran. StageEnd AcceptedFirst /
AcceptedAfterDiscard is phase-level, not business success. Prior accepted private
frame IDs remain attached; discarded candidates have only original source IDs.

## Deterministic Gateway/transaction checks

No validator-success injection: trusted capture/QPC doubles supply pixels/facts;
real transaction time/geometry/ROI code, Gateway authorization, input lease,
original cancellation and lifecycle execute. Both delivery modes use fake input,
never real SendInput. Fixture disposal asserts zero pooled bytes/reservations/
frames/operations/sessions and active inputs. Actual calls are asserted:

| Case | Counts / outcome |
| --- | --- |
| First valid, each mode | 2(PostMessage)/3(SendInput) total captures, exactly one move/down/up |
| One/two discards per phase | phases*(2/3) captures, unchanged source/point, one move/down/up, only phases private IDs |
| Invalid native time, same/caught-up frame | 3 captures, TimeUnavailable, zero move/down |
| Same rejected stamp repackaged as valid | discarded; strictly newer third accepted, no duplicate action |
| Valid time after invalid but ROI/geometry changed | terminal at second candidate, zero input; no extra search |
| Null/IO/old/candidate-clock/host-clock after discard | terminal at second acquisition, original error semantics, zero input |
| Shared deadline 100/750/2000 host config | 2/3/1 captures respectively; 2000 still capped750; no new input |
| Caller source age100ms expires at150ms | Stale after one fetch, no source swap/input |
| Stop/revoke/takeover/cancel/blocked-cancel/timeout during second candidate | actual task held; no following down; before-move counts0/0/0, after-move1/0/0 and uncertain |
| Two instances/capacity | cross-source rejected; pressure refuses before Native; released capacity and per-attempt reservation verified; each instance receives one down/up |
| Trace write failure | candidate transaction still succeeds once; no retained frame/work |

Source native-time diagnostic tests now expect three candidate-local failures
rather than the old first-failure count. Unknown and cross-domain values remain
unknown; exact source/QPC facts survive checked offline reopening. Existing final
geometry/original-token barriers and necessary-release tests remain passing.

## Commands and results

For each Debug and Release:

```powershell
dotnet build Rug.UI/Rug.UI.csproj -c <configuration> -p:Platform=x64 -warnaserror
dotnet build tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 -warnaserror
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 --no-build
dotnet run --project Rug.UI.Tests/Rug.UI.Tests.csproj -c <configuration> -p:Platform=x64
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 --no-build -- --private-candidate-tests
```

Builds0 warnings/errors; full script and UI core each exit0 in both configurations.
Full includes Lua/Gateway/authorization/arbitration/lifecycle/recovery/checkpoint/
observations/traces/hot reload/runtime switch. New candidate tests are in the full
observed-click suite. Final explicit candidate/observed-click/trace/recovery/
lifecycle selector commands also exit0 in both configurations after diagnostic
end-reason labels were refined. Logs distinguish those runs. No Native rebuild.

Initial full Debug run failed removing .rugtrace.lock from its owned temporary
diagnostic directory: bounded recorder Dispose had not yet finished actual file
IO. The [first failure](initial-full-writer-cleanup-failure.txt) remains. Test owner
now awaits bounded **actual test-writer completion** before checked-file read/
directory cleanup. Production bounded flush/shutdown remains unchanged; no delay,
forced GC, swallowed failure or recorder policy relaxation was introduced. Final
full runs pass. This was automated-test cleanup, not a real desktop sample failure.

## First real desktop runs

User confirmed keyboard/mouse idle before actual desktop runs and was notified
when finished. Each command below ran exactly once per configuration on owned
isolated target/plugin/data, with real WGC/OpenCV/PostMessage and actual WinUI
viewer/recovery interaction. No business SendInput/new human takeover acceptance.

```powershell
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 --no-build -- --controlled-visual-sample artifacts/08d-first-<configuration>-visual
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c <configuration> -p:Platform=x64 --no-build -- --controlled-recovery-sample artifacts/08d-first-<configuration>-recovery
```

| First complete command | Functional cases | Aggregate exit | WinUI natural exit |
| --- | --- | --- | --- |
| Debug08C-01 | 8/8 | 0 | 0 |
| Release08C-01 | 8/8 | 0 | 0 |
| Debug08C-02 | 8/8 | 0 | 0 |
| Release08C-02 | 8/8 | 0 | 0 |

Visual stable: actual1/1, fresh business confirmation; changed/resize/stale/
invalidated: actual0/0 with their precise refusals; Stop/revoke0/0; no-completion
actual1/1 but business unconfirmed (API completion not success inference).
Recovery success: BEFORE1/1 and AFTER1/1, original Lua locals/state continue once;
waiting/validation Stop/revoke/late cancel paths: BEFORE1/1, AFTER0/0, expected
terminal state. Real recovery UI ends2/2. Trace integrity Valid, actual work0,
run cleanup/resources drained; incident identities/links preserved. Reports retain
IDs and outcome detail separately. No assertion, forced exit or hidden nonzero.

**No time-invalid private candidate occurred naturally in these four runs.**
Discard-then-accept/exhaustion branches are covered by deterministic tests only.
The zero discard count does not mean underlying WGC future/unknown time disappeared.
Raw capture-time-probe code remains unfiltered and was not rerun in this unit;
there was no loop to accumulate a desired branch or a green result.

See [first command codes](first-real-command-exits.json),
[checked trace phase summary](real-evidence-summary.json),
[original successful stage event lines](real-stage-end-sample.jsonl), and the four
*-report.json files. Native hashes are recorded separately. The full regression
logs and final selector logs retain actual command outcomes. CLI text copies trim
trailing whitespace only; original artifacts remain unchanged.

## Limits / not run

Source itself can still be invalid or expire during extra captures/movement;
then it is refused, without replacing source or retrying input. Three candidates
or750ms can still be exhausted. General scene/geometry refusal, animation/hover
limits and supported capture mapping are unchanged. Native uncooperative work
can still delay actual cleanup; it is never released early to satisfy a deadline.

No raw-probe rerun, new human takeover/real business SendInput acceptance, Native
change, Windows future-time explanation, historical Stale root-cause closure,
Hook-gap fix, Cancelled archive-classification fix or SAC compatibility fix is
claimed. This unit adds bounded **private candidate selection** only.
