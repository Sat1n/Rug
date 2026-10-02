---
id: rug_hook_controlled_stages
type: logic_node
inputs: [rug_hook_comparison, rug_script_tests]
outputs: []
tags: [diagnostics, hooks, isolation]
---

# Controlled managed-component stages

This is diagnosis, not Raw Input or takeover acceptance. The native Human probe
remains the fixed sender/target/observer; the second managed process adds exactly
one selected cumulative stage. It never sends input, registers Raw Input,
automatically reinstalls a Hook, or alters production emitters.

| Stage | Managed Rug.Core load | Managed target/message thread | Keyboard Hook | Mouse Hook |
|---|---|---|---|---|
| load | Yes | No | No | No |
| target | Yes | Yes | No | No |
| keyboard | Yes | Yes | Yes | No |
| both | Yes | Yes | Yes | Yes |

Use **fresh A and B processes for every stage**, in this order:

1. Terminal A: `./tests/run_native_input_probe.ps1 -Mode Human`. Leave it waiting
   at HUMAN BEFORE; its native hooks are already installed.
2. Terminal B (initial stage load):

   ```powershell
   dotnet run --project tests/Rug.UI.Core.Script.Tests/Rug.UI.Core.Script.Tests.csproj -c Debug -p:Platform=x64 -- --hook-isolation-stage load
   ```

   Wait for ISOLATION READY and CORE LOADED. Replace load with target, keyboard,
   then both in subsequent fresh sessions. Do not switch stages in a live process.
3. Perform A's HUMAN BEFORE using the **native** white window and another app.
   In B enter baseline; in A enter next. During the five-second countdown activate
   the **native** window, then release keys/mouse and keep hands off AUTO.
   The managed white window in target/keyboard/both is not the input target.
4. After A finishes all four software rounds, enter snapshot in B. Complete A's
   HUMAN AFTER and done; finally enter snapshot and done in B. Keep both outputs.
5. Keep elevation and external applications unchanged across stages. Preserve
   module fingerprints, native tagged F8/B down/up, target delivery, per-round
   interference verdict, and both thread cleanup statuses. Verify normal input
   and shortcuts yourself; counters alone do not certify noninterference.

Managed READY means the selected components have started, not that Hook delivery
has passed. Absent Hook counters in load/target are expected. Keyboard stage has
no managed mouse Hook, but native mouse evidence remains available. Installation
failures or post-start thread faults fail visibly. EOF without done is incomplete.
Snapshots print aggregate counts only; baseline/terminal input is not an AUTO
interference verdict. No key sequence or mouse trajectory is persisted.

If load already loses native keyboard evidence, focus on process/DLL/environment
differences; if target is the first change, examine target/thread interactions;
if keyboard is the first change, examine Hook registration/chain interaction;
if only both changes it, examine interaction between registrations. These are
investigation branches, not causal proof. A both-stage pass does not reproduce
the original failing **managed sender** case: next control would keep target and
observers fixed and compare caller thread/emitter context, only when authorized.
Do not label absent native Hook evidence as a Raw Input parser failure.

Automated tests validate stage mapping, invalid stage rejection before native
loading, real selective Hook startup, message barriers and cleanup. Native Human
comparison in these stages remains NOT RUN by the agent; user operation required.
