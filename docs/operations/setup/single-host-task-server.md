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

The sidecar calls the serving Task Server's authenticated backup API every five
minutes. The server creates the snapshot under its write gate; the sidecar has
no live store mount and verifies the snapshot hash before and after copying.
A directory on the same
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

## Production cutover window

The operator agent starts in the first 22:00 to 02:00 Europe/Berlin night
window after the rehearsal report is accepted. Acceptance is the only
authorization gate. The agent records UTC and local timestamps for every
check and step in the signed cutover report. It does not advance past a failed
check. The single-host origin is the loopback edge reached through the
existing supervised reverse link; use that origin in the D6 switch scripts
instead of the dedicated-VM WireGuard examples. Keep the old link configuration
available but disabled after the final switch.

### Automated entry checks (budget 15 minutes)

Run these checks against both the Windows authority and the rehearsal remote
authority before the first mutation. Capture the response bodies and exit codes.

1. Read `/readyz` and authenticated `GET /api/v1/management/status` through the
   connector and through the remote loopback edge. Require readiness, matching
   release and protocol versions, and the expected server IDs. Check the
   supervised link reports connected and a second authenticated `/readyz`
   request succeeds after reconnect. A successful local HTTP request alone
   does not prove the Windows connector path.
2. Enter `Draining` on the Windows authority, call
   `POST /api/v1/management/prepare-shutdown` and require `safeToStop:true`
   and `unresolvedAttempts:0`. Recheck immediately before freeze. Any active
   or `process-unknown` run blocks the window; resolve it through the normal
   audited API, then repeat the check. If the window aborts before freeze,
   restore `Normal` on Windows.
3. Verify the newest complete off-host backup by ID, SHA-256 and integrity
   check; require `createdAt` within 24 hours. Verify the Windows fallback has
   a locally available copy of that same ID. Check the destination is a real
   off-host mount, not a directory on `agent-runner-01`. Record the recovery
   point and run a restore into an empty isolated store before the window.
4. Verify the exact Windows fallback package and remote images match, the
   workspace repository credential can pull and push, and disk space covers
   the measured frozen copy plus three backup generations. Require all
   rehearsal release gates and the 13-operation AGT-2835 route delta resolved.

### Ordered cutover (budget 90 minutes)

| Budget | Agent action and required evidence |
|---:|---|
| 0 to 15 min | First run the D6 rollback and forward drill against the isolated rehearsal stores. Record both elapsed times below 15 minutes, write conflicts on the fenced side, and a healthy connector and Runner after forward switch. Abort if either direction fails. |
| 15 to 25 min | Put Windows Task Server into `Maintenance` after the drain check. Stop scheduled mutations. Prove a write is refused, then make the final workspace commit and push. Record commit SHA, clean Git status, and remote ref SHA. |
| 25 to 45 min | Produce final inventory, archive, Git bundle, hashes and file manifest. Transfer the frozen copy, including the measured evidence overlay, to `agent-runner-01`. Verify `git fsck`, refs, manifest, case collisions, and per-project and per-state counts. |
| 45 to 60 min | Start the remote Task Server in `Maintenance`; inventory the staged source and require exact count and hash parity. Import once with the frozen migration ID. Save the pre-import backup ID and signed import report. Abort on every unexplained warning or mismatch. |
| 60 to 70 min | Verify remote restore readiness, start Engine, switch the Studio connector to the remote loopback origin, then switch Runner registration. Disable the former Windows-origin tunnel only after the new supervised link is healthy. Keep admission closed. |
| 70 to 85 min | Open remote admission. Check authenticated board read, reversible task mutation, SignalR reconnect, claim and renewal, artifact upload, completion, Engine post-steps, and Studio detach and reconnect. Require the Windows store to refuse a write throughout. |
| 85 to 90 min | Sign the gate matrix and cutover report, record sole-writer evidence and recovery point, and leave the Windows fallback package and rollback path warm for the whole window. |

Abort before changing authority if any entry check fails, the rehearsal drill
exceeds 15 minutes, the final Git push is absent, or an inventory hash or count
differs. After changing authority, abort on an unhealthy link, lost Runner
lease, failed Engine post-step, a writable Windows store, failed backup
verification, or a gate without evidence. If a step exceeds its budget, stop
admission and use the rollback path; do not run beyond 02:00 local time.

### Rollback inside the window (hard limit 15 minutes)

Start the timer when admission is stopped. Within 2 minutes fence the remote
writer using `Maintenance` and verify it refuses writes; if the host is
unreachable, use host-level fencing before opening Windows. By minute 5 select
and verify the latest locally available remote backup by ID, SHA-256 and
recovery point. By minute 8 restore it to the version-matched Windows Task
Server in `Maintenance`, then enter `Normal` and prove a write succeeds. By
minute 10 switch the Studio connector atomically to Windows. By minute 13
restore the preserved Runner reverse link and registration. By minute 15
verify a Runner claim or renewal, Studio events, and the remote write refusal;
publish the measured elapsed time. Never start the stale pre-cutover Windows
store after a remote mutation. A later forward switch after accepted Windows
mutations requires a new freeze, inventory, backup, and import.

Before scheduling, the operator agent must supply the workspace repository
write credential, the supervised Windows connector path, a genuinely off-host
backup with Windows restore proof, a durable home for past evidence, and a
decision for the 44 orphan images. The host outage limitation remains accepted
for the single-operator setup and is revisited with Dossier AGT-W49.
