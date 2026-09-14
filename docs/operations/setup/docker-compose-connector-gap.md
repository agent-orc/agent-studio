# Gap: the Connector (D4b) cannot be the root compose's Studio backend

Status: open, needs a decision. Written while working AGT-2736 ("Agent Studio
Docker-deployable: one compose file runs the complete product from published
images"). Not itself a decision dossier; it exists so the next card does not
re-derive this from scratch.

## The ask

AGT-2736 asks for one root `docker-compose.yml` where `task-server`,
`orchestrator-engine`, `connector` (the AGT-2754 "OrchestratorApi connector
profile", tracked elsewhere as D4b), and `web` together are the standard,
LAN-exposable, all-in-Docker product deployment, replacing today's `distributed`
profile combination of `task-server` + `orchestrator-engine` + `studio-bff` +
`orchestrator-api` (proxy mode).

## Why the Connector cannot fill that role as built

The Connector's design is deliberately, explicitly scoped to one loopback
process next to the browser on a single operator's machine - see
[Studio route ownership and local connector](../../studio-route-ownership/index.html)
(AGT-2731, status **Decided**) and
[Remote Task Server with local Agent Studio](../remote-task-server-local-studio.md).
It is not a general Docker Compose network service:

- `ConnectorOptions.Load` (`backend/Features/Connector/ConnectorOptions.cs:27-36`)
  throws unless `Connector:Authority` equals exactly `[::1]:5031` and
  `Connector:StudioOrigin` equals exactly `http://[::1]:4011` - both are
  effectively constants, not configuration.
- The route-ownership dossier's own Security section is explicit: "bind only
  `[::1]:5031`... Do not bind IPv4 wildcard, IPv6 wildcard, LAN, or Docker
  Desktop default interfaces," and unsafe requests are rejected unless their
  `Origin` is exactly the one configured Studio origin.

A browser reaching `web` at anything other than literally `http://[::1]:4011`
(a LAN address, `127.0.0.1`, a hostname, or an `edge` TLS front door) sends a
different `Origin`, which the Connector rejects by design. This is a
deliberate CSRF boundary, not an oversight, and AGT-2736's own acceptance
criterion ("exposing to a LAN is an explicit opt-in variable") is exactly the
case it is built to refuse.

`studio-bff`, by contrast, already runs as an ordinary compose-network
service today (`docker-compose.yml`'s `distributed` profile) but only forwards
`/api/v1` and `/hubs` - see "Alternatives" in the route-ownership dossier,
which rejected extending it specifically because it would need to import the
95 local dev-seat handlers (file, Git, CLI-probe, launcher) that the Connector
already carries.

## What this blocks in AGT-2736

Item 1 of the original card ("task-server, orchestrator-engine, connector,
web" as the one compose product) and item 7 ("distributed stack is the
standard deployment", retiring `setup/DemoInstaller.cs`) both assume a
Studio backend that is simultaneously (a) reachable from other containers or
a LAN client and (b) has the Connector's full route coverage. No existing
component is both today.

## Options for the next card

1. **Make the Connector's authority/origin configurable for a `docker`
   deployment**, replacing the hardcoded loopback constants with a validated
   allowlist (still rejecting drive-by origins), and prove the CSRF/session
   model holds for a LAN-exposed origin. Security-sensitive; needs explicit
   sign-off since it loosens a Decided boundary.
2. **Grow `studio-bff` to Connector-equivalent route coverage** (the 95
   dev-seat operations plus the WebSocket/session/CSRF handling the Connector
   already has), keeping the Connector unchanged for Robert's own Windows
   setup. Larger, non-trivial backend scope, but it does not touch a Decided
   security boundary.
3. **Scope AGT-2736 down**: ship the one-box product on today's `distributed`
   profile (`task-server` + `orchestrator-engine` + `studio-bff` +
   `orchestrator-api` proxy) as-is, accept its `/api/v1`-only coverage as a
   known limitation, and defer full route parity to a later card.

This note takes no position between the three; it only establishes that one
of them is required before item 1 and item 7 can be implemented.

## What AGT-2736 shipped instead

Scoped to what does not depend on this decision:

- Fixed a real regression where a bare `docker compose up` (no profile, no
  `.env`) failed unconditionally interpolating `DISTRIBUTED_ENGINE_TOKEN`,
  even though `orchestrator-engine` is gated behind the `distributed`/`dev`
  profiles and was not being started.
- Added `scripts/compose-distributed-bootstrap.sh`, which mints the three
  distributed-profile principal tokens into `.env` on first run (never
  overwriting an existing value), removing the manual-token-generation step
  for that profile.
- Verified the existing `scripts/compose-runner-bootstrap.sh` /
  `runner.env.template` pair (already shipped) still makes
  `docker compose --profile runner` usable from a fresh clone, and extended
  `scripts/compose-smoke-test.sh` with a fourth scenario that exercises it in
  CI.
- Documented both bootstrap scripts in
  [getting-started.md](./getting-started.md).
