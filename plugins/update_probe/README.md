# Controlled update-freeze sample

Trusted host registers the embedded exact `main.lua` hash for `panel-ready`.
Each Running tick logs BEFORE, validates a newly captured green target via WGC,
publishes a checkpoint, then logs AFTER. Its timer/capture/checkpoint permissions
must be explicitly granted; it sends no input and has no restore entry.

Append a comment to installed `main.lua` while the original run is active, then
click Prepare runtime update. At **已冻结，尚未切换**, logs show BEFORE without its AFTER.
Cancel releases that same checkpoint call; AFTER follows without INIT replay.
Prepare again, then Stop: no held AFTER follows. The installed edit remains
unapplied and cannot inherit the embedded node on a later new-code startup.
Use the self-contained `--runtime-update-ui` harness described in
[host contract](../../Rug.UI.Core/RuntimeUpdates.md) for isolation and cleanup.
