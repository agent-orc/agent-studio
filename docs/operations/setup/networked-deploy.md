# Networked installation deploy

`deploy/networked/deploy.sh` drives one server-first release window. It builds
API, web, engine, agent-host, and gate-worker artifacts from one full Git commit
in parallel. The repository-owned [`host_adapter.py`](../../../deploy/networked/host_adapter.py)
performs the artifact build, SSH transfer, host switch, health checks, and
rollback. It sends [`remote_host.py`](../../../deploy/networked/remote_host.py)
over SSH for each host action; no adapter is installed on the hosts. This
implements the OP-6 Dossier's server-first, no-blocker-override option.

## Private manifest

Copy [`manifest.example.json`](../../../deploy/networked/manifest.example.json)
to a private, owner-only path outside the checkout. Fill in the operator
checkout, artifact directory, image repositories, host SSH targets, paths,
Compose project names and service names, and the site-specific command arrays. Use
the names from the running containers' `com.docker.compose.project` labels for
`composeProject` and `engineComposeProject`. Do not copy a guessed project name
from a Compose directory name. The snapshot queries Docker by those labels, so
it can capture a stack started with the previous Compose configuration before
the new gate variables are written. No host names,
addresses, accounts, credentials, or private paths belong in the repository.
The engine has its own manifest role and SSH target; it may share a physical
host with a coding runner while remaining a separate release step.
SSH uses batch mode and strict host-key checking. Registry and SSH credentials
remain in the operator's normal local files; do not put them in the manifest
or command arguments. The deploy journal records a SHA-256 of the manifest and
refuses a changed manifest during deploy or rollback.

The manifest contains command arrays already wired to the repository adapter.
Keep that wiring. `repository` must contain the requested commit; the adapter
builds from `git archive <commit>`, never from a dirty working tree. Docker
Buildx pushes API, web, and engine images and returns their registry digests.
The agent-host and gate-worker releases are immutable tar archives under
`artifactDirectory/<commit>` with checked SHA-256 values. The gate archive
contains `OrchestratorApi.dll`; the coding archive is staged into the existing
`agent-runner-deploy` incoming directory, then the installed helper performs
its guarded promotion and service restart. Buildx, Docker registry access,
`dotnet`, `git`, `ssh`, `scp`, and `studio-op` must be available where the
command runs. Remote hosts need Python 3, plus their existing Docker, Compose,
service, and release tools.
The coding SSH account must own the helper's incoming directory and have its
documented sudo grant for `agent-runner-deploy`; other SSH accounts need write
access to the configured environment and release paths and permission to run
their Docker or Compose commands.
An interrupted release extraction leaves no completed gate or review release;
the next attempt rebuilds it. An incomplete coding incoming directory is
cleared on retry after the transferred archive digest is checked.
An interrupted agent-host or gate-worker archive build leaves no completed
checksum. The next build replaces that incomplete archive from the same commit.

Site-specific commands are **inline argument arrays**, not paths to operator
scripts:

- Server `admissionDrainCommand` and `admissionResumeCommand` must close and
  reopen work admission through the installation's approved control. They
  return success only after the state is observed.
- Coding `modelProbes` lists each configured CLI/model acceptance command and
  an output marker. Every command must succeed and emit its marker before
  admission resumes. Execute the probes under the account that owns that
  model's credential.
- Review `reviewDrainCommand` closes new review admission before recreation.
  `reviewIdleCommand` returns `{"idle":true}` only when every listed review
  service has no active work. The adapter runs it once before recreation and
  again after draining, immediately before changing the image. A busy result
  fails the deploy and leaves the review image unchanged.

The server Compose file reads `STUDIO_API_DIGEST`, `STUDIO_WEB_DIGEST`,
`STUDIO_GATE_SSH_HOST`, and `STUDIO_GATE_WORKER_PATH` from its private
environment file. The gate values become `RemoteGate__SshHost` and
`RemoteGate__WorkerPath`. A running API from the previous Compose configuration
can have neither value. The snapshot records each missing value as null, and
rollback removes its environment-file entry. Compose then supplies an empty
value, matching the previous effective configuration. The automated switch
requires a gate SSH host and absolute worker path. The engine Compose file reads the digest
pinned `ORCHESTRATOR_ENGINE_IMAGE_REF`. The review Compose file must read
`REVIEW_IMAGE_REF` for every service named in `reviewServices`. Its pinned
`reviewBaseImage` must contain the coding CLIs and runner runtime. The adapter
copies the checked agent-host publish output into the review image, records a
plan digest from the base image, release SHA, and commit, and verifies that
digest on the running containers.

## Run

```sh
commit=$(git rev-parse develop)
manifest=/private/networked-deploy.json
journal=/private/deploy-$(date -u +%Y%m%dT%H%M%SZ).json
bash deploy/networked/deploy.sh deploy "$commit" --manifest "$manifest" --journal "$journal" --dry-run
bash deploy/networked/deploy.sh deploy "$commit" --manifest "$manifest" --journal "$journal"
```

Dry run validates the manifest and prints the plan without invoking a build,
SSH, API, or journal command. The live run refuses an existing journal. It
creates the first snapshot in that new journal from the containers already
running under the configured Compose project labels. No earlier deploy journal
or gate setting is needed. Missing or empty gate values in the old API are
recorded as null for rollback.
The live run checks OP-6 preflight, formed batches, and running gates before
the plan, after draining, and immediately before the server switch. The server
health check observes the running image revision before the engine and hosts
change. The plan must
stay within its expiry. Every step writes start/end UTC times, duration,
status, and failure text into one owner-only JSON journal. The `steps` array
supplies the deploy duration table. Keep the journal for recovery.

```sh
bash deploy/networked/deploy.sh rollback "$commit" --manifest "$manifest" --journal "$journal" --dry-run
bash deploy/networked/deploy.sh rollback "$commit" --manifest "$manifest" --journal "$journal"
```

Rollback rechecks preflight and work state, drains admission, and restores only
hosts whose release-changing step was reached, in review, coding, engine,
server, gate order. It restores the captured API/web image digests, engine image ref,
gate SSH host, worker path, and agent-host release. Review replacement again requires an idle
observation. The host adapter verifies the resulting health before reporting a
restore. For a server container started from a tag, the snapshot resolves the
tag's repository digest only while it still names the running image, then
falls back to the running image ID. It records the image ID before any switch. Rollback
pulls the captured digest and checks that the restored container has the same
image ID. If Docker cannot resolve a unique repository digest, the deploy
stops at snapshot. If the release changed on-disk formats, use the installation's backup
restore procedure before a downgrade.

Run fake-host checks without contacting an installation:

```sh
bash deploy/networked/tests/deploy.test.sh
python3 -m unittest discover -s deploy/networked/tests -p 'test_*.py'
```
