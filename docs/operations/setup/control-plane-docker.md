# Control plane on Docker (task-server-01)

Status: bootstrap material for Phase B slice B5
([remote-task-server-local-studio.md](../remote-task-server-local-studio.md)),
runtime changed from systemd packages to Docker containers per the
2026-09-06 operator decision. Reuses the same published images and the same
`agent-orchestrator-setup` guided installer as the systemd target; only the
runtime differs.

This is the Docker-target sibling of
[task-server.md](./task-server.md#package-and-process-boundary). Run exactly
one target (systemd or Docker) on a given host. The Docker target is a
dedicated VM (`task-server-01`, never co-hosted with `agent-runner-01`) that
runs Task Server, Orchestrator Engine, a scheduled backup sidecar, and a
private TLS edge behind WireGuard. It never serves Angular; Robert's Studio
stays on Windows.

## What this card prepares vs. what stays an operator action

Everything in this document runs from a checkout of this repository once the
operator has done three things that are not part of this card:

1. Created the Hetzner VM `task-server-01`.
2. Generated WireGuard peer keys for Robert's Windows device.
3. Assigned private DNS (or a hosts-file entry) for the control-plane domain,
   if any is used beyond the WireGuard address.

With those three inputs, the bootstrap below brings the control plane up in
under 15 minutes.

## Compose sources

[`deploy/compose/control-plane/`](../../../deploy/compose/control-plane/)
holds:

| File | Purpose |
|---|---|
| `compose.yaml` | `task-server`, `orchestrator-engine`, `backup`, and `edge` services. |
| `.env.example` | Every Compose variable, documented; copy to `.env` or let `install-docker.sh` write it. |
| `Caddyfile` | Self-signed leaf via Caddy's internal CA. Default for first bootstrap and CI. |
| `Caddyfile.private-ca` | Alternate edge config for an operator-issued private-CA certificate. |
| `backup-loop.sh` | Runs inside the `backup` service; calls `task-server backup --name` on a timer and copies the result off-host. |
| `network/configure-firewall.sh` | Default-deny `ufw` policy for the public interface. |
| `network/verify-no-public-listener.sh` | Proof command: no API/health/management port is reachable off the WireGuard interface. |
| `wireguard/*.conf.template` | One template per peer (`task-server-01`, `agent-runner-01`, `windows-studio`). |

Task Server and Engine reach each other only on the Compose-internal
`control-plane` network; neither publishes a host port. The `edge` service is
the only one with a published port, and it is bound to the WireGuard address
only (`${WG_ADDRESS}:443:443`, never a bare `443:443`, which would default to
the wildcard address).

## Install

```bash
sudo deploy/release/agent-orchestrator/install-docker.sh
```

or through the guided installer, which also mints and writes the Studio,
Engine, and bootstrap Runner credentials:

```bash
sudo ./agent-orchestrator-setup --mode control-plane --target docker \
    --server-url task-server-01.wg.internal \
    --wg-address 10.60.0.1 \
    --offhost-backup-path /mnt/agent-orchestrator-offhost-backup
```

Both paths:

1. Install Docker Engine and the Compose plugin (apt-based hosts only;
   install manually first on any other distribution).
2. Create the `agent-orchestrator` service user (Docker group member) and
   `/etc/agent-orchestrator/{docker.env,secrets/}`.
3. Copy `deploy/compose/control-plane/` to
   `/opt/agent-orchestrator/compose/`.
4. Generate `studio.token`, `engine.token`, and `runner.token` (mode `0600`)
   under the configured secrets directory and mount them as Docker secrets;
   the containers never receive a credential as a plain environment value
   except the Engine, which reads its secret file into `CLIENT_CREDENTIAL` at
   container start because the Engine binary only accepts that variable
   directly.
5. `docker compose pull && docker compose up -d`, then block until every
   service reports `healthy` and confirm `task-server`'s own `/healthz` from
   inside its container (this does not depend on WireGuard being configured
   yet).

The off-host backup destination
(`CONTROL_PLANE_OFFHOST_BACKUP_PATH`) must already be a mounted remote
filesystem before this step; install fails fast with a clear message
otherwise. Mounting it (sshfs, rclone, NFS, or equivalent) is host-level
configuration outside Compose's scope.

## Update and rollback

```bash
sudo deploy/release/agent-orchestrator/update-docker.sh <image-tag>
sudo deploy/release/agent-orchestrator/rollback-docker.sh [image-tag]
```

Both follow the same drain-before-switch contract as the systemd target
([task-server.md](./task-server.md)): enter `Draining`, poll
`prepare-shutdown` until `safeToStop`, switch the `CONTROL_PLANE_VERSION`
image tag in `docker.env`, `docker compose up -d`, and require every service
to report `healthy` before returning to `Normal`. A candidate that never
becomes healthy is automatically rolled back to the previous tag and the
command exits non-zero; no operator step is required to recover. `rollback.sh`
without an explicit tag replays `CONTROL_PLANE_PREVIOUS_VERSION`, the tag the
last successful update recorded.

Management-API calls (`mode`, `prepare-shutdown`) run through
`docker compose exec task-server curl ... 127.0.0.1:5071`, authenticated with
the Studio credential file already mounted into the container. The host
scripts never need a route to the WireGuard-only edge themselves.

## Backup and restore

The `backup` service shares the `task-server` image and calls the same
`task-server backup --name <name>` command the systemd timer uses
([task-server.md, "Backup and restore rehearsal"](./task-server.md)): a
consistent SQLite snapshot, an integrity check, an audit record, and a
SHA-256 in the JSON result. It runs every `BACKUP_INTERVAL_SECONDS` (default
300, matching the plan's five-minute maximum recovery point) and copies the
verified archive to the off-host mount. Inspect its log:

```bash
docker compose --project-directory /opt/agent-orchestrator/compose \
    --env-file /etc/agent-orchestrator/docker.env logs backup --tail 50
```

Restore follows the systemd runbook exactly, run inside the `task-server`
container: drain, resolve every attempt, enter `Maintenance`, then
`POST /api/v1/management/restore` with the backup id and, first, a
`verifyOnly:true` call. See
[task-server.md, "Backup and restore rehearsal"](./task-server.md) for the
full request/response contract; only the transport (compose `exec` instead of
a bare `curl` on the host) differs.

## WireGuard

Three peers, one hub (`task-server-01`) and two clients (`agent-runner-01`,
Robert's Windows device), each with a single-purpose route
(`AllowedIPs` scoped to exactly one address, no `0.0.0.0/0`, no IP
forwarding on the hub):

```bash
wg genkey | tee task-server-01.key | wg pubkey > task-server-01.pub
wg genkey | tee agent-runner-01.key | wg pubkey > agent-runner-01.pub
wg genkey | tee windows-studio.key | wg pubkey > windows-studio.pub
```

Exchange public keys out of band (not through task text or chat), then fill
[`wireguard/task-server-01.conf.template`](../../../deploy/compose/control-plane/wireguard/task-server-01.conf.template),
[`wireguard/agent-runner-01.conf.template`](../../../deploy/compose/control-plane/wireguard/agent-runner-01.conf.template),
and
[`wireguard/windows-studio.conf.template`](../../../deploy/compose/control-plane/wireguard/windows-studio.conf.template)
with the matching keys and the public endpoint address. On each Linux peer:

```bash
install -m 0600 <peer>.conf /etc/wireguard/wg0.conf
systemctl enable --now wg-quick@wg0
wg show
```

Import the Windows template into the WireGuard for Windows app. Confirm a
handshake (`wg show` reports a recent `latest handshake`) before relying on
the tunnel.

## Firewall and the no-public-listener proof

Run after WireGuard is up and before opening the control plane to normal
admission:

```bash
sudo WG_INTERFACE=wg0 deploy/compose/control-plane/network/configure-firewall.sh
sudo deploy/compose/control-plane/network/verify-no-public-listener.sh
```

`configure-firewall.sh` resets `ufw` to default-deny inbound, then allows SSH,
the WireGuard handshake (UDP 51820), and TCP 443 only on `wg0`. No rule
permits 80, 443, 5030, 5031, or 5071 on the public interface.
`verify-no-public-listener.sh` checks every listening socket for those ports
with `ss` and fails if any is bound outside loopback or `wg0`; pass a public
hostname or IP as its first argument, run from a separate machine (not
`task-server-01` itself), to also prove a real connection from the public
address is refused. Both the local socket-binding proof and the external
connection proof are required before Phase B is called complete
([remote-task-server-local-studio.md, "Release gates"](../remote-task-server-local-studio.md#release-gates)).

## Runner side

Point `agent-runner-01` at the WireGuard origin
([runner.env.template](../../../deploy/release/agent-host/runner.env.template)):

```ini
RUNNER_SERVER_URL=https://task-server-01.wg.internal
RUNNER_AUTH_TOKEN_FILE=/etc/agent-host/runner.token
RUNNER_TLS_CERTIFICATE_SHA256=<sha256 of the edge's leaf certificate>
```

Get the leaf's SHA-256 with:

```bash
openssl s_client -connect task-server-01.wg.internal:443 </dev/null 2>/dev/null \
    | openssl x509 -noout -fingerprint -sha256 \
    | cut -d= -f2 | tr -d ':' | tr 'A-F' 'a-f'
```

`RUNNER_TLS_CERTIFICATE_SHA256` pins the self-signed (or private-CA) leaf so
the Runner does not need the operating-system trust store to reach a private
address. The reverse tunnel documented in
[remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md)
stays installed but disabled once this switch is verified; it is the
documented fallback, not a route kept live in parallel.

## Verify

1. `docker compose ... ps` reports every service `healthy`.
2. From `agent-runner-01`, over WireGuard:
   `curl --cacert <pinned-or-trusted> https://task-server-01.wg.internal/healthz`
   returns `200`.
3. `verify-no-public-listener.sh <task-server-01-public-ip>` reports `OK` for
   both the socket-binding and the external connection proof.
4. A 401/403 matrix: an unauthenticated request to `/api/v1/*` returns `401`;
   a valid Runner bearer against a Studio or management route returns `403`;
   `X-Client-Id` without a bearer returns `401`
   ([remote-task-server-local-studio.md, "Authentication and authorization design"](../remote-task-server-local-studio.md#authentication-and-authorization-design)).
5. `agent-runner-01` claims and reports one seeded task through the WireGuard
   origin with the reverse tunnel disabled.
6. `docker compose restart orchestrator-engine` recovers without disturbing
   `task-server`'s active leases.

The CI topology test
([`.github/workflows/control-plane-topology.yml`](../../../.github/workflows/control-plane-topology.yml))
runs steps 1 to 4 and 6 against a self-signed edge on every push to `main`
and on demand; it does not have a WireGuard interface or a second host, so it
proves the edge and firewall-adjacent contract with a loopback-bound edge and
a plain socket check instead of the two-host WireGuard proof.

## Related documents

- [Remote Task Server with local Agent Studio](../remote-task-server-local-studio.md)
- [Task Server deployment and recovery (systemd target)](./task-server.md)
- [Remote runner persistent connection (tunnel fallback)](./remote-runner-persistent-connection.md)
- [Release, installation, update, and rollback](../releases.md)
