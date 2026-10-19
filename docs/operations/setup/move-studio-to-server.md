# Move Studio to a server

Application lifecycle D15, option B (decided 17.10.2026): Studio leaves Stable on the workstation and runs on agent-runner-01 as the networked monolith stack in Docker, published as `robert.omgai.dev` through the Cloudflare proxy. The server's load containment comes from AGT-3213. This page is the procedure. One script performs it: [`scripts/migration/move-studio-to-server.sh`](../../../scripts/migration/move-studio-to-server.sh). Background on the pieces it combines:

- [studio-website.md](./studio-website.md): the networked Compose stack (API, frontend, Caddy edge), `setup.sh`, backup and update.
- [monolith-control-plane-docker.md](./monolith-control-plane-docker.md): why Windows paths must be relocated offline, the relocation tool, and CLI authentication state under `/data/home`.
- [cli-relogin-runbook.md](./cli-relogin-runbook.md): provider sign-in on an execution host.
- [`scripts/migration/relocate-workspace-paths.mjs`](../../../scripts/migration/relocate-workspace-paths.mjs): the relocation tool the script runs with a generated mapping.

## Modes

| Mode | Stable | What happens |
|---|---|---|
| `--dry-run` (default) | keeps running | Inventory, path mapping, credential checklist, and server preflight. Nothing is written on either host except the local state directory. |
| `--transfer` | keeps running | One transfer pass into server staging. Repeat it as often as you like. Each pass sends only the delta. |
| `--rehearse` | keeps running | Transfer pass, then a separate Compose project (`studio-move-rehearsal`) on `127.0.0.1:18443` against a copy of staging, smoke checks, teardown. |
| `--apply` | stopped | Stops Stable, sends the final delta, switches the server stack to the relocated copy, checks it, and prints the rollback command. |

Run the script on the workstation from Git Bash or another POSIX shell. It needs Git, GNU tar, gzip, curl, ssh, and Node. The server needs Docker Compose 2.24 or later (the rehearsal override uses `!override`), Git, Node, and curl. The workstation workspace is only read. Options: `--workspace`, `--server` (SSH alias, default `agent-runner-01`), `--server-root` (default `/srv/agent-studio`), `--state-dir` (default `<workspace>.move-state`), `--extra-mapping`, `--rehearsal-port`, `--rehearsal-subnet`, and `--yes`. `--help` lists them.

## What moves and how

The inventory classifies every workspace file by area (`.metadata` attempt authority and other metadata, `results`, `attachments`, `logs/bus` and other logs, `.runtime`, `.orchestration-*`, `.security`) and by Git state (tracked, tracked but modified, untracked). It prints the size of each area, every registered project with its absolute storage, repository, and root paths, and the planned mapping.

- Git history: the server clones the workspace from its own remote. The workstation then pushes its `HEAD`, including commits the remote does not have yet, into `refs/move/source-head` on the server, and the staging checkout is reset to it.
- Untracked state and modified tracked files: a streamed `tar | gzip | ssh` archive. A manifest of size and modification time in the state directory makes later passes send only new and changed files and remove files that disappeared. Modified tracked files and deleted tracked files are handled again on every pass because the Git reset restores them.
- Never copied: credential-shaped files (`.git-credentials`, `auth.json`, `.credentials.json`, `oauth_creds.json`, private keys, `.env` files) and transient files (`*.tmp`, relocation backups). The inventory lists each one.
- `.security/access-state.json` (Studio accounts and sessions) is copied. Treat staging and the server data directory as credential material.
- Project checkouts are not copied. Each checkout outside the workspace becomes `/data/projects/<name>`, cloned on the server from the project's repository URL (the registry `repo` URL, otherwise the workstation checkout's `origin`). Uncommitted work in workstation checkouts stays there.

Staging on the server (`<server-root>/move-staging`) always holds the raw transferred state. Relocation only ever runs on copies of it, so the delta logic stays valid across passes.

## Credentials

The move never copies a login. The inventory reports which of these exist on the workstation and the replacement for each:

