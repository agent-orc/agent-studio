# Managed databases for hosted apps (IP-D5 option B)

The owner chose option B for IP-D5 on 16 November 2026: the run zone
provisions a managed database for a hosted app. AGT-3877 delivers
provisioning, credentials, the Bereitstellung view and deletion protection.
Backups, restore and health checks of the database follow in MD-2. The
operator-attached database of option A
([hosted data](../hosted-data/README.md)) stays available.

## Declaring a database

A hosted app declares its databases in `.agent-studio/project.yml` schema 2:

```yaml
databases:
  - name: orders
    engine: postgres
    version: "16.4"
    size: small
    variable: DATABASE_URL
    environments: [staging, production]
```

| Key | Meaning |
|---|---|
| `name` | Name of the database, its volume and its connection file: a lower-case letter, then lower-case letters, digits or underscores (up to 31). Unique per environment. |
| `engine` | `postgres`. |
| `version` | PostgreSQL major version or major.minor, 14 to 17, such as `16` or `16.4`. |
| `size` | `small` (0.5 CPU, 512 MB, 256 processes), `medium` (1 CPU, 1 GB, 512) or `large` (2 CPUs, 4 GB, 1024). Default `small`. |
| `variable` | Name of the connection string. Default `DATABASE_URL`. Two databases of one environment need different names. |
| `environments` | `staging`, `production`, or both; omitted means every environment. |

Validation lives in `contracts/TaskServer.Contracts/ProjectDatabaseDefinition.cs`
and the JSON schema is
[project-execution.schema.json](../../app/schemas/project-execution.schema.json).
A database may apply only to environments hosted in the run zone, that is
container environments declared with `"destination": "edge"`
([run-target host](../../system/domains/runner.md#run-target-host-app-hosting-ip-1)).
An environment outside the run zone that a database applies to is reported as
unusable; scope the entry with `environments`. When schema 2 project.yml overlays an
edge-hosted declaration, the declaration keeps its own template and gets no
SSH steps, because the run-target host executes and reports its deploy jobs. Secret names starting with
`MANAGED_DB_` are reserved for Studio, and `variables` may not declare the
connection string's name or its `_FILE` name.

## Provisioning on deploy

A deploy job stores the databases with the rest of its approved snapshot, so a
change to project.yml reaches the run host only through a deploy. On the job's
first poll the host provisions each database before it starts the app
revision that uses it:

- One `postgres:<version>-alpine` container per database, named
  `<app key>-db-<hash>`, with the declared CPU, memory and process limits,
  `no-new-privileges`, and `NET_RAW` and `MKNOD` dropped.
- One named Docker volume per database (`<container>-data`). The compose file
  declares it external, so no compose command removes it.
- One internal Docker network per environment (`<app key>-data`). The
  database container joins only this network and publishes no port. The
  environment's app containers join it next to their own network. The edge
  never joins it, and no other environment's app does, so neither public
  traffic nor another environment can reach the database (the zone rules of
  AGT-3231 and AGT-3241).
- Readiness is `pg_isready` over TCP inside the container, so the image's
  initialisation server does not count as ready.

The host reports each database on the observed-state route as `provisioning`,
`running`, `unhealthy`, `failed`, `retained`, `upgrade-refused` or `deleted`,
with its version and the size of its data directory, measured at most every
five minutes.

## Credentials

Studio generates a 32-byte password for each database before it answers the
host's poll. It stores the value through the secret provider (AGT-3222) as
`secret://<project>/<environment>/MANAGED_DB_<NAME>_PASSWORD` and binds that
reference to the environment (AGT-3246), recorded as
`studio:managed-database`. No person chose or saw the value, so a production
binding needs no separate approver.

The host resolves the reference with
`POST /api/runner/secrets/resolve` and the workload
`{ "kind": "run-target", "project", "environmentId" }`. Studio issues it only
to a credential with `runner.deploy` whose Runner ID is the environment's
`targetHostId`, and only for the databases of the open or running deploy of
that environment. The value exists on the host in the agent's memory and in
tmpfs files under `RUN_TARGET_SECRET_RUNTIME_DIR`:

- the database container reads `/run/secrets/password` through
  `POSTGRES_PASSWORD_FILE`;
- the app container gets `<variable>_FILE`, for example `DATABASE_URL_FILE`,
  the path of a file that holds
  `postgresql://app:<password>@<container>:5432/<name>`.

Like every run-zone secret, the connection string never enters a compose
file, Docker's container configuration, host state, Studio's state, logs or
the UI; host diagnostics are masked. project.yml and the Bereitstellung view
show only the reference. The agent keeps an issued value in memory, so a
crashed database or app restarts while Studio is unreachable. After an agent
restart, a restart that needs the value waits until Studio answers again.

## Upgrades

A changed minor version or size is a deploy step: the host recreates the
container on the same volume and checks readiness again. A changed major
version needs a dump and restore, which a later card adds. Studio refuses
such a deploy before the host sees it, using the version the host reports;
the job fails with `deploy-step-failed` and the database shows the message.
The host refuses it as well and leaves the data untouched.

## Removal and deletion

Removing a database from project.yml deletes nothing. After the next deploy
is live without it, the host stops the database container and keeps the
volume; Studio shows the database as retained. When an environment leaves
the host, its database containers and data network go and the volumes stay.

Deleting the data is a separate owner action in the Bereitstellung area
(Deploy, Operations). Only a retained database offers it, and the owner types
the database name to confirm. Studio accepts the request only when neither
the running deploy nor an open one declares the database. The host then
removes the container and the volume and confirms with `deleted`; Studio
removes the generated credential and its binding afterwards. A host never
deletes a database that its desired state still declares.

## Studio HTTP contract

| Method and path | Request | Response |
|---|---|---|
| `GET /api/projects/{project}/environments/{environmentId}/databases` | None | `200` with `databases`: `name`, `engine`, `version` (approved), `runningVersion`, `size`, `resources`, `variable`, `credentialReference`, `state`, `declared`, `volumeBytes`, `observedAt`, `message`, `deletionRequestedBy`, `deletionRequestedAt`, `deletedAt`. `404` for an undeclared environment. |
| `POST /api/projects/{project}/environments/{environmentId}/databases/{name}/deletion` | `{ "confirm": "<name>" }` | `202` when requested. `403` without a signed-in Owner, `400` when `confirm` differs from the name, `404` for an unknown database, `409` while a deploy declares it or when the deletion is already requested or done. |

The view's `state` adds `pending-deploy` (declared, no deploy yet),
`retained` and `deleting` to the host states. The public demo execution
profile denies the deletion.
