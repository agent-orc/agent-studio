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
| `--apply` | stopped | Stops Stable, sends the final delta, switches the server stack to the relocated copy, checks it, and prints the rollback command. It runs once: a second apply needs `--force`. |

Run the script on the workstation from Git Bash or another POSIX shell. It needs Git, GNU tar, gzip, curl, ssh, and Node. The server needs Docker Compose 2.24 or later (the rehearsal override uses `!override`), Git, Node, and curl. The workstation workspace is only read. `--workspace` is required in every mode. `--devspace` is required for `--apply`. It names the folder that holds `agent-taskboard-stable`, `start-stable.sh`, and `stop-stable.sh`. On the workstation the workspace (`C:\Projects\agent-taskboard-workspace`) and the devspace (`C:\Projects\agent-taskboard-devspace`) are siblings, so neither is derived from the other. `MOVE_WORKSPACE` and `MOVE_DEVSPACE` set the same values. The dry run reports the Stable checkout, guard, and maintenance marker it derived from `--devspace`, or says why `--apply` would refuse. Further options: `--server` (SSH alias, default `agent-runner-01`), `--server-root` (default `/srv/agent-studio`), `--state-dir` (default `<workspace>.move-state`), `--extra-mapping`, `--rehearsal-port`, `--rehearsal-subnet`, `--yes`, and `--force`. `--help` lists them.

## What moves and how

The inventory classifies every workspace file by area (`.metadata` attempt authority and other metadata, `results`, `attachments`, `logs/bus` and other logs, `.runtime`, `.orchestration-*`, `.security`) and by Git state (tracked, tracked but modified, untracked). It prints the size of each area, every registered project with its absolute storage, repository, and root paths, and the planned mapping.

- Git history: the server clones the workspace from its own remote. The workstation then pushes its `HEAD`, including commits the remote does not have yet, into `refs/move/source-head` on the server, and the staging checkout is reset to it.
- Untracked state and modified tracked files: a streamed `tar | gzip | ssh` archive. A manifest of size and modification time in the state directory makes later passes send only new and changed files and remove files that disappeared. Modified tracked files and deleted tracked files are handled again on every pass because the Git reset restores them.
- Every registered `StorageLocation` outside the workspace is transferred as its own delta root, even when it lies inside a cloned checkout. This includes projects with no `RepositoryPath`. The script screens each file for credentials and tokens, keeps a separate manifest per project, and stages it at `<server-root>/move-staging/storage/<project-id>`. Rehearsal and apply copy it to `<data>/projects/.task-storage/<project-id>`, which the API container sees at `/data/projects/.task-storage/<project-id>`. The generated mapping points to that copy. A missing source folder blocks the move; `--extra-mapping` cannot substitute for the task files.
- Never copied: credential-shaped files (`.git-credentials`, `auth.json`, `.credentials.json`, `oauth_creds.json`, private keys, `.env` and `*.env` files such as `runner.env`, `*.token`, `.npmrc`, `.netrc`, GitHub CLI `hosts.yml`, Copilot `apps.json`, Docker `.docker/config.json`), any file that would travel in the archive and contains a known token shape (GitHub, Anthropic, OpenAI, AWS, Google, Slack, and npm tokens, private key blocks, URLs with a password), and transient files (`*.tmp`, relocation backups). The inventory lists each exclusion with its reason. The token screen reuses its result for files whose size and modification time did not change, so a delta pass only reads new and changed files. Files that are already in the workspace's Git history still arrive through Git.
- A file sent in an earlier pass that is excluded now, for example because a token appeared in it, is removed from staging. An excluded modified tracked file keeps its committed version on the server, and the inventory says so.
- A token hit in non-disposable root state or any task folder, including an external `StorageLocation`, is a blocking `token-in-state` issue. Disposable `.runtime`, `logs`, and `.logs` files with token hits are excluded and reported. Without a blocked state file the server's attempt authority, orchestration records, or task folder would be incomplete. Remove the token from the file, then run the pass again.
- Repository URLs lose embedded credentials before they are logged, written to `checkouts.tsv`, or passed to the server. HTTP(S) URLs drop the whole user part and ssh URLs drop the password. Existing staging clones get the stripped URL on the next pass.
- `.security/access-state.json` (Studio accounts and sessions) is copied. Every server-side step runs with `umask 077`: archive extraction, clones, the history push, the rehearsal copy with its owner login, cookies, and runner credential, and the apply copy. Staging is mode 700 and `.security` stays owner-only (700, files 600). A successful apply removes staging. The rehearsal owner password and the one-time bootstrap and enrollment codes reach their JSON payloads on stdin, never on a command line. Treat the server data directory as credential material.
- Project checkouts are cloned, not copied. Each checkout outside the workspace becomes `/data/projects/<name>`, cloned on the server from the project's repository URL (the registry `repo` URL, otherwise the workstation checkout's `origin`). Its task storage is copied separately, even when it is untracked beneath that checkout. Other uncommitted work in workstation checkouts stays there.

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

