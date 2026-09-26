# Single-host Task Server operations

Status: rehearsal completed on 2026-09-26 for AGT-2737. Production cutover
remains pending Robert's maintenance window and the open release gates in
[the cutover plan](../remote-task-server-local-studio.md#release-gates).

The approved interim topology puts the Task Server, Orchestrator Engine, backup
sidecar, and loopback TLS edge in Docker on `agent-runner-01`. The Runner shares
that host. A host outage therefore stops both task authority and execution.
Revisit the dedicated `task-server-01` design when Dossier AGT-W49 is decided.
The rehearsal did not switch Robert's Studio, local Task Server, workspace, or
reverse tunnel. Its signed report is in the AGT-2737 task results and in the
rehearsal store at `migration-reports/rehearsal-report.md`.

## Daily health

Use the installed Compose project directory and its host-owned environment
file. Do not copy credentials into shell arguments or logs.

```bash
docker compose --project-directory /opt/agent-orchestrator/compose \
  --env-file /etc/agent-orchestrator/docker.env ps
docker compose --project-directory /opt/agent-orchestrator/compose \
  --env-file /etc/agent-orchestrator/docker.env logs --tail 100 task-server orchestrator-engine backup edge
```

Require Task Server and edge health checks to pass, Engine and backup to be
running, the `volume-init` one-shot service to have exited successfully,
`/readyz` through the loopback TLS edge, recent Engine settlements,
and a fresh backup with a verified SHA-256. Inspect the host's published ports:
Task Server and Engine must publish
none, and the edge must bind `127.0.0.1` only. Test the Windows-to-host forward
and connector separately before depending on it. The earlier Windows-to-Runner
reverse link does not itself provide this forward path. The active forward must
be supervised and must verify the edge certificate.

Check free space on the host and backup destination. The 2026-09-25 frozen
workspace contained a 1,757,099,318-byte ignored `results/` overlay in addition
to Git. Imported artifact bodies are references to the frozen source. Retain
that source until evidence has a verified durable home outside Git.

## Backup verification

Follow [Task Server backup and restore](task-server.md#backup-and-restore-rehearsal)
for the management API and full backup set commands. Verify the newest backup
by ID and SHA-256 without changing the active store; periodically restore into
an empty isolated store and compare the inventory SHA-256 and migration report.
Copy to a genuinely off-host destination and verify a Windows restore before
calling either release gate passed. The rehearsal's local staging copy is not
off-host protection.

Keep the Windows fallback on the exact same release and schema range. The
fallback is in `Maintenance` while the remote Task Server is authoritative.
Never start a stale Windows store after remote mutations. Preserve the latest
verified backup and its recovery point for the [D6 switch](windows-fallback-runbook.md#the-switch-drill).

## Update

Use the versioned release and the
[Docker update script](control-plane-docker.md#update-and-rollback). Drain
admission, resolve active and `process-unknown` attempts, take and verify a
backup, then update. Check image and Windows fallback package identities,
schema compatibility, health, authentication, a Runner claim, and Engine
settlement before reopening admission. Record the exact image digest and
backup ID in the operator log. Do not use an uncommitted rehearsal image for a
production update.

The Task Server host needs authenticated pull **and push** access to the
workspace repository for production. Supply a scoped deploy key or fine-grained
write token, or move the repository to `agent-orc`. Keep that credential outside
the Compose file and verify both directions before the maintenance window.

## Rollback and forward recovery

Follow [the cutover plan's two data cases](../remote-task-server-local-studio.md#rollback-in-less-than-15-minutes)
and the [Windows fallback switch runbook](windows-fallback-runbook.md#the-switch-drill).
Close admission and fence the remote writer before allowing the Windows writer
to leave `Maintenance`; a missing heartbeat is not fencing. After any remote
mutation, restore the newest verified remote backup on Windows instead of
opening the pre-cutover store. Record its recovery point and compare counts and
inventory SHA-256. Switch the Studio connector, then the Runner link, and prove
the remote writer rejects a mutation. Time every phase.

Forward recovery is a new freeze, inventory, backup, and sole-writer handoff.
The isolated rehearsal switched between two Linux stores in 31.639 seconds
back and 0.101 seconds forward; those timings do not certify the Windows D6
switch or a production recovery point. Keep the reverse tunnel configuration
documented and disabled in remote mode; enable it only during a fenced
fallback.
