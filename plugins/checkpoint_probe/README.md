---
id: rug_checkpoint_probe
type: logic_node
inputs: [rug_ui_core]
outputs: [rug_ui_core]
tags: [plugin, checkpoint, sample]
---

# Controlled checkpoint example

Copy this folder into the host plugins root. Bind a controlled window whose center
is green, explicitly grant checkpoint/vision.capture/timer, and run via the host's
bound-task button. The exact shipped main.lua is embedded into the managed host
for trusted node registration: changing the installed code/version rejects the
node rather than trusting a new plugin-provided validator.

panel-ready/v1 means the green panel was observed and position=after-panel-ready
is confirmed; its completed action ID is panel-ready. The validator checks the
explicit state schema and a fresh owning-run capture; no input action is emitted.
Lua logs the result and sets its instance checkpoint_saved only after committed.
This example saves/reads business data, not a new-instance restore demonstration.
See [contract and limitations](../../Rug.UI.Core/Checkpoints.md).