Two server identities need Git access, and neither sees the other's credential:

- The operator account on the server host clones the workspace remote and every project repository into staging before the first transfer pass. It uses its own `~/.ssh` key or Git credential store.
- The API container pushes workspace evidence and works in the checkouts after the switch. It runs as UID 10001 with `HOME=/data/home`, so it reads only `<data>/home` on the host. The host operator's credential is not visible inside the container. Give the container its own credential there before `--apply`:
  - SSH remotes: put a deploy key with write access in `<data>/home/.ssh/id_ed25519`, the host keys in `<data>/home/.ssh/known_hosts`, and, if needed, a `config` with `IdentityFile ~/.ssh/id_ed25519`.
  - HTTPS remotes: write a fine-grained token to `<data>/home/.git-credentials` (`https://<user>:<token>@github.com`) and set `[credential] helper = store` in `<data>/home/.gitconfig`.

  Make the files owned by UID 10001, with the directory at 700 and the key and credential files at 600 (`sudo chown -R 10001:10001 <data>/home`). Apply already sets UID 10001 ownership on `<data>/home`. The URLs the move stores carry no credentials, so the container always authenticates with this credential. After the switch, check it with `docker compose exec api git -C /data/workspace push --dry-run`.

## Prepare the server

1. Copy `deploy/compose/monolith-networked` of the release you will run to `<server-root>/stack` on the server.
2. In that directory run `bash setup.sh <version> robert.omgai.dev <server-root>/data` as the operator account (not root) with Docker access. The moved access state replaces the installer owner code. The code is needed only when the workstation had no accounts.
3. In Cloudflare, use SSL/TLS mode Full (strict) and make sure the edge can obtain its certificate. Caddy's ACME challenge must reach the origin.
4. Give the server its own Git credentials (see above): one for the operator account, for the transfer, and one in `<data>/home`, for the API container.

## Procedure

```sh
cd /c/Projects/agent-taskboard-devspace/agent-taskboard-stable
set -- --workspace /c/Projects/agent-taskboard-workspace --devspace /c/Projects/agent-taskboard-devspace
sh scripts/migration/move-studio-to-server.sh "$@"                 # dry run
sh scripts/migration/move-studio-to-server.sh --rehearse "$@"      # any time before the window, Stable keeps running
sh scripts/migration/move-studio-to-server.sh --apply "$@"         # in the outage window
```

1. Dry run. Resolve every blocking issue it prints. `missing-repository-url` means a checkout has no usable remote: add a `repo` URL to the project. `missing-storage-root` means the workstation cannot read a registered task folder: restore or correct that source folder before transfer. `unmapped-path` means a non-storage registry path lies outside the workspace and every checkout: pass `--extra-mapping FILE` with a JSON object such as `{"G:\\Elsewhere\\root": "/data/projects/demo/root"}`. `token-in-state` means a state file holds a token: remove the token from that file.
2. Rehearse. The rehearsal copies staging, relocates paths (it fails on any unresolved Windows path and leaves the report in `<server-root>/move-bin/relocate-report.json`), disables Git pushes in the copy, starts the stack with `WorkspaceEvidence__Push=false` under a private hostname on the loopback port, and checks health, sign-in, a board read, and one runner claim. The claim uses a runner credential enrolled for the test and reports zero free slots, so it authenticates without leasing a card. By default the copy's access state is validated and set aside, and a disposable test owner is bootstrapped. To sign in with an existing owner instead, set `MOVE_REHEARSAL_OWNER_USERNAME` and `MOVE_REHEARSAL_OWNER_PASSWORD`. The login is passed on stdin. On failure the last Compose logs are kept in `<server-root>/move-bin/rehearsal-failure.log`. The stack and the copy are always removed.
3. Apply. `--devspace` must hold `agent-taskboard-stable/api.sh` and `start-stable.sh`; otherwise apply refuses before it stops anything. Every Stable path comes from it: the checkout `<devspace>/agent-taskboard-stable`, `stop-stable.sh` and `start-stable.sh`, the maintenance marker `<devspace>/.stable-maintenance` (the `${ROOT}/.stable-maintenance` that `watchdog-stable.sh` honours, override with `STABLE_MAINTENANCE_FILE`), and the guard. The script computes the guard exactly as `api.sh` does, as `.studio-moved-to-server` in the parent of the stable checkout. Stable's `api.sh` must honour the guard; otherwise apply refuses. The script asks for confirmation (`--yes` skips it) and notes whether `MOVE_STABLE_HEALTH_URL` (default `http://127.0.0.1:5031/healthz`) answers. It then sets the maintenance marker and the guard, runs `stop-stable.sh`, and verifies that Stable no longer answers. It repeats the inventory and sends the final workspace and task-storage deltas. On the server it runs the relocation dry run against staging and refuses on any unresolved path. It then stops the stack and verifies that no container is still running, restarting, or paused. Only then does it move the previous `workspace` and `projects` aside as `*.pre-move-<UTC stamp>`, copy staging into the data directory, relocate paths, set UID 10001 ownership, and start the stack. It checks `https://robert.omgai.dev/api/auth/status` locally and expects the moved accounts, then writes `<server-root>/moved-to-server` and removes staging. Stop every other writer to the workspace, such as a dev backend, before you apply.

   A failure before the server switch (a blocking issue in the final inventory, a failed transfer, unresolved paths in staging, or a server stack that would not stop) leaves the live server data untouched. The script restarts Stable with `DETACH=1 start-stable.sh` only when two things hold. First, this run found neither a server marker nor a guard. Second, Stable answered before the stop. It then also lifts the guard and the maintenance marker it set. In every other case Stable stays stopped and the guard stays in place, because the server may be the live Studio. This applies, for example, to `--force` after a completed or failed apply. The script then prints the rollback command. If the server fails after the first data move, the server side stops the stack again, so no half-switched Studio serves. Stable stays stopped behind the `status=switching` guard, and the script prints the rollback command.

