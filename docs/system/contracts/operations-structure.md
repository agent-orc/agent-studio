# Operations structure response and delta contract

`GET /api/v1/operations/structure[?scope=workspace|project:{id}][&environment={id}]`
returns one snapshot of the installation structure (AGT-3389, AGT-3954). The
shape below is stable: the structure canvas, its performance, storage and
backup layers, and the LLM-driven assessments build on it by id. Fields are
added, never renamed or repurposed; every new field is optional.

Setup and the placement file: [structure-placement.md](../../operations/setup/structure-placement.md).

## Response

```text
StructureResponse { nodes[], links[], revision, generatedAt, findings[]? }
```

| Field | Meaning |
|---|---|
| `revision` | Snapshot revision. Monotonic across restarts (seeded from the clock). The `ETag` carries it. |
| `generatedAt` | When the facts behind the snapshot were collected. |
| `findings` | Placement file problems (`code`, `subject`, `message`, `remediation`). Observed nodes still show. |

### Node

| Field | Meaning |
|---|---|
| `id` | Stable id; see [Ids](#ids). |
| `kind` | `boundary`, `location`, `physical`, `vm`, `cloud-server`, `host`, `container`, `process`, `external`, `managed`, `environment`, ... (open vocabulary). |
| `label`, `parentId`, `role` | Display name, nesting, function group. |
| `provenance` | `declared` (manifest, placement file, project declarations) or `observed` (only a report proves it). |
| `sourceRef` | Where the node comes from, for example `Structure:PlacementPath#machines/lab-vm` or `host capability report`. |
| `observation` | `difference` (`matched`, `declared-only`, `undeclared`, `differs`, `stale`, `unreachable-by-design`), `health`, `observedAt`, `version`, `expectedVersion`, `behind`, `cpuPercent`, `memoryUsedBytes`, `memoryTotalBytes`, `activeGates`, `detail`. |
| `facts` | Facts only, see below. Absent when nothing reports any. |

`undeclared` marks a node that reports but that nothing declares: the view
shows it as *Not declared*.

### Node facts

| Field | Meaning |
|---|---|
| `hostId` | The reporting host id. |
| `machine` | `kind` (`container`, `vm`, `physical`, `unknown`), `virtualization`, `containerRuntime`, `containerName`, `reportedParentId`, `source`. |
| `roles[]` | Per runner identity: `runnerId`, `role` (`coding`, `review`, `gate`, `integration`), `status`, `version`, `lastSeenAt`, `busySlots`, `freeSlots`, `runningCards[]`. |
| `cpuCores`, `cpuPercent`, `load1`, `load5`, `load15` | Kernel-wide CPU facts; fresh values only (90 s). |
| `memoryUsedBytes`, `memoryTotalBytes` | Machine memory. |
| `mounts[]` | `purpose`, `mount`, `usedBytes`, `freeBytes`, `totalBytes` per file system the host measured. |
| `networkReceiveBytesPerSecond`, `networkTransmitBytesPerSecond` | Host interfaces without loopback, bridges and veth pairs. |
| `busySlots`, `freeSlots`, `runningCards[]` | Slots across roles and gate workers; task keys under an unexpired lease. |
| `version`, `stableVersion`, `releaseState`, `releaseDetail` | Host release against Stable (`current`, `behind`, `differs`, `unknown`). |
| `pendingHostUpdate` | A host CLI update that has not finished, as `state[: detail]`. |
| `lastSeenAt`, `telemetryObservedAt`, `admissionState` | Report freshness and claim admission. |
| `tools[]` | Toolchains and CLIs the host reports: `key`, `name`, `status`, `version`, `targetVersion`, `behind`. |
| `requirements[]` | Required keys the capability and readiness checks find unmet: `key`, `state` (`missing`, `not-ready`, `held`, `indeterminate`, `stale`, `behind`), `detail`, `waitingCards[]`. |
| `linksDown[]` | Ids of links at this node whose health is `down`. |
| `heldCards[]` | Cards waiting on this host's held capabilities or refused by its claims. |
| `dataLocations[]` | State the machine keeps, see below. Studio server only today. |
| `imageDigest`, `containerState` | Container nodes. |

### Data locations

`{ id, role, label, sizeBytes, backupCovered, gitRemoteCovered, measuredAt, detail }`.
`id` is `data:{machineId}:{role}`. Roles: `workspace-repository`,
`project-checkouts`, `application-state`, `secrets-key`, `edge-certificates`,
and other backup source names. `backupCovered` is true when a configured
`/api/operator/backups` source contains the location; the secrets key is
excluded from archives by design. `gitRemoteCovered` is true when every
checkout there has a remote. Paths are not part of the response. Sizes refresh
every 10 minutes.

### Link

| Field | Meaning |
|---|---|
| `id` | `link:{from}|{to}|{kind}`, with `#n` for a repeated triple. |
| `from`, `to`, `kind`, `label`, `sourceRef` | Ends, `call`, `tunnel`, `ssh`, `artifact`, ... |
| `observation` | As for nodes; `difference` is `observed` for a link only reports prove. |
| `facts` | `state`, `latencyMs` (probe round trip where probed), `probedAt`, `transport`. |

## Ids

| Id | Stable because |
|---|---|
| `machine:{hostId}` | Host id from the report (`RUNNER_HOSTNAME`) or the placement file. |
| `machine:{parentMachineId}` | `RUNNER_PARENT_MACHINE_ID` or a placement file machine. |
| `machine:kernel-{hash}` | Only until that machine reboots; set a parent id for stability. |
| `runner:{runnerId}`, `gate:{hostId}` | Registered runner identity, gate host id. |
| `component:{machineId}:{api|web|edge|engine}` | Studio server components. |
| `observed-container:{hostId}:{name}` | Container name on the host. |
| `external:cdn`, `external:git-remote`, `external:model-provider:{provider}` | Observed externals. |
| `data:{machineId}:{role}` | Data location role on its machine. |

## Delta

Clients call `SubscribeToStructure` on `/hubs/v1/studio` (same permission as
the read; `UnsubscribeFromStructure` leaves). The server sends
`structureChanged` with:

```text
StructureDelta { fromRevision, revision, generatedAt, upsertNodes[], removeNodes[], upsertLinks[], removeLinks[], findings[]? }
```

A client applies a delta only when its snapshot revision equals
`fromRevision`; otherwise, and for scoped reads (`project:` scope or
`environment`), it reads the snapshot again. Upserted entries replace the entry
with the same id in place; new ones append. Deltas describe the workspace
snapshot.
