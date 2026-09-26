# Gap: the Connector (D4b) cannot be the root compose's Studio backend

Status: option C selected by the operator on 2026-09-25. Written while working AGT-2736 ("Agent Studio
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

The operator selected option C for the one-box delivery. The distributed
Task Server and Engine are the standard Compose deployment, with `/api/v1`
covered through the Studio API proxy. Connector-equivalent dev-seat route
coverage remains a documented limitation pending the operations topology work.

## What AGT-2736 ships under option C

The root Compose file starts the distributed Task Server, Engine, Studio API
proxy, web UI, and agent host as one stack. A one-shot service creates
principal credentials in a persistent named volume without operator token
handling. The source-built path is the verified install path for this checkout;
the published-image path is checked after release. See
[Docker operations](./docker.md) for the commands and the route coverage limit.
