# Host readiness and capacity baseline

`scripts/host-readiness-baseline.py` produces one JSON export from saved metadata. It runs offline with Python 3 and writes to standard output. It reads only the capture file named on the command line. Run from the repository root.

```sh
python3 scripts/host-readiness-baseline.py capture.json > host-readiness-baseline.json
```

The input is a JSON array of captures. Each capture names the deployed profile that produced the response, its release, the UTC collection time and the response itself. Use stable, non-secret labels for profiles and release IDs. If the producing release is unknown, use `unknown`. Use `isolated-worktree` for local measurements. Omit unavailable captures. An empty array still exports the supplied Dossier and card observations.

```json
[
  {
    "kind": "task-server-placement",
    "profile": "fleet-task-server",
    "release": "unknown",
    "scope": "deployment",
    "capturedAt": "2026-11-28T11:50:00Z",
    "data": [
      {"projectId": "sample-project", "pinnedRunnerId": null, "maxParallelism": 1}
    ]
  }
]
```

| Capture kind | Owning profile and input shape |
|---|---|
| `task-server-roles`, `monolith-roles` | Standalone Task Server or Studio monolith response from `GET /api/v1/management/remote-hosts`. Includes runner and instance IDs, release, advertised readiness, timestamps, capacity and role-reported telemetry. Central desired versions and runner-applied versions retain separate fields. |
| `task-server-quotas` | Standalone Task Server response from `GET /api/v1/quota/readings`. The wire field `hostId` names the runner that reported the reading. One reporter does not establish which other roles use that account. |
| `monolith-quotas` | Studio monolith response from `GET /api/operator/coding-capacity`. Only the cached `subscriptions` projection is used. |
| `task-server-placement` | Array of standalone Task Server `GET /api/v1/projects/{projectId}/placement` response objects. |
| `monolith-placement` | Studio monolith response from `GET /api/projects/settings`. Keeps configured execution location, runner and per-project limit. |
| `task-server-queue` | Standalone Task Server response from `GET /api/v1/studio/runner/queue-starvation`. Its age is measured from task creation for the Ready subset without a recent attempt. |
| `monolith-queue` | Previously saved Studio monolith `GET /api/runner/queue-starvation` response. Its timestamps identify Ready lane entry. |
| `studio-gates` | Studio gate coordinator response from `GET /api/system/gate-capacity`. Its use counters are aggregate. |
| `studio-integration` | Studio pipeline `GET /api/gate-economics` response. Keeps queue counts and the allowlisted stage durations from `recentIntegrations`. |
| `role-profile` | Array of redacted service and topology receipts provided by the owner. Use for measured parent/leaf cgroup limits, explicit physical ancestry, bridge gate profiles, and opaque role/account associations. |
| `phase-samples` | Array of selected pipeline or worktree sample receipts. Retains source SHA, toolchain, cache condition, queue and phase timing fields. Use the profile that recorded the timings. |

Keep each capture scoped to the same installation and collection window when making comparisons. The input and output are each capped at 2 MiB. Every projected array is capped at 128 rows. Select a bounded sample first if a response exceeds the limits and record the selection and total population with the baseline. `null` denotes absent or invalid data, while an empty list denotes a supplied list with no rows. The exporter keeps only named metadata fields and excludes free-form diagnostics and credential fields. Supply metadata receipts only. Keep authorization headers, service environments, login files, credential material and raw build or provider output outside the capture.

## Physical mapping

A runner row retains the source's placement kind, ancestry, direct parent, and registration and instance IDs. Known physical ancestry or a reported bare-metal machine name can map the runner. A VM or a container ID alone leaves its physical parent unresolved. Container inventories can come from a remote runtime and require their own exact-ID evidence.

For an owner topology receipt, set the capture's `profile` to the source of the owner record. Each item can name the `subjectProfile` of the role response, exact `runnerId` and `instanceId`, and/or exact `containerId`. Supply `physicalMachineId` and optionally `vmId`. Use a consistent physical-machine identifier across the source profiles. Conflicting physical IDs are reported as conflicting. A role-profile row may also stand alone for a service absent from the runner API, for example an SSH gate. Supply its role, release and topology explicitly.

The same role-profile row can carry numeric `workerCpuQuotaPercent`, `workerMemoryMaxBytes`, `physicalMemoryBytes`, `parentMemoryMaxBytes`, `leafMemoryMaxBytes`, `loadPerCoreThreshold` and `loadSustainSeconds`, Linux `parentCpuMax` and `leafCpuMax` strings, gate transport/slots/processor hint/sharding and an array of `accountBindings`. Each binding contains `provider`, a 64-character lowercase hex `subscriptionKey` from approved identity metadata, `observedAt` and `confidence` (`owner-confirmed`, `observed` or `unknown`). A cached provider reading alone cannot confirm that two roles share an account.

## Matched observations

Use the same source SHA, toolchain, dependency and output cache conditions, build command and concurrency on each profile. Put build and queue durations in milliseconds. Each phase can carry `phase`, `status`, `queuedAt`, `startedAt`, `finishedAt`, `queueDurationMs`, `executionDurationMs` and `durationSource`. Keep production task ages, local fixture wait and integration queue stages labeled with their own clocks and definitions.

An authorized deployment baseline pairs those builds with contemporaneous coding, review and gate queue observations, parent and leaf CPU quota and throttling, memory and I/O pressure, and the active slot counts. Capture source release, role identity and reserve settings from the deployed profile. Use equivalent cache states and record the sample population. A local one-slot build measures that worktree queue. It cannot establish two-host capacity or provider headroom.

Owner authorization and an approved provider budget are required before a live canary. The exporter does not contact a service, issue claims, alter project pins, probe provider credentials or mutate Git.
