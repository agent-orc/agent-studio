# Structure: observed hosts, placement file and facts

Operations / Structure (`#/workspace/operations/structure`) shows what runs. It
needs no declaration: every host that reports becomes a node, and the Studio
server shows with its components. A placement file adds names, locations,
externals and links. It is separate from the
[connectivity manifest](./connectivity-manifest.md) and cannot change
connectivity.

Source: `backend/Features/Management/Connectivity/` (`StructureProjection`,
`ObservedStructureProjection`, `StructureSnapshotService`,
`StructurePlacement`, `StudioSelfObserver`, `StructureDataLocations`).

## What shows without any declaration

| Node | Id | Source |
|---|---|---|
| Reporting host (coding, review, gate) | `machine:{hostId}` | The host's capability advertisement, or the gate host registration |
| Runner role on a host | `component:{hostId}:{runnerId}` | The same advertisement; `role` is `coding`, `review` or `gate` |
| Enclosing machine a host names | `machine:{id}` from `RUNNER_PARENT_MACHINE` | Host placement report |
| Studio server | `machine:{Structure:MachineId}`, default `machine:studio-server` | What the API observes about its own machine |
| API, web, edge, engine | `component:{machineId}:api`, `:web`, `:edge`, `:engine` | The API itself, engine readiness, forwarded requests, and a host container report |
| Container a host's container runtime lists | `observed-container:{hostId}:{name}` | Host container report |
| Control connection of a host | `link:machine:{hostId}>component:{machineId}:api:control` | Host telemetry |
| Supervised runner link | `link:machine:{machineId}>machine:{hostId}:{kind}` | `RunnerLinks` state |

A node that reports and is not declared has `provenance: "observed"` and the
view marks it "not declared". Nothing that reports is hidden: a host that
stopped reporting stays as `stale` with its last seen time.

Ids come from the reports and the configuration, never from a boot id, a
process id or an order. They stay the same across reports and restarts, so
assessments and the canvas layers can attach to nodes, links and data
locations by id.

## Machine kind and nesting from the hosts

Each agent host detects the kind of place it runs in and reports it in its
telemetry (`placement`): `container`, `vm`, `physical` or `unknown`. It reads
the container markers (`/.dockerenv`, `/run/.containerenv`, the cgroup of
process 1), asks `systemd-detect-virt` where the host has it, and otherwise
reads the DMI identity and the `hypervisor` processor flag.

Two optional runner settings add what a host cannot detect:

| Environment variable | CLI flag | Meaning |
|---|---|---|
| `RUNNER_PARENT_MACHINE` | `--parent-machine` | Enclosing machines as a slash-separated path, outermost first, for example `home-lab/linux-vm`. A segment may name its kind: `home-lab=physical/linux-vm=vm`. Without a kind the outermost machine is physical and inner ones are virtual machines. |
| `RUNNER_CONTAINER_NAME` | `--container-name` | Name of the container the daemon runs in. It labels the node and matches the container report of the machine. |

With `RUNNER_PARENT_MACHINE=home-lab/linux-vm` on a review host container the
view nests container in virtual machine in physical machine without a
manifest. A host without these settings shows with its detected kind directly
in the installation. A host on an older release reports no placement and
still shows, flat, as a host.

A daemon that runs on the Studio server itself (same kernel as the API, not in
a container) shows as an agent host inside the Studio server node, and the
containers it lists belong to that machine. The container whose id is the
API's host name is the API. Web, edge and engine match the last word of the
container name (`frontend` or `web`, `edge` or `caddy`, `engine`) within the
API container's name prefix. A container that matches nothing shows as it is,
for example a leftover stage container of a deploy run.

## Placement file

Set `Structure:PlacementPath` to a JSON file with `machines`, `externals` and
`structureLinks`. The schema of the three lists is the one the connectivity
manifest uses ([field table](./connectivity-manifest.md)). Any other key is
ignored.

- The file is read by the structure projection only. `ConnectivityPolicy`,
  listeners and `RunnerLinks` never see it; setting or editing it does not
  change connectivity and needs no restart.
- Declared and observed nodes join by id: a machine with `"id": "review-1"`
  is the node of the host that reports as `review-1`. The declaration gives
  name, kind, location and parent; the report gives state and facts.
- A declared machine that does not report stays "not observed".
- A link may end at a host that only reports.
- When a connectivity manifest also declares machines, the placement file
  wins per id and the manifest keeps what the file does not name.
- An unreadable or invalid file makes the view answer 503 with the reason
  instead of silently dropping the declarations.

Role-named example: [`deploy/connectivity/structure-placement.example.json`](../../../deploy/connectivity/structure-placement.example.json).
Host names, domains and addresses of an installation belong in the file on
that installation, not in a repository.

