# Single-host Task Server: daily operation and cutover handoff

Status: AGT-2737 rehearsal completed 2026-09-26 on `agent-runner-01`.
Production cutover remains closed. This page is the operator handoff for the
approved single-host exception to the dedicated-VM plan in
[Remote Task Server with local Studio](../remote-task-server-local-studio.md).

The rehearsal used Docker Compose with the Task Server, Engine, backup sidecar,
and TLS edge on the Runner host. The edge bound only to loopback. The Windows
connector and live reverse tunnel were not changed. A host outage takes both
Runner and control plane down. Revisit separate hosts with Dossier AGT-W49.

## Daily health

Use the installed Compose project and env file. The exact paths are recorded
in the installation handoff; the rehearsal override and credentials are not a
production installation. Require every service to be healthy or running, then
check `/readyz` through the private edge and inspect Task Server, Engine, and
backup logs for errors:

```bash
docker compose --project-directory <compose-directory> --env-file <env-file> ps
docker compose --project-directory <compose-directory> --env-file <env-file> logs --tail 100 task-server orchestrator-engine backup edge
```

Use a pinned TLS certificate and a scoped bearer for authenticated API checks.
Check that neither Task Server nor Engine publishes a host port and that the
edge binds to the intended loopback address. Check Runner registration, lease
renewal, and an actual reported task after Studio disconnects. The isolated
Linux BFF detach is rehearsal evidence, not proof of Windows sleep.

## Backup verification

The sidecar calls Task Server's consistent SQLite backup command every five
minutes and copies it to the configured destination. A directory on the same
host is only staging, even when Compose calls it `offhost`. Daily, confirm a
new backup ID, SHA-256, copy completion, and age within the recovery-point
budget. Weekly, verify a full backup and restore it into an **empty isolated**
store. Compare the migration inventory SHA-256 and project, state, event, and
artifact counts. Keep the frozen source and ignored `results/` overlay until
artifact pointers have a durable home; a database backup does not copy their
bodies. The rehearsal overlay was 1,757,099,318 bytes across 27,307 files.

## Update

Follow [the Docker update script](control-plane-docker.md#update-and-rollback):
enter `Draining`, wait for `prepare-shutdown` to say `safeToStop`, capture and
verify a backup, enter `Maintenance`, change to the exact published image tag,
and require health and version compatibility before returning to `Normal`.
Keep the previous tag and version-matched Windows fallback package. Rerun the
deployment scenario and authentication checks on the release candidate.

## Rollback and forward recovery

Fence the current writer before opening another. If remote mutations exist,
restore the latest verified remote backup on Windows; never start the stale
pre-cutover Windows store. Verify the backup ID, SHA-256, recovery point, and
count parity. Use the D6
[Windows switch drill](windows-fallback-runbook.md#the-switch-drill) to change
the connector and Runner link, time each phase, and prove the fenced authority
returns a write conflict. The isolated Linux two-store drill took 27.676
seconds back and 21.195 seconds forward after each verified backup was copied;
it did not exercise the Windows connector, reverse tunnel, or live Runner.
Returning to the remote host after
accepted local mutations requires a new freeze, inventory, backup, and import.

## Maintenance-window checklist, dated 2026-09-26

Before scheduling: reconcile the 13-operation route inventory delta against
AGT-2835; make the exact release and Windows package match; provide a scoped
workspace-repository deploy key or fine-grained write token and verify pull
and push; choose durable storage for past evidence; decide the 44 orphan
images; prove a genuinely off-host and Windows restore; and establish the
supervised Windows connector path. During the window, resolve all active and
`process-unknown` attempts, freeze and push Windows workspace Git, inventory
and bundle it, import the final copy, compare every count and hash, switch the
connector and Runner, disable the old tunnel, prove the Windows store refuses
writes, perform the timed D6 rollback and forward drill, and obtain Robert's
sign-off. Keep the release gates closed until their production evidence exists.
