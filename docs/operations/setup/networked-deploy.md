# Networked installation deploy

`deploy/networked/deploy.sh` drives one server-first release window. It builds
the API, web, engine, agent-host, and gate-worker artifacts in parallel from
one full Git commit SHA, then executes checked host operations in order. It
does not contain installation host names, addresses, accounts, or credentials.
This implements the OP-6 Dossier's server-first, no-blocker-override working
choice; no different operator decision was recorded.

## Local configuration

Copy [`manifest.example.json`](../../../deploy/networked/manifest.example.json)
to a private path outside the checkout. Replace each target and each
`/path/to/local-adapter` command with the installation's operator-owned command
or script. Keep the file owner-only. Command arrays execute directly, without
a shell; use an explicit `bash` entry only when a local adapter needs one.
The `studio-op` commands require the existing owner-only operator token file.
The adapter can use the existing image-build scripts, SSH release transfer,
`agent-runner-deploy`, and Compose setup for this installation. Each command
must return one JSON object on stdout, send diagnostics to stderr, and exit
nonzero on failure. Do not put credentials in command arguments or JSON output.

The local adapter contract is:

| Command | Required JSON output or effect |
|---|---|
| `build.*` | `{"commit":"<full SHA>","ref":"<immutable ref>","sha256":"<64 lowercase hex>"}`. API, web, and engine refs use `@sha256:`. Agent-host and worker refs identify immutable release paths. |
| `snapshot.*` | `{"value":{...}}` with current installed values. Server: `apiImage`, `webImage`, `engineVersion`, `workerPath`; gate: `workerPath`; coding: `agentHostRelease`; review: `agentHostRelease`, `image`. Rollback receives this object as compact JSON in `{previous}`. |
| `preflight` | `studio-op preflight deploy --json`; must report `allowed: true` and no blockers. |
| `work_state` | `{"formedBatches":[],"runningGates":[]}` for a clear window. Populate both arrays from the current batch and gate inventory. Nonempty arrays stop the deploy. |
| `plan` | `studio-op deploy plan --target-version <SHA> --json`. The runner verifies the OP-6 order and expiry. |
| `drain`, `resume` | Drain admission before switching releases (`drained: true`). Resume only after host model probes and review verification (`admitting: true`). |
| `gate_publish`, `gate_verify` | Publish the worker release on the gate host, then verify its SHA-256. Publish returns the target `commit` and `ref`; verify returns `sha256`. The server switch sets `RemoteGate__WorkerPath` to this release. |
| `server_switch`, `server_health` | Switch only the API and web to the digest refs and set the worker path. Health returns `{"healthy":true,"commit":"<SHA>"}` after the switch. |
| `engine_switch`, `engine_health` | Switch the engine after server readiness. Health has the same output shape. |
| `coding_install`, `coding_health`, `model_probe` | Install the agent-host release and restart its services. Health names the running commit. Run model acceptance on each coding host before admitting work; probe returns `{"accepted":true}` only when the configured models are ready. |
| `review_copy`, `review_build`, `review_idle`, `review_recreate`, `review_verify` | Copy the checked agent-host build to each review host (`sha256`), build its image (`commit`, matching `agentHostSha256`, `planDigest`, digest-pinned `imageRef`), prove every container to be replaced is idle (`idle: true`), then recheck idle state atomically in the recreate adapter before replacing it. Verification returns the running `commit`, matching `planDigest`, and matching `imageRef`. Leave busy containers untouched and fail the step. |
| `rollback.*` | Restore and verify the snapshot for that host. The server rollback restores both image refs, engine version and `RemoteGate__WorkerPath`; coding restores the agent-host release and restarts services; review restores its release and image only when idle; gate restores its worker path. Return `{"restored":true}` only after observing the prior values and health. |

The host adapter owns the actual SSH, image registry, service, and container
commands for the installation. Its health and idle checks must observe the
running state, not just successful command exits. In particular, the
`review_recreate` adapter repeats the idle check at the point it changes a
container, so a review starting after `review_idle` is never interrupted.

## Run

```sh
commit=$(git rev-parse develop)
manifest=/private/networked-deploy.json
journal=/private/deploy-$(date -u +%Y%m%dT%H%M%SZ).json
bash deploy/networked/deploy.sh deploy "$commit" --manifest "$manifest" --journal "$journal" --dry-run
bash deploy/networked/deploy.sh deploy "$commit" --manifest "$manifest" --journal "$journal"
```

Dry run validates the manifest and prints the plan without invoking any build,
host, API, or journal command. The live run refuses an existing journal. It
rechecks OP-6 preflight and formed batches/running gates immediately before
the server switch. It refuses any later change after the plan expires. All
steps write start/end UTC timestamps, duration seconds, and pass/fail status
into one owner-only JSON journal. The `steps` array directly supplies the
deploy duration table. A failed step leaves the journal and previous values
available for recovery.

```sh
bash deploy/networked/deploy.sh rollback "$commit" --manifest "$manifest" --journal "$journal" --dry-run
bash deploy/networked/deploy.sh rollback "$commit" --manifest "$manifest" --journal "$journal"
```

Rollback rechecks preflight and work state, drains admission, and restores only
hosts whose release-changing step was reached, in review, coding, server, gate
order. It resumes admission after the rollback adapters verify health. It uses captured
previous values from the journal; keep that file with the release evidence.
The adapter must refuse an unsafe downgrade after a store schema change and
use the verified backup procedure in that case. A failed rollback remains
visible in the same journal for operator repair.

Run the fake-host contract checks with
`bash deploy/networked/tests/deploy.test.sh`. They do not contact a host.