## Guards against two Studios

Two Studios against the same remotes and runners would race each other. After apply, three things keep Stable down:

- The maintenance marker stops `watchdog-stable.sh` from restarting the backend. The script removes it on rollback only if it set it.
- The guard `<devspace>/.studio-moved-to-server` makes `api.sh start` in the stable checkout refuse. `start-stable.sh`, the watchdog, and the Update Service all start the backend through it. The guard records `status=moved` (or `status=switching` while an apply runs), the server, and the time.
- The server marker `<server-root>/moved-to-server` and any guard, `status=moved` or `status=switching`, make another `--apply` refuse before it stops anything. A second apply would replace the live server data with the old workstation snapshot. After a failed switch, the server may still hold the half-switched data. `--force` overrides the marker and the guard. Use it only to discard the server's state on purpose, for example to move again after a rollback. With `--force`, a pre-switch failure never restarts Stable.

## Rollback

Apply prints the rollback command, and prints it again if a step after the server switch fails:

```sh
ssh agent-runner-01 'docker compose --project-directory /srv/agent-studio/stack stop' && rm -f '<devspace>/.studio-moved-to-server' '<devspace>/.stable-maintenance' && DETACH=1 '<devspace>/start-stable.sh'
```

Order matters: stop the server stack first, then lift the guard, then start Stable. `DETACH=1` starts Stable in the background, as the Stable updater does. The command lists `.stable-maintenance` only when this apply set it; a marker that was there before stays until you remove it. The workstation workspace was never modified, so Stable resumes from the exact state it had when it stopped. Work done on the server after the switch is not carried back. Never run both instances against their copies at the same time. The server keeps `moved-to-server` after a rollback, so moving again later needs `--apply --force`.

For a forced apply, the script also prints the server snapshot names `<data>/workspace.pre-move-<stamp>` and `<data>/projects.pre-move-<stamp>` if the data move began. Resolve `<data>` from `STUDIO_DATA_DIR` in the stack `.env` before restoring a previous server copy.

## After the move

Sign in at `https://robert.omgai.dev`, complete the credential replacements, point remote runners at the new URL, and keep the state directory until the old setup is retired. Backups, updates, and stopping the stack follow [studio-website.md](./studio-website.md).

## Tests

`node --test scripts/migration/move-studio-to-server.test.mjs` covers the pure parts (inventory classification, mapping generation, delta selection) with the fixture in `scripts/migration/fixtures/move-studio-workspace.json` and an injected clock. The same suite covers the wider credential exclusion, the token screen, and URL credential stripping. `sh scripts/migration/move-studio-to-server.test.sh` runs against local repositories with fake `ssh`, `docker`, and `curl`. The workspace and the devspace are siblings, as on the workstation, and a copy of `api.sh` stands in for Stable. The suite covers:

- refusal without `--workspace`, and the dry run with and without `--devspace`;
- three transfer passes, then the removal of a sent file that gained a token, and a blocking token in orchestration state;
- task storage inside a checkout and outside every checkout, each with its own delta, including a file addition and removal;
- a rehearsal, including the stdin-fed sign-in and enrollment payloads;
- rehearsal teardown and failure logs under the fixed `studio-move-rehearsal` Compose project;
- refusal for a Stable without the guard and for a missing or wrong `--devspace`;
- automatic restart after a blocking final inventory and after a server stack that keeps running;
- Stable kept down when it did not answer before the stop;
- a failed switch that stops the server stack again, a refused re-run after it without `--force`, and the printed rollback;
- a successful apply, with `api.sh` refusing through the guard the script wrote and nothing written beside the workspace;
- refusal of a second apply, `--force` with a blocking final inventory that keeps Stable stopped, and a successful `--force`.

No real host is contacted.