| Setting | Default | Meaning |
|---|---|---|
| `Structure:PlacementPath` | unset | Path of the placement file. |
| `Structure:MachineId` | `studio-server` | Id of the Studio server node. Use the id the placement file declares for it. |
| `Structure:MachineName` | `Studio server` | Label of the Studio server node when nothing declares it. |
| `Structure:MachineKind` | detected | Kind of the Studio server node, for example `cloud-server`. |
| `Structure:EdgeCertificatesPath` | unset | Directory of the edge certificates, when the API can read it. |

## Facts per node and link

Facts are values, not judgements. Ranking and wording come later from
assessments. Every member is optional; a host that does not report a value
omits it.

| Field of `facts` | Content |
|---|---|
| `hostId`, `machineKind`, `virtualization`, `containerName`, `roles`, `lastSeenAt` | Identity and placement as reported |
| `performance` | `cpuPercent`, `cpuCores`, `load1`, `load5`, `load15`, `memoryUsedBytes`, `memoryTotalBytes`, `disks[]` (`mount`, `purpose`, `usedBytes`, `freeBytes`, `totalBytes`), `networkReceiveBytesPerSecond`, `networkTransmitBytesPerSecond`, `observedAt` |
| `capacity` | `busySlots`, `freeSlots`, `totalSlots`, `runningCards[]` |
| `release` | `version`, `stableVersion`, `state` (`current`, `behind`, `differs`, `unknown`), `behind`, `pendingInstall` |
| `dataLocations[]` | On the Studio server: `id` (`data:{machineId}:{role}`), `role`, `label`, `sourceRef`, `exists`, `sizeBytes`, `sizeComplete`, `measuredAt`, `backupCovered`, `gitRemoteCovered`, `coverage` |
| `warnings` | `tools[]` (`name`, `kind`, `version`, `targetVersion`, `state`), `missingRequired[]`, `behindRequired[]`, `releaseBehind`, `pendingHostInstall`, `linksDown[]`, `blockedCards[]` (`key`, `state`, `reason`) |

Data location roles: `workspace-repository` (`TaskRepository`),
`project-checkouts` (project registry), `application-state` (local state
root), `secrets-key` (`Secrets:KeyFile`) and `edge-certificates`
(`Structure:EdgeCertificatesPath`). The absolute path never leaves the host;
`sourceRef` names the setting. `backupCovered` is true when backups are
configured and a source of `/api/operator/backups` contains the location.
`gitRemoteCovered` is true when the location is a git checkout with a remote
(for project checkouts: every checkout). Sizes come from a bounded walk at
most every 15 minutes; `sizeComplete` is false when the walk stopped early.

`blockedCards[].state` is `refused` when a claim was refused because the role
never offered the capability in `reason`, and `parked` when the capability is
held back for now (stale, not ready, probe undecided).

Links carry `id` (`link:{from}>{to}:{kind}`), `kind` and `facts`: `state`
(`up`, `down`, `stale`, `not observed`), `latencyMs` where the link is probed
(the round trip of the host's capability advertisement), `probedAt` and
`transport`.

Host telemetry gained `placement`, `networkReceiveBytesPerSecond`,
`networkTransmitBytesPerSecond` and `taskServerRoundTripMs`, and each listed
container its short `id`. All are optional on the wire.

## Reading and live updates

`GET /api/v1/operations/structure?scope=workspace|project:{id}&environment={id}`
returns `nodes`, `links`, `revision`, `generatedAt` and `schemaVersion` (2).
Fields are only ever added; existing fields keep their meaning.

- The projection is cached per scope and refreshed every 5 seconds in the
  background while a view reads or subscribes. A request reads the cache. It
  never rebuilds the projection per client and never asks a host: host
  reports, the declared part (at most every 30 seconds) and data locations
  are read on the service's own cadence.
- `revision` is the entity tag. A read with `If-None-Match: "{revision}"`
  answers 304 when nothing changed.
- The Studio hub pushes `structureDelta` to views that called
  `SubscribeToStructure(scope, environment)` (`UnsubscribeFromStructure` to
  leave). A delta has `scope`, `environment`, `baseRevision`, `revision`,
  `generatedAt`, `nodes`, `removedNodes`, `links` and `removedLinks`, and
  holds only what changed. A view whose revision differs from `baseRevision`
  reads the structure again.
- The view applies fact updates in place, reads again when nodes or links
  come or go, converges every 15 seconds with its entity tag, and reads every
  5 seconds while the hub is down.

Observed hosts show in the workspace picture. A project or environment excerpt
stays declaration driven.
