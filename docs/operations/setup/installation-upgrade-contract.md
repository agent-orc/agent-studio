# Installation upgrade and rollback contract

AGT-2947 implements item I06 of the deployment story
([Dossier AGT-W63](../deployment-story/index.html#I06)). It applies
decision D6 option A: one versioned installation manifest, with one updater
for each placement. It covers journey steps A7, B7 and C8.

## One updater per placement

| Placement | Updater | Notes |
|---|---|---|
| Dev/stable checkout | Loopback update service on `127.0.0.1:5039` | Checkout-only. With `Placement` set to anything else, triggers and manual rollbacks are refused. |
| Installed Compose control plane | `deploy/release/agent-orchestrator/update-docker.sh` and `rollback-docker.sh` | Writes the installation manifest. |
| Installed systemd Task Server | `update.sh` and `rollback.sh` | Drain-before-switch contract. It does not write a manifest yet. |
| Runner host | Per-host drain, runner update and canary (B7) | Never the checkout updater. |

The pure decisions live in
`update-service/Installation/InstallationUpgradePolicy.cs`. The manifest
shape lives in `InstallationManifest.cs`. Until an explicit adapter exists,
the 5039 service does not manage an installed Compose stack.

## The manifest

The installed Compose authority has two files with separate owners. The D6
runtime manifest at `/etc/agent-orchestrator/installation-manifest.json` is the
authoritative record for desired and observed versions, upgrade phases, backup
verification and host compatibility. Only `update-docker.sh` and
`rollback-docker.sh` write it. The guided setup writes
`/etc/agent-orchestrator/installation.json` for the installation id, placement,
principal names and install retry phase. It does not contain secrets or replace
the runtime manifest. Setup must preserve the D6 file on retry, uninstall and
relocation; a setup release pin alone does not certify a running digest or a
completed upgrade. The same setup identity file is at `/etc/agent-host` for a
Linux runner host and at the selected Studio installation root for the other
placements. On a one-box installation, setup records `awaiting-acceptance`
after service health until identity, the provider canary and recovery are
verified; that phase does not mean the D6 runtime updater is waiting for a
canary. A relocation carries the setup identity file with the restored
authority and compares its id, principals and project origin against the frozen
source before the journey can finish. The runtime manifest remains with the
authority backup and is reconciled by its updater after restoration.

`update-docker.sh` and `rollback-docker.sh` render
`/etc/agent-orchestrator/installation-manifest.json` (schema version 1)
after every phase. Phase state is kept in `installation-upgrade.env` in the
same directory. Host state is kept in `installation-hosts.json`.

| Field | Meaning |
|---|---|
| `desired.version` | Target image tag of the current operation. |
| `observed` | What the running containers report: Task Server version, store schema, protocol range, runtime mode (from `/readyz`) and, for authority, engine and edge, the digest of the image each running container uses. A pulled but unused image never appears here. |
| `prior` | The retained release that a rollback may return to, with its store schema. |
| `backup` | The backup taken before the switch. It is recorded only after a verify-only restore has passed. |
| `progress` | `operation` (`update` or `rollback`), `targetVersion`, last completed `phase`, `outcome` and `reason`. |
| `hosts` | Runner hosts seen by the authority: `current`, `pending` (offline) or `incompatible`. |

Phases, in order: `started`, `drained`, `backed-up`, `switched`, `healthy`,
`compatible`, `resumed`, `canary-passed`.

Outcomes: `in-progress`, `succeeded`, `awaiting-canary`, `rolled-back`,
`awaiting-restore` and `failed`.

## Upgrade rules

1. **Retained prior release.** The images of the active release must exist
   locally, or the update refuses.
2. **Bounded drain.** The authority enters `Draining`. It polls
   `prepare-shutdown` for at most `DRAIN_TIMEOUT_SECONDS` (default 900). On
   timeout it returns to `Normal` and changes nothing.
3. **Verified backup.** The update creates a backup with
   `POST /api/v1/management/backups` and proves it with a verify-only
   restore. Without that proof no version changes.
4. **Switch and health.** The update pulls, then runs `up -d`. Every service
   must report `healthy`, and the running authority must report the target
   version.
5. **Protocol compatibility.** Every online runner host must be inside the
   candidate's protocol range. Hosts not seen within
   `HOST_ONLINE_WINDOW_SECONDS` (default 300) stay `pending` whatever their
   protocol. When a pending host returns, the Task Server's protocol
   admission (`426` outside the range) checks it before it is admitted.
6. **Resume and canary.** Admission returns to `Normal`, then the command in
   `AGENT_ORCHESTRATOR_CANARY_COMMAND` runs. Without a canary command the
   outcome is `awaiting-canary`, never `succeeded`. Run the detached canary,
   then rerun `update-docker.sh <same tag>` with the canary command set to
   finish.
7. **Candidate failure.** If the candidate's store schema is unchanged, the
   prior tag is restored and the outcome is `rolled-back`. If the candidate
   has already moved the store schema forward, no downgrade is attempted.
   The installation stays in `Maintenance` with outcome `awaiting-restore`,
   and the reason names the verified backup to restore with the prior
   release.
8. **Interruption.** Rerunning the same tag resumes from the recorded phase.
   Before the switch it drains again and skips a backup that is already
   verified. After the switch it re-verifies the candidate instead of
   switching twice. Another target is refused until the in-progress
   operation is finished.

## Rollback rules

`rollback-docker.sh [tag]` follows the same drain, verified-backup and
health steps. It refuses before changing anything in two cases:

- the active store schema is newer than the schema recorded for the
  target, or
- the target's schema was never recorded. This applies to any tag other
  than `CONTROL_PLANE_PREVIOUS_VERSION`.

In both cases, restore a verified backup taken with the target release
instead (see the [backup and restore](./control-plane-docker.md#backup-and-restore)
section).

## Evidence

`deploy/release/agent-orchestrator/tests/upgrade-rehearsal.sh` runs the
real scripts against a simulated Compose, Docker and Task Server
(`tests/fake-control-plane.sh`). It covers N-1 to N with active-run drain,
bounded drain, health without a canary, candidate boot failure, failure
after a schema change, interrupted upgrade, an offline host, an
incompatible protocol, a rollback rehearsal and a refused rollback.
`backend.Tests/InstallationUpgradePolicyTests.cs` runs that script. It
parses the manifest the script writes and checks the policy matrix directly.

The simulation is not host evidence. A published-image N-1 to N run on a
real installation is still needed, with the prerequisites named in the
installation guide.

## Not yet covered

- The systemd target (`update.sh`/`rollback.sh`) does not write a manifest
  yet.
- Runner-host tooling (B7) records host versions only through the
  authority's view (`hosts`). It has no per-host updater adapter yet.
- The 5039 service has no installed-Compose adapter. It refuses that
  placement by design.
