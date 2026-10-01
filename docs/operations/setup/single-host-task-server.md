# Single-host Task Server: daily operation and cutover handoff

Status: AGT-2737 rehearsal completed 2026-09-26 on `agent-runner-01`.
Production cutover remains closed. This page is the operator handoff for the
approved single-host exception to the dedicated-VM plan in
[Remote Task Server with local Studio](../remote-task-server-local-studio.md).

The rehearsal used Docker Compose with the Task Server, Engine, backup sidecar,
and TLS edge on the Runner host. The edge bound only to loopback. The Windows
connector and live reverse tunnel were not changed; the connector attach
procedure below was added after gate 4 closed (AGT-2984). A host outage takes both
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

## Studio connector attach

The Windows Studio reaches this Task Server only through the hardened
connector profile of the same release (gate 4, closed by AGT-2984). The
connector is `OrchestratorApi.exe` with `OrchestratorApi__Profile=connector`.
It listens only on `http://[::1]:5031`, accepts browser requests only from
`http://localhost:4011` and `http://[::1]:4011`, and injects the Studio
credential after the browser boundary. The legacy `agent-studio-bff.exe`
forwarder must not attach a remote upstream, and `switch-upstream.ps1` refuses
it. On the single host the connector reaches the loopback TLS edge through
the existing supervised SSH link; WireGuard is not required.

1. **Forward the edge.** On Windows, keep an SSH local forward from a loopback
   port to the edge on `agent-runner-01`, for example
   `ssh -N -L 127.0.0.1:18443:127.0.0.1:<edge-port> agent-runner-01`, under
   the same supervision as the existing link. The connector validates the
   edge certificate by name and pinned SHA-256. Map the certificate's host
   name to `127.0.0.1` in the Windows hosts file, and use that name in the
   base URL.
2. **Issue or rotate the Studio credential.** List the principals with
   `GET /api/v1/management/principals` and pick the one of kind `studio`.
   Then call `POST /api/v1/management/principals/<studio-principal>/rotate`
   with `{"overlapSeconds":300}`, as in
   [Rotate and revoke principals](task-server.md#rotate-and-revoke-principals).
   Redirect the one-time response to a protected file.
3. **Store it in Credential Manager** as the Windows user who runs Studio:

   ```powershell
   .\deploy\windows\studio-connector\set-studio-credential.ps1 `
       -FromFile C:\Users\<user>\studio-rotation.json -RemoveSourceFile
   ```

   Without `-FromFile`, the script prompts for the credential as a
   SecureString. On a Linux connector, write the credential to the file named
   by `Connector:CredentialFile` instead (default
   `~/.config/agent-studio/studio-connector.credential`), for example
   `install -m 600 /dev/null <file>` and then write the value. A group- or
   world-readable file, or a symbolic link, is refused.
4. **Configure the connector.** `C:\ProgramData\AgentOrchestrator\studio-connector.env`
   holds no secret; the connector refuses to boot if a credential key is
   present:

   ```text
   OrchestratorApi__Profile=connector
   Connector__Upstream__Mode=remote
   Connector__Upstream__BaseUrl=https://<edge-certificate-name>:18443
   Connector__Upstream__TlsCertificateSha256=<edge certificate SHA-256>
   Connector__Upstream__Generation=1
   Connector__Upstream__MaskedName=single-host-task-server
   Connector__CredentialTarget=AgentStudio/TaskServer/studio-robert-windows
   ```

5. **Register and start the connector** from a published OrchestratorApi
   build of the same release. The task runs in the Studio user's interactive
   session because an S4U session cannot read that user's Credential Manager:

   ```powershell
   .\deploy\windows\studio-connector\register-studio-connector.ps1 `
       -InstallRoot C:\AgentOrchestrator\connector\<version> `
       -ExecutableName OrchestratorApi.exe
   ```

6. **Prove the attach.** `GET http://[::1]:5031/readyz` must report
   `"status":"ready"` with the expected `serverVersion`, `protocol`, and
   `hubProtocol`. The connector also logs the attach result at startup. On
   refusal, `failureCode` and `failureReason` name the cause:

   | `failureCode` | Operator action |
   |---|---|
   | `credential-unavailable`, `credential-empty`, `credential-file-insecure` | Store the credential (step 3); fix the file mode on Linux. |
   | `credential-rejected` | The Task Server returned 401. Store the current credential; it is re-read without a restart. |
   | `credential-not-studio` | The stored credential is a Runner, Engine, or management principal. Store the Studio principal. |
   | `protocol-incompatible`, `hub-protocol-incompatible`, `hub-path-mismatch`, `attach-unsupported` | Connector and Task Server releases differ. Install the matching release on both sides. |
   | `upstream-not-ready`, `upstream-unavailable` | Check the SSH forward, the edge, and the Task Server `/readyz`. |

7. **Prove the browser boundary.** Open Studio at `http://localhost:4011`.
   Require a board read and one reversible mutation to succeed, and a
   SignalR reconnect. Then revoke the previous Studio credential once the
   overlap ends.

Credential rotation later repeats steps 2, 3, and 6 without restarting the
connector. To move between the remote and the Windows fallback upstream, use
`deploy/windows/fallback/switch-upstream.ps1`. For a connector-profile env
file it rewrites the `Connector__Upstream__*` keys and the credential target,
bumps the generation, and gates on the connector's `/readyz` attach result.
The fallback Task Server's Studio credential lives under its own target
(default `AgentStudio/TaskServer/studio-robert-windows/local`).

The connector negative-test matrix (absent and invalid bearer, cross-origin,
missing Origin, CSRF, replayed token, API and hub protocol mismatch) is part
of the deployment regression scenario evidence. Run
`scripts/scenario.sh --target inproc --level full --report-dir <dir>` on the
release candidate and attach `connector-negative-matrix.md` to the cutover
report.

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
| 60 to 70 min | Verify remote restore readiness, start Engine, switch the Studio connector to the remote loopback origin (see [Studio connector attach](#studio-connector-attach); require `/readyz` ready), then switch Runner registration. Disable the former Windows-origin tunnel only after the new supervised link is healthy. Keep admission closed. |
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
write credential, the supervised Windows connector path (attached as in
[Studio connector attach](#studio-connector-attach)), a genuinely off-host
backup with Windows restore proof, a durable home for past evidence, and a
decision for the 44 orphan images. The host outage limitation remains accepted
for the single-operator setup and is revisited with Dossier AGT-W49.
