# Operations structure: observed hosts and the placement file

Operations / Structure shows what runs in an installation. Since AGT-3954 it
needs no declaration to show the running hosts: every agent host that reports
(coding, review and gate roles) becomes a node, and the Studio server shows as
a machine with its API, web frontend, edge proxy and Orchestrator Engine. A
placement file can add what no host reports (the physical machine under a VM,
external services, declared links) and give observed hosts their names.

The response shape is the contract in
[operations-structure.md](../../system/contracts/operations-structure.md).

## What shows without configuration

| Source | Becomes |
|---|---|
| Agent host capability report (`HostId`, roles) | `machine:{hostId}` with one `runner:{runnerId}` child per role, marked *Not declared* until a placement file declares the same id. |
| The host's machine report (kind, container name, parent id, kernel) | Nesting: container under its VM or physical machine. |
| Pull gate heartbeat without a capability report | `machine:{gateHostId}` with a `gate:{gateHostId}` worker. |
| The API process itself | `machine:{server}` with `component:{server}:api`; `engine` from the engine readiness contact; `edge`, `web` and `external:cdn` once requests arrive through them. |
| A host's container inventory (`docker ps`) | `observed-container:{hostId}:{name}`; the API's own Compose stack joins the Studio components instead. |
| Host telemetry, capability probes, link supervisor, gate heartbeats | Links (`Task Server route`, `Runner link`, `Gate pull`, `Model calls`, `Git`) and the facts on every node. |

Older hosts that send no machine report still show, flat under the
installation.

### How nesting is decided

1. `RUNNER_PARENT_MACHINE_ID` on the host names its parent machine. The parent
   node uses that id; a placement file that declares the same id gives it a
   name, kind and its own parent.
2. Without a parent id, a container nests under the reporting machine that
   runs the same kernel (containers share the kernel boot id with their
   machine). Containers on a kernel no machine reports from get a
   `machine:kernel-{hash}` node; its id changes when that machine reboots, so
   set `RUNNER_PARENT_MACHINE_ID` for a stable id.
3. Otherwise the host sits directly in the installation.

The Studio server's id is `Structure:ServerMachineId` when set, else the id of
a reporting host on the API's kernel (for example the coding and review runner
daemons that run as system services on the same machine), else
`studio-server`.

### Host settings

Set these in the agent host's environment (systemd unit, Compose file). They
are optional; detection fills the kind on its own.

| Variable | Meaning |
|---|---|
| `RUNNER_PARENT_MACHINE_ID` | Id of the machine this host runs in, for example `lab-vm` for a review container on the home-lab VM. |
| `RUNNER_CONTAINER_NAME` | Container name to report when the runtime does not expose one (Docker shows only the short id inside a container). |
| `RUNNER_MACHINE_KIND` | Overrides detection: `container`, `vm` or `physical`. |

Detection reads `systemd-detect-virt`, `/.dockerenv`, `/run/.containerenv`,
`/proc/1/cgroup`, the DMI vendor and the CPU hypervisor flag once at host
start. The `AGENT_HOST_` prefix works as for every other `RUNNER_` variable.

## The placement file

The placement file holds only `machines`, `externals` and `structureLinks`,
with the same schema as those sections of the
[connectivity manifest](connectivity-manifest.md). It is read from its own
setting and **never feeds `ConnectivityPolicy`**: listeners, origins and runner
links stay owned by `Connectivity:ManifestPath` alone, so setting a placement
file on a live instance cannot change connectivity.

| Setting | Meaning |
|---|---|
| `Structure:PlacementPath` | Absolute path of the placement file (environment `Structure__PlacementPath`). |
| `Structure:ServerMachineId` | Optional id of the machine the Studio API runs on. |

A role-named example is
[`deploy/structure/placement.example.json`](../../../deploy/structure/placement.example.json).
Keep the real file with the installation's other host-local configuration,
outside the repository: it names hosts, and host names, domains and addresses
stay out of the repository.

Rules:

- A machine `id` is the agent host's `HostId` (its `RUNNER_HOSTNAME`, by
  default the machine name). A declared machine and an observed host with the
  same id are one node: the declaration names it, the report observes it.
- A declared machine without a report stays *Not observed*.
- Component ids `api`, `web`, `edge` and `engine` under the Studio server's
  machine id receive the Studio's own observations.
- `structureLinks` name both ends by machine id or by node id
  (`component:<machine>:<id>`, `external:<id>`); a link whose end never
  reports and is not declared is dropped.
- When the connectivity manifest also has placement sections, both are used;
  the placement file wins for equal ids.
- An unreadable or invalid file does not hide the structure: observed nodes
  still show and the response lists the problem under `findings`.

## Live updates

The structure is a cached snapshot rebuilt every 5 seconds in the background;
requests never read host facts. Open views receive `structureChanged` deltas
on the Studio hub (`/hubs/v1/studio`) after calling `SubscribeToStructure`,
which requires the same permission as the read. The 15-second poll remains the
fallback, and `GET /api/v1/operations/structure` answers `304` to a matching
`If-None-Match`.
