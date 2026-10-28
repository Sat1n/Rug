---
id: rug_runtime_update_target_sample
type: logic_node
inputs: [rug_ui_core]
outputs: []
tags: [sample, update, checkpoint]
---

# Fixed runtime-update target

These are the embedded v2 manifest/code for the trusted update_probe source in
`plugins/update_probe`. This directory is deliberately outside the plugin scan
root; copying it as another installation cannot inherit the source's grant/rule.
Only direct compatible continuation of panel-ready is registered by the host.
Use `--runtime-switch-manual` rather than overwriting an installed plugin by hand.
Its `update` command writes the exact embedded pair into its temporary source
installation. on_restore logs AFTER once, on_init faults if wrongly invoked.
See [switch contract](../../Rug.UI.Core/RuntimeSwitch.md) for verification/limits.
