---
id: rug_exit_time_followup
type: logic_node
inputs: [rug_ui_core, rug_ui]
outputs: []
tags: [diagnostics, qpc, wgc, regression]
---

# EXIT-01 Release time-rejection investigation — 2026-10-23

**Not an all-green business acceptance.** CLR exit repair remains independently
confirmed. Current 08C-02 completed twice, but an additional 08C-01 changed-scene
run rejected TimeUnavailable instead of reaching SceneChanged. Its aggregate
command stays failed. No threshold, click retry, input timing, Native DLL,
permission or recovery behavior was changed by this investigation.

## Established facts

1. `git diff 95149f3 73b018f` is empty for Rug.Core, CaptureFacts, CaptureService,
   ObservedClickServices, InputBridge, ControlledRecoverySample and the recovery
   plugin Lua. The capture/time/input implementation was not directly edited by
   EXIT-01. This alone would not exclude indirect timing effects.
2. Actual current Release changed-scene rejection is recorded in
   [original checked event lines](current-real-time-rejections.jsonl). The action
   source is frame `36accf1e-be1c-41ea-baa7-d8479a0520be`; its age was 91.5975ms.
   The rejected **unpublished private verification candidate** has raw render
   time 2310656657572 (100ns), native sample 2310656652776 QPC, current QPC
   2310656656839 and frequency 10000000. Render time is 0.4796ms ahead of native
   sample and still 0.0733ms ahead at rejection. Native flags=4: time-valid bit
   absent; managed NativeTime is null. The existing time guard rejects before ROI
   comparison. This run did not prove SceneChanged. No candidate frameId or
   successful observation is invented for this unpublished frame.
3. A bounded read-only exact-WGC producer audit uses the same Native DLL and the
   same actual QPC interface in both implementations. No tasks/inputs/clicks are
   retried, and no input is emitted. Current: 2/128 future-at-native candidates;
   95149f3: 1/128. All three have native flags=4 and managed time unavailable.
   Baseline index108 has render=2314309968293, native sample=2314309961652,
   frequency=10000000, signed native age=-6641 (100ns), or -0.6641ms. At managed
   receipt age was already +0.2994ms, but it correctly remains unavailable: raw
   diagnostic values do not retroactively grant action eligibility.
4. [Native hashes](native-hashes.json) match across root and baseline copy:
   Rug.Core SHA256 4C84E319E4D81714ADCA7D00CEF01D7E0BCF3A262E9CD64FC5E90C2F632E0E5D.
   No Native rebuild or dependency replacement occurred. The samples establish
   **presence on the old implementation**, not a statistical frequency comparison.
5. User's historical Stale rejects passed initial ROI and began input side effects.
   Their movement-start-to-rejection intervals are 605.67–649.19ms;
   SourceResolved-to-rejection intervals are 835.54–882.39ms. See
   [checksum-verified historical extraction](historical-rejections.json).
   Original native timestamps/current-QPC-at-rejection were NOT COLLECTED, so
   exact original ages cannot be reconstructed. These intervals are not frame age.
   The existing post-move source-age check explains the rejecting stage, but why
   the original run was slow enough is not established by these old logs.
6. A controlled real stale case verifies the unchanged guard: source frame
   `85061ee2-4231-455f-acaf-7bf023bceec8`, render=2310664574589, current
   QPC=2310675086518, frequency=10000000, age=1051.1929ms, limit=1000ms.
   It rejects Stale with actual target 0/0 and resource drain. This is deliberately
   aged test evidence, not reproduction of spontaneous recovery-scene slowness.

## Meaning and remaining uncertainty

TimeUnavailable is not interchangeable with Stale: the reproduced private
candidate lacks valid comparable time, while the source was young. Future raw
WGC time versus QPC is demonstrably an existing capture-boundary condition,
not a newly introduced time-validation branch in EXIT-01. Why the platform delivers
those particular future-valued facts is still unknown; no Windows/driver root
cause is claimed. Current 08C-02 passes do not prove the user's original
TimeUnavailable had exactly these unrecorded values, nor close its flakiness.

Stale remains an intentional safety refusal when a source ages during the compound
operation. Source receipt, matching, lease/verification and real smooth movement
consume its finite lifetime. We have timing evidence and deterministic delayed-
move coverage, not the original rejected source timestamp. It would be incorrect
to assert the old exact age, blame CLR shutdown, or declare all regression solved.

## Comparison design and actual results

