---
id: rug_checkpoint_probe
type: logic_node
inputs: [rug_ui_core]
outputs: [rug_ui_core]
tags: [plugin, checkpoint, sample]
---

# Controlled checkpoint example

Copy this folder into the host plugins root. Bind a controlled window whose center
is green, explicitly grant checkpoint/vision.capture/timer/input, and run via the host's
bound-task button. The exact shipped main.lua is embedded into the managed host
for trusted node registration: changing the installed code/version rejects the
node rather than trusting a new plugin-provided validator.

panel-ready/v1 means the green panel was observed and position=after-panel-ready
is confirmed; its completed action ID is panel-ready. The validator checks the
explicit state schema and a fresh owning-run capture. Normal on_init waits five
seconds (detector active), sends BEFORE B once via PostMessage, then saves.
Lua logs the result and sets its instance checkpoint_saved only after committed.
Stop/close the source host. Reopen, explicitly re-grant and rebind; query/select
Ready and confirm restore. A NEW green capture admits on_restore, which waits
five seconds and sends AFTER C once without running on_init/B again. Red rejects
with no business input. Returning control never auto-starts. v1.1.0 is exact-code
bound; old code checkpoints are incompatible. Use the self-contained
`--checkpoint-restore-manual` fixture; its independent target survives host restart.
See [startup contract and steps](../../Rug.UI.Core/CheckpointStartup.md).
