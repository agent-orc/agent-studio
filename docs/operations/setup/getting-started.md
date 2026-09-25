# One-box Compose installation

The [deployment Dossier](../../deployment-story/index.html) defines one
installation that starts on one workstation, can add runner hosts, and can
later move its authority to an always-on box. The workstation is a placement,
not a separate agent product. The Task Server is the only task authority in
this Compose contract. The browser edge, engine, coding daemon and review
daemon use that same Task Server.

This is the I01 one-box wiring after AGT-2736's limited option C baseline.
It is still a **transitional one-box baseline**: the Angular application has
legacy `/api/*` routes without standalone equivalents, human account setup
and host join are not guided, and detached publication/recovery have not passed
I08. Do not use the UI shell or a successful fake-CLI run as proof of the
complete deployment ladder.

## Prerequisites and first start

Use Docker Engine with Compose v2 or Docker Desktop, Git, enough disk for
images, worktrees and backups, and a separately retained recovery destination.
Coding and review require provider credentials and Git fetch/push access to
the project's real origin. A Linux container on a Windows desktop advertises
Linux tools; it does not gain Windows toolchains.

```sh
git clone https://github.com/agent-orc/agent-studio.git
cd agent-studio
scripts/compose-distributed-bootstrap.sh
```

The script creates owner-only `.env` and `runner.env` files and four distinct
service credentials. Re-running it preserves their values and the Task Server
identity. Edit `runner.env` to replace the example Git origin and supply a
provider credential, such as `CLAUDE_CODE_OAUTH_TOKEN` or `ANTHROPIC_API_KEY`.
The bootstrap does not create these external credentials. Keep both files in
the installation recovery set.

`.env` uses one `AGENT_STUDIO_IMAGE_TAG` for every published image. It starts
as `unpublished` because this I01 source has not been released as a compatible
image set. After release, set it to one verified `v<version>` tag and confirm
that **all six** AGT-2729 images exist at that tag before using the published
image path:

```sh
docker compose pull
docker compose up --wait
```

The published-image path is separate from source-build evidence. If the tag
cannot be pulled, do not infer it from a successful local build. The interim
source-built installation from this checkout is:

```sh
docker compose --profile dev up --build --wait \
  task-server-dev orchestrator-engine-dev studio-bff-dev \
  frontend-dev agent-host-distributed-dev agent-host-review-distributed-dev
```

Name the services explicitly: `--profile dev up` would also start the
published default services and collide on ports. Open
<http://127.0.0.1:4011>. The default browser listener is loopback only.
Health at `/healthz` proves the edge and BFF are live; it does not prove
provider login, Git permissions, review or publication.

## Service and route ownership

| Service | Owns | Host binding |
|---|---|---|
| `task-server` | Workspace, task, attempt and principal authority | `127.0.0.1:5071` for local diagnostics |
| `orchestrator-engine` | Steering through Task Server APIs | None |
| `studio-bff` | Same-origin `/api/v1/*` and `/hubs/*` proxy with a server-side Studio credential | `127.0.0.1:5072` for local diagnostics |
| `frontend` | Static browser edge and BFF proxy | `127.0.0.1:4011` |
| `agent-host-distributed` | One coding slot and a coding principal | None |
| `agent-host-review-distributed` | One review slot and a separate review principal | None |

The BFF forwards versioned Task Server routes. Unknown `/api/*` paths return
404. A mutation through the BFF requires an exact allowed `Origin`; a foreign
Origin returns 403. `STUDIO_ALLOWED_ORIGINS` defaults to the two local HTTP
origins on port 4011. When overriding `STUDIO_UI_PORT`, update this setting
to the exact new origin. LAN access requires a separately configured HTTPS
reverse edge and its explicit HTTPS origin allowlist. Never widen the
Connector's fixed `[::1]:5031` and `http://[::1]:4011` contract to solve
Compose access.

| Route family | Browser edge | Writer |
|---|---|---|
| `/api/v1/workspaces`, `/projects`, `/runs`, `/reviews`, `/management`, `/studio` | Caddy to BFF | Task Server, after bearer and protocol checks |
| `/hubs/*` | Caddy to BFF | HTTP forwarding only; WebSocket parity is not certified |
| Legacy `/api/tasks`, `/api/projects`, `/api/runner` and other `/api/*` | BFF returns 404 | None in this installation |
| Unknown `/api/*` | BFF returns 404 | None |
| `/healthz`, `/readyz` | Caddy to BFF | No task writes |

The old `orchestrator-api` and its coding/review runners are under
`--profile legacy` and are not part of this installation. The image service
has no task workspace mount and is fixed to the same Task Server. Only
`/api/v1/*` is forwarded; other `/api/*` routes return 404 instead of writing
a local task repository. Do not mix legacy runner services into the standalone
installation. Source-only `orchestrator-api-dev` can still run in local mode
for compatibility tests; setting an invalid `TASK_SERVER_BASE_URL` now fails
startup instead of silently selecting that mode.

## Data, restart and host extension

`orchestrator-data` stores the Task Server SQLite authority, artifacts and
backups. `runner.env` and `.env` hold the local installation projection and
bootstrap credentials; the runner containers use private local state. Stop
and restart with `docker compose stop` and `docker compose start --wait`.
`docker compose down` keeps named volumes. Never use `down --volumes` on an
installation whose data you intend to retain. Compare the same workspace,
principal and task ids after restart.

Register projects through the Task Server API with a canonical, credential-free
Git URL. Prove fetch and permitted push from each eligible runner host. A new
host receives its own principal, token file, state root, probed capabilities
and finite coding/review budget; it connects to this Task Server through
private HTTPS, or through the supervised reverse SSH transition. The existing
`runner` profile uses the legacy API and is not a host join procedure.

The first box remains the authority as hosts are added. Moving authority,
N-1 upgrade, full recovery rehearsal and detached canonical publication are
separate gates in the [deployment Dossier](../../deployment-story/index.html).
Do not call the ladder complete before I08 passes.

## Verification and evidence

`scripts/compose-smoke-test.sh` builds this checkout's images, checks the
one-box edge and route boundary, tests the explicit compatibility guard and
drives a fake-CLI runner claim. Run the typed full scenario with:

```sh
scripts/scenario.sh --target compose --level full --report-dir "$JOB_RESULTS_DIR"
```

The scenario uses a fixture coding agent. A deployment acceptance canary
also needs a real provider, review of the immutable subject, exact canonical
Git ref publication and a separate empty-target recovery rehearsal. Save
published-image pull evidence apart from source-built evidence. Fresh Ubuntu
VM, Windows/macOS Docker Desktop, N-1 upgrade and provider-authenticated
canary evidence require their named hosts and credentials; no local source
build substitutes for them.

## Troubleshooting

| Symptom | Check |
|---|---|
| Compose is unavailable | Install Docker Compose v2 and confirm `docker compose version`. |
| A role container is unhealthy | Check its logs, the matching bootstrap token, Task Server reachability and the provider/Git settings in `runner.env`. |
| Browser mutation returns 403 | Compare the browser's exact Origin with `STUDIO_ALLOWED_ORIGINS`. |
| Browser route returns 404 | Check the route inventory above. Legacy `/api/*` handlers are not Task Server routes. |
| Registry pull is denied | Verify the pinned release exists and your registry access; keep source-build evidence separate. |

For the older Linux release installer, see [multi-machine setup](./multi-machine.md).