Baseline is an isolated `git archive 95149f3` under ignored artifacts/exit-time-baseline.
Only the same diagnostic trace fields/emission and test/report/probe additions are
overlaid. No host admission/shutdown changes are copied into baseline production
logic. SDK remains 10.0.402; native dependencies are byte-identical. Separate
processes/owned windows are used sequentially. UI old/new outcomes are separate.

| Experiment / prediction | Current | 95149f3 | Interpretation |
| --- | --- | --- | --- |
| Same eight real recovery scenes: identify whether old implementation also fails | 8/8, twice; real UI 2/2, exit0 | 8/8, twice; completed UI 2/2 | Original recovery business rejection not reproduced in these comparisons |
| Original full command must retain actual UI exit | command0, UI0 | completed UI interaction but UI C0000602; aggregate command nonzero | Original CLR defect remains in old baseline; not a new failure of repaired host |
| Same 128 read-only captures: old producer can also deliver future raw time | 2 future/unknown, 0 input | 1 future/unknown, 0 input | Confirms pre-existing capture-time condition, not incident-rate equivalence |
| Complete visual sample adds real stale/time evidence | changed scene TimeUnavailable, command nonzero; viewer exits0 | all eight functional assertions pass; viewer CLR failure, command nonzero | Current changed-scene failure is preserved, not renamed SceneChanged or passed |
| Delay after movement with trusted test QPC | zero down, uncertain; exact rejection fields persist | same deterministic test passes | No relaxed age policy or hidden retry |

The current producer probe and current visual sample are diagnostic adjuncts, not
substitutes for the recovery scenario. Only a rejected source has a real action
frameId; read-only raw audit rows use capture indexes and do not mint fake run or
observation identities. Producer audit return0 means sampling/cleanup completed,
not that every candidate was actionable or a business workflow passed.

## Automated checks and initial failures

[related-tests.json](related-tests.json) lists final command exit codes. Debug and
Release WinUI builds are 0 warnings/0 errors. Observed-click (actual Lua/Gateway +
Fake clock/input), execution trace, trace review, recovery, lifecycle and UI core
Schema/state/hot-reload suites all returned0 in final independent runs. Baseline
Release observed-click tests with the same diagnostic overlay also returned0.
New assertions cover stale after movement, suppressed future raw metadata,
unknown/mismatched-clock time, source association, zero down/uncertain semantics,
checked-file reopening and frame/work counters. Fake evidence is not real WGC.

An earlier Debug lifecycle attempt failed `deferred stop rejection relied on
cancellation`; [its log](initial-lifecycle-test-failure.txt) is preserved. That
lifecycle test was not changed; final standalone reruns passed. This earlier
attempt is not retroactively counted as passed or silently fixed here. Full
default script suite was not rerun in this targeted investigation.

Initial current Native load failed 0x800711C7; Code Integrity event3077 named
jpeg62.dll. A baseline UI launch was also initially blocked. User/environment
assistant confirmed later new-process loading worked, no changed DLL hash or
security policy, no new3077. Subsequent baseline UI launch also worked and exposed
its original CLR assertion. The reason for transient trust recovery is unknown;
no permanent trust fix or security bypass is claimed. Initial load failure remains
in [its original log](initial-loader-failure.txt).

## Commands / retained outputs

Build matching configuration first. Existing full entry remains unchanged:

```powershell
dotnet build Rug.UI/Rug.UI.csproj -c Release -p:Platform=x64 -warnaserror
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Release -p:Platform=x64 -- --controlled-recovery-sample artifacts/recovery-time-review
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Release -p:Platform=x64 -- --capture-time-probe artifacts/capture-time-review
dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Release -p:Platform=x64 -- --observed-click-tests
```

The probe is fixed at128 sequential captures, one owned window, no input, no UI
host, explicit exact-window capture, bounded metadata only and actual capture
Stop/Dispose. It is not an alternative business acceptance command.

Reports: current-recovery-first-report.json, current-recovery-report.json,
baseline-recovery-report.json, current-visual-report.json,
baseline-visual-report.json and both producer reports. Historical user reports at
artifacts/review-exit01-release-08c02* remain unchanged. Original checked rejected
event lines retain their checksums in jsonl. No pixels, dump or binary is committed.

**Conclusion:** diagnostic work delivered; CLR fix still confirmed. A real
pre-existing capture-time condition is established, but overall business regression
is not declared all-green. Original spontaneous Stale timing and capture-side
handling of invalid future candidates remain open for scoped follow-up. No
threshold changes, automatic click retries, Hook-gap closure or Cancelled archive
classification change are included.