| Credential | Replacement on the server |
|---|---|
| Claude, Codex, Gemini, Copilot CLI logins | Sign in inside the server stack, for example `docker compose exec api claude`. State persists in `<data>/home`. |
| GitHub CLI token | `gh auth login` on the server with a token scoped to the server. |
| Workstation SSH keys | A new key in `<data>/home/.ssh`, registered as a deploy key. |
| Git Credential Manager entries | Never exported. The server gets its own deploy key or fine-grained token. |
| Remote runner credentials | Stay on their hosts and stay valid with the moved access state. Point each runner at `https://robert.omgai.dev` after apply. |

The server must be able to clone the workspace remote and every project repository with its own credential before the first transfer pass.

## Prepare the server

1. Copy `deploy/compose/monolith-networked` of the release you will run to `<server-root>/stack` on the server.
2. In that directory run `bash setup.sh <version> robert.omgai.dev <server-root>/data` as the operator account (not root) with Docker access. The moved access state replaces the installer owner code. The code is needed only when the workstation had no accounts.
3. In Cloudflare, use SSL/TLS mode Full (strict) and make sure the edge can obtain its certificate. Caddy's ACME challenge must reach the origin.
4. Give the server its own Git credential (see above).

## Procedure

```sh
sh scripts/migration/move-studio-to-server.sh                 # dry run
sh scripts/migration/move-studio-to-server.sh --rehearse      # any time before the window, Stable keeps running
sh scripts/migration/move-studio-to-server.sh --apply         # in the outage window
```

1. Dry run. Resolve every blocking issue it prints. `missing-repository-url` means a checkout has no usable remote: add a `repo` URL to the project. `unmapped-path` means a registry path lies outside the workspace and every checkout: pass `--extra-mapping FILE` with a JSON object such as `{"G:\\Elsewhere\\root": "/data/projects/demo/root"}`.
2. Rehearse. The rehearsal copies staging, relocates paths (it fails on any unresolved Windows path and leaves the report in `<server-root>/move-bin/relocate-report.json`), disables Git pushes in the copy, starts the stack with `WorkspaceEvidence__Push=false` under a private hostname on the loopback port, and checks health, sign-in, a board read, and one runner claim. The claim uses a runner credential enrolled for the test and reports zero free slots, so it authenticates without leasing a card. By default the copy's access state is validated and set aside, and a disposable test owner is bootstrapped. To sign in with an existing owner instead, set `MOVE_REHEARSAL_OWNER_USERNAME` and `MOVE_REHEARSAL_OWNER_PASSWORD`. The login is passed on stdin. On failure the last Compose logs are kept in `<server-root>/move-bin/rehearsal-failure.log`. The stack and the copy are always removed.
3. Apply. The script asks for confirmation (`--yes` skips it) and runs `stop-stable.sh` beside the workspace (override with `MOVE_STABLE_STOP`). It verifies that `MOVE_STABLE_HEALTH_URL` (default `http://127.0.0.1:5031/healthz`) no longer answers, then repeats the inventory and sends the final delta. On the server it moves the previous `workspace` and `projects` aside as `*.pre-move-<UTC stamp>`, copies staging into the data directory, relocates paths, sets UID 10001 ownership, and starts the stack. It then checks `https://robert.omgai.dev/api/auth/status` locally and expects the moved accounts. Stop every other writer to the workspace, such as a dev backend, before you apply.

## Rollback

Apply prints the rollback command, and prints it again if any step after stopping Stable fails:

```sh
ssh agent-runner-01 'docker compose --project-directory /srv/agent-studio/stack stop' && '<devspace>/start-stable.sh'
```

The workstation workspace was never modified, so Stable resumes from the exact state it had when it stopped. Work done on the server after the switch is not carried back. Never run both instances against their copies at the same time.

## After the move

Sign in at `https://robert.omgai.dev`, complete the credential replacements, point remote runners at the new URL, and keep the state directory until the old setup is retired. Backups, updates, and stopping the stack follow [studio-website.md](./studio-website.md).

## Tests

`node --test scripts/migration/move-studio-to-server.test.mjs` covers the pure parts (inventory classification, mapping generation, delta selection) with the fixture in `scripts/migration/fixtures/move-studio-workspace.json` and an injected clock. `sh scripts/migration/move-studio-to-server.test.sh` runs the dry run, three transfer passes, a rehearsal, and an apply against local repositories, with fake `ssh`, `docker`, and `curl`. No real host is contacted.
