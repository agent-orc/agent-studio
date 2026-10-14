# Monolith control plane on agent-runner-01

This is the temporary production placement for the existing `OrchestratorApi` task server, review plane, batch gate, integration, publication, and Studio API. It runs in Docker on Linux. The standalone Task Server cutover is separate. Keep the workstation instance stopped while this instance owns the workspace. The operator owns the production transfer, networking, and rollback mirror.

## Prepare the host

Use [the Compose file](../../../deploy/compose/monolith/compose.yaml) and copy [the environment example](../../../deploy/compose/monolith/.env.example) to `deploy/compose/monolith/.env`. Set `MONOLITH_VERSION` to this checkout's `VERSION`, replace the API image placeholder with a release tag built from this change (or a local tag and run `docker compose build orchestrator-api`), and stage the matching prebuilt Studio bundle at `MONOLITH_STUDIO_DIST_DIR`. `index.html` must be at that directory root. The Studio service uses the repository's `serve-prebuilt-frontend.mjs` and Linux host networking to bind `127.0.0.1:4011` and proxy to `127.0.0.1:5031`. The API's only published address is `127.0.0.1:5031`.

Copy [the production config example](../../../deploy/compose/monolith/appsettings.Production.json.example) to `MONOLITH_CONFIG_FILE`. Set `TaskRepository` to `/data/workspace`. Set every `WatchPaths` path to its Linux mount path; it seeds only missing registry records. Existing project paths come from `.metadata/projects.json` and must be relocated before startup. `RepositoryPath` must resolve to an existing checkout containing `.git`. For additional checkouts, add one bind mount per project in a local Compose override at `/data/projects/<name>` and set the same paths in the registry. Never bind an entire Windows checkout tree in place of these Linux targets. Set `MONOLITH_REMOTE_GATE_SSH_HOST` to the nonlocal OpenSSH host alias for the approved gate worker and `MONOLITH_REMOTE_GATE_WORKER_PATH` to the absolute path of its release-pinned `OrchestratorApi.dll`. Compose passes these as `RemoteGate:SshHost` and `RemoteGate:WorkerPath`; both are required when a remote gate runs.

The API runs as UID and GID 10001. Give that identity read and write access to the workspace, every checkout, and `MONOLITH_HOME_DIR`; give it read access to the config, Git credential store, and Claude, Codex, and Gemini authentication seed directories. The API image includes pinned CLI packages for all three providers because monolith orchestration can launch CLI one-shots itself. The authentication seed directories are mounted read only under `/run/cli-auth/`. At each container start, the entrypoint copies their contents into separate writable CLI state under `/data/home/.claude`, `/data/home/.codex`, and `/data/home/.gemini`. Changes made by the CLIs stay in `MONOLITH_HOME_DIR` and cannot modify the host authentication seed directories. Refresh the seed directories on the host before restarting if renewed credentials must survive a restart. Keep these seed directories small and include only the auth and base configuration needed by each CLI. Place the Git credential store at `MONOLITH_GIT_CREDENTIALS_FILE`. Put OpenSSH `config`, the private key named by `IdentityFile`, and `known_hosts` in `MONOLITH_SSH_DIR`; mount them read only. Set the key to mode 0600 for UID 10001, the directory to 0700, and the other files to 0600 or 0644 as appropriate. Configure both GitHub and the remote gate host alias there. If the gate host requires a jump host, use an OpenSSH `Host` stanza with `ProxyJump` and host key entries for both hops. The container's `HOME` is `/data/home`, so ordinary `ssh` and Git use that config.

The API has `restart: unless-stopped`, bounded JSON logs, and `cpu_shares: 2048`. CPU shares are a relative weight under contention, not a reserved CPU quota.

## Relocate paths offline

Stop all API writers first and copy the full workspace, all project checkouts, and config to the Linux host. Worktree slot names hash the absolute repository path, so clear or finish old integration slots before resuming. Inspect `.git` worktree links as part of the migration. Use a mapping JSON object whose keys are Windows path prefixes and values are absolute Linux prefixes, for example:

```json
{
  "C:\\Projects\\agent-taskboard-workspace": "/data/workspace",
  "D:\\Projects\\demo": "/data/projects/demo"
}
```

Run the relocation tool from the source checkout. Supply the production config and each checkout. Add `--link FILE` for worktree link files outside those checkouts. The default is a dry run. It lists every absolute JSON path in `.metadata`, including `projects.json.bak`, the config, and Git worktree links, with each Windows source and Linux target. The registry can recover from `projects.json.bak` if its primary file is absent or invalid, so both must be relocated. An unresolved Windows path makes the command exit 2. Apply refuses to write unless that count is zero.

```sh
node scripts/migration/relocate-workspace-paths.mjs --workspace /srv/agent-studio/workspace --mapping /srv/agent-studio/path-map.json --config /srv/agent-studio/config/appsettings.Production.json --checkout /srv/agent-studio/checkouts/demo
node scripts/migration/relocate-workspace-paths.mjs --workspace /srv/agent-studio/workspace --mapping /srv/agent-studio/path-map.json --config /srv/agent-studio/config/appsettings.Production.json --checkout /srv/agent-studio/checkouts/demo --apply
```

Apply backs up every changed file and writes a restore manifest beside the workspace in `workspace.migration-backups/<timestamp>/`. It preserves JSON layout and line endings. Check the reported paths and the zero unresolved count before starting the container. Use the registry API for later live changes to project paths.

## Start, update, and recover

From `deploy/compose/monolith`, run `docker compose config` to validate mounts, then `docker compose pull && docker compose up -d --no-build`. Check `/healthz` at port 5031 and Studio at port 4011. The Windows Update Service cannot update this placement; `UpdateService:ProbeEnabled=false` closes its probe. For a release, stage the matching Studio bundle, set a new pinned API image tag and bundle directory in `.env`, then run `docker compose pull && docker compose up -d --no-build`. Preserve the previous tag and Studio bundle. Roll back by restoring both values in `.env` and running `docker compose up -d --no-build` with the prior local image; pull that tag first if it is no longer cached. Restore workspace and config from the pre-update backup if the release changed on-disk formats. Do not run both monoliths against the same workspace.

The Linux readiness audit checked API startup and the relevant shell paths: `PlatformShellCommand`, `GateShellInvocation`, and `ProjectUrlProcessService` use `/bin/sh` on Linux; `BashExecutable` resolves `bash` from PATH; the Windows orphan sweeper and PowerShell probes have OS guards. The remaining host-specific state is the registry/config/worktree paths handled above. The Linux image includes the .NET runtime, Git, OpenSSH, Node, pinned agent CLIs, curl, and the API health check. Its passwd home is `/data/home`, which OpenSSH uses to locate the mounted config.

Run `bash scripts/monolith-compose-smoke.sh` from the repository to build and start the stack with a disposable workspace and local Git project. It checks health, Studio, board read, task creation and movement, and a commit in the workspace repository. It requires Docker, curl, jq, and Python 3; it selects free loopback ports for the disposable stack.
