# Single-host Task Server operator runbook

Status: rehearsal topology verified on `agent-runner-01` on 2026-09-26.
Production cutover of Robert's Windows Studio is pending a maintenance window.
The evidence and release matrix are in `AGT-2737/results/rehearsal-report.md`.

This page applies to the approved single-host setup: Task Server and
Orchestrator Engine run in Docker beside Agent Runner. The edge publishes only
on host loopback. An outage or compromise of `agent-runner-01` affects both
the execution and control planes. Revisit the separate `task-server-01` host
when Dossier AGT-W49 decides the Operations Server.

## Daily health

Use the installed Compose source and protected environment file. Do not use
the rehearsal token files for production.

```bash
compose_dir=/opt/agent-orchestrator/compose
compose_env=/etc/agent-orchestrator/docker.env
docker compose --project-directory "$compose_dir" --env-file "$compose_env" ps
docker compose --project-directory "$compose_dir" --env-file "$compose_env" \
  exec -T task-server curl --fail --silent http://127.0.0.1:5071/healthz
docker compose --project-directory "$compose_dir" --env-file "$compose_env" \
  logs --tail 50 task-server orchestrator-engine backup edge
```

Expect Task Server, Engine, backup, and edge to be healthy. Check the
authenticated `/api/v1/management/status` response for `authorityReady`,
mode, schema version, and outbox backlog. Check the Runner service separately
with `systemctl status` and its configured `--health-check`. A loopback
`/healthz` response alone does not prove that the Runner can claim work.

Confirm the published edge remains bound to `127.0.0.1` and that Task Server
and Engine publish no host ports:

```bash
docker compose --project-directory "$compose_dir" --env-file "$compose_env" ps
ss -ltn
```

## Backup verification

The backup sidecar creates an integrity-checked SQLite snapshot and records a
SHA-256 in its log. Confirm each expected interval has a completed copy in
the configured **off-host mounted** destination. The AGT-2737 rehearsal used a
local staging directory and therefore did not prove off-host durability.

For one selected backup ID, compare the file SHA-256 with the log, then call
`POST /api/v1/management/restore` with `verifyOnly:true`. On a schedule,
restore a copy into an empty Task Server in `Maintenance` and compare the
returned `inventorySha256` with the signed import report. AGT-2737 restored a
464 MiB backup in 13.598 seconds with the same inventory hash. Never test a
restore by replacing the active store.

Legacy result and attachment bodies are pointer-only after import. Preserve
the frozen workspace source and its evidence overlay separately from the
SQLite backup. The rehearsal measured 27,307 ignored result files occupying
1,757,099,318 bytes. No attachment files were present. A Git-only install
loads the task and event records but cannot display those past result files;
an object store or separate evidence repository is needed for durable past
artifacts. The 44 files in orphan archive folders need an explicit retention
decision because the migration inventory cannot attach them to a task.

## Update

Use the published matching image tag for Task Server and Engine. Confirm a
current off-host backup and no active or `process-unknown` attempts. Then run:

```bash
deploy/release/agent-orchestrator/update-docker.sh <image-tag>
```

The script drains work, changes the Compose image tag, waits for health, and
returns to `Normal` only on success. Check status, Runner lease renewal,
backup completion, and the TLS edge after it exits. Record the image digests,
schema version, backup ID, and exact start and end times.

## Rollback and forward switch

For a bad release on the same host, use the version-matched rollback script
after confirming schema compatibility and a verified backup:

```bash
deploy/release/agent-orchestrator/rollback-docker.sh [image-tag]
```

For a host outage, fence the remote authority through the host control plane
before starting the Windows fallback. Loss of a heartbeat is not fencing.
Follow the [Windows fallback runbook](windows-fallback-runbook.md): restore the
newest verified backup to the version-matched Windows Task Server, keep it in
`Maintenance` until remote fencing is proved, switch the Studio connector,
and re-enable the preserved reverse tunnel only for the fallback route.
Moving forward again requires a new freeze, inventory, backup, and import;
never treat the rehearsal's fast API switch as automatic data resynchronization.

The AGT-2737 isolated two-store drill took 31.639 seconds to switch back and
0.101 seconds to switch forward, with one store in `Maintenance` at each
boundary. It did not switch the Windows connector or live Runner. The real D6
switch timing remains a maintenance-window gate.

## Before the Windows maintenance window

1. Reconcile the route inventory against the current source. The 2026-09-26
   guard found 420 frontend operations versus 408 in the saved inventory.
2. Provision and restore-test a real off-host backup destination plus a
   Windows fallback copy. Confirm equal image and schema versions.
3. Give the Task Server host a deploy key or fine-grained token with write
   access to the workspace repository, or move that repository into the
   `agent-orc` organisation. Test pull and push from the host.
4. Resolve the missing attempt-authority warning and the 44 orphan evidence
   files, or record explicit operator acceptance. Require zero active and
   `process-unknown` attempts at freeze.
5. Schedule Robert's window, then follow the production sequence and release
   gates in [the migration plan](../remote-task-server-local-studio.md). Keep
   the Windows writer frozen until the remote path is signed off.
