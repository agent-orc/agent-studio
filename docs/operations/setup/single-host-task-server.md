# Single-host Task Server operator runbook

Status: interim rehearsal procedure, 2026-09-25. Production authority remains
on Robert's Windows device until a separately scheduled maintenance window
passes every release gate in the [cutover plan](../remote-task-server-local-studio.md).
The [AGT-2737 evidence report](/api/tasks/AGT-2737/results/report.md) records
which gates have actually been exercised.

## Topology and limits

`agent-runner-01` hosts the Task Server, Orchestrator Engine, backup sidecar,
and TLS edge in Docker. The edge publishes TCP 443 on `127.0.0.1` only. The
Task Server and Engine publish no host port. The Runner and control plane share
one machine, so a host outage interrupts both. This is an accepted interim
availability and security limitation for the single-operator setup. Revisit a
dedicated control-plane host when Operations Server dossier AGT-W49 is decided.

The `/api/v1/studio` routes require a Studio principal even where their
`tasks.read` scope is also granted to Runners. Include a Runner bearer against
the Studio board in the daily authorization spot-check; it must return 403.

The existing `RunnerLinks` SSH command contains only `-R` forwards from
Windows to the Runner. Those forwards let the Runner reach the **Windows** Task
Server at `127.0.0.1:15031`; they cannot let Windows Studio reach a Task Server
on the Runner host. A remote Studio connector needs a loopback `-L` forward
from Windows to the Runner's loopback TLS edge, with certificate verification
and independent supervision. `LinkSupervisor` currently judges the `-R` link by
legacy Runner heartbeats, so it cannot supervise that remote connector path
after the Runner moves to the remote Task Server. Configure and prove the new
direction before a Windows connector flip. Preserve the `-R` configuration for
fallback; do not run both task authorities against the same workspace.

## Daily health

Run from the deployed Compose directory using its protected environment file.
For an isolated rehearsal, use that rehearsal's project name and environment
file. The commands below assume the installed location:

```bash
compose_dir=/opt/agent-orchestrator/compose
env_file=/etc/agent-orchestrator/docker.env
docker compose --project-directory "$compose_dir" --env-file "$env_file" ps
docker compose --project-directory "$compose_dir" --env-file "$env_file" \
  exec -T task-server curl --fail --silent http://127.0.0.1:5071/readyz
docker compose --project-directory "$compose_dir" --env-file "$env_file" \
  logs --tail 50 task-server orchestrator-engine backup edge
```

Require Task Server, Engine, backup, and edge to be healthy. Look for Engine
claim-loop errors even when its process health is green. Confirm that the edge
is bound only to `127.0.0.1:443` and no Task Server API port is published. On
the Runner, verify its configured Task Server URL and one successful heartbeat
through that URL. Studio connector health is checked separately on Windows.

## Backup verification

The backup sidecar uses the Task Server's SQLite backup command and writes an
audit row in the same store. Its store volume must therefore be writable. It
copies the backup archive to the configured destination and compares the
copied file's SHA-256 with the CLI result. The destination must be a mounted
off-host filesystem writable by container UID 10001. A local directory is
useful for a rehearsal but does not pass the off-host release gate.

```bash
docker compose --project-directory "$compose_dir" --env-file "$env_file" \
  logs --tail 50 backup
mountpoint /mnt/agent-orchestrator-offhost-backup
```

Require a recent `Off-host copy complete.` after a backup result with a SHA-256.
At the agreed restore interval, verify a full backup set with `task-server
backup verify-full <backup-id> --json`, then restore a copy into an empty
separate store in Maintenance. Compare task counts, migration inventory hash,
and the signed migration report before declaring that restore usable. The
[Task Server backup procedure](task-server.md#backup-and-restore-rehearsal)
contains the API and full-backup request contracts.

## Update

Record the running image tags, image digests, Task Server version and schema,
backup ID, and current task attempt inventory. Drain admission and resolve all
active or `process-unknown` attempts before switching. Use the versioned
`deploy/release/agent-orchestrator/update-docker.sh` procedure with the exact
published image tag. Its operator account requires the privileges documented
in the [Docker control-plane runbook](control-plane-docker.md#update-and-rollback).
After restart, repeat daily health, the 401/403 authorization matrix, a Runner
claim, and backup verification. Do not treat container health alone as a
successful Engine decision path.

## Rollback and failback

The [Windows fallback runbook](windows-fallback-runbook.md) owns the D6 switch.
Fence the remote authority before starting the Windows Task Server. If remote
mutations were accepted, restore a verified remote backup on Windows; the
pre-cutover Windows store is stale. Enter Maintenance, verify counts and
integrity, and only then switch the connector to Windows. Re-enable the old
`-R` tunnel for the Runner after the remote server is fenced. Record the actual
recovery point, both switch durations, and proof that only one writer accepted
mutations. Failback to the remote server requires a fresh freeze, inventory,
backup, and import. There is no automatic two-way synchronization.

## Maintenance-window checklist

1. Obtain a frozen Windows workspace copy with `.git`, `.metadata`, task
   results, and all hidden files. Compare the Windows and Linux file manifests,
   `git fsck --full`, bundle SHA-256, and migration inventories.
2. Resolve all migration warnings and route-inventory drift. Prove the Studio
   connector's Windows-to-Runner `-L` path and the Runner's direct Task Server
   path. Confirm certificate and token rotation, plus no public API listener.
3. Freeze Windows writers with zero active or unknown attempts. Import the
   exact frozen inventory in Maintenance and verify every before/after count,
   integrity hash, and signed report.
4. Start remote Engine and Runner admission. Switch Studio connector only after
   the remote origin passes auth, board, mutation, event replay, and chat
   checks. Keep the `-R` fallback disabled during remote authority.
5. Run a real detached-Studio task through review and Engine decision. Execute
   the D6 switch back and forward with production scripts and time both ways
   below 15 minutes. Record sole-writer proof and Robert's sign-off.

Until step 5 passes, the Windows Task Server and workspace remain authoritative.
