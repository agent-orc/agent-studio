# Secret provider

The secret provider implements Dossier
[integrierte-plattform](../integrierte-plattform/index.html) §9, sections 7.2
to 7.4, as card IP-6 (AGT-3222). Every part of the platform uses one interface,
`ISecretProvider` (`backend/Features/Secrets/`). A storage adapter,
`ISecretStore`, sits behind it. The built-in store is the default. IP-9
(AGT-3249) adds scope-specific Vault/OpenBao KV v2 and 1Password Connect
adapters without changing callers. Cloud secret managers remain future work.

Recorded options: IP-D7 A (built-in store plus adapters) and IP-D8 A (write
without read). The operator had not recorded a decision, so the Dossier
recommendation applies. This card has no reveal operation. A later card may add
reveal with its own permission, a reason and an audit entry.

IP-9 implements the same IP-D7 option A: one provider interface, with the
built-in encrypted store as the default and external stores selected for an
individual workspace, project, or environment scope. OIDC sign-in belongs to
AGT-3228 and its follow-up, not IP-9 storage adapters.

## External stores and selection

Settings > Access > Secrets chooses storage for the currently selected scope.
Only a person with workspace-wide secret write rights may change backend
selection because the selected URL receives installation-held credentials.
The selection is an owner-only installation file at
`<TaskRepository>/.security/secrets/backends.json`; it is not project or
repository configuration. The backend URL, KV mount or 1Password vault ID,
credential source name, and cache TTL are saved there. Credential **values**
come from an environment variable or a `secret://workspace/NAME` reference in
the built-in encrypted store. A referenced credential is always fetched from
the built-in store so routing cannot recurse. Use a separate least-privilege
credential for each backend. The settings page shows each selected backend's
health and reachability, with no credential value in the response.

Vault and OpenBao use the KV v2 HTTP API. Secrets live under the selected
mount at `<scope>/<name>`. The backend's KV version is the `@version` suffix.
The adapter uses compare-and-set writes and KV metadata for native version state.
Each value write also stores its Studio version record in the same KV v2 data
version. A separate `__studio_metadata__` KV path holds later metadata changes
such as last use (KV custom metadata is limited to 512 bytes). If that sidecar
save fails after a value write, the adapter can still read and manage the
written version using the record stored with it. KV delete revokes versions.
A put revokes earlier versions; rotate preserves
them. The adapter requires read, create, update, list and delete rights under
the selected path, including its KV metadata path, plus read access to
`<mount>/config` to verify the KV v2 mount. An absent or incompatible mount
fails closed before an empty scope can be reported. Its token is sent only in
the `X-Vault-Token` header.

The 1Password HTTP adapter uses the documented [Connect API](https://developer.1password.com/docs/connect/connect-api-reference/), one Secure Note
item per secret in the selected vault. It requires a Connect token with access
to that vault, stored in the built-in store or an
environment variable. A direct `OP_SERVICE_ACCOUNT_TOKEN` cannot authenticate
to the Connect HTTP API; direct service account access uses 1Password's SDK
or CLI and is not this transport. Connect exposes the
current item, so put replaces its value and delete removes it. Historical
version reads, rotate and version-specific revoke return a clear unsupported
error. The item title carries `agent-studio/<scope>/<name>` and its notes
carry Studio metadata. A JSON Patch to `notesPlain` persists last-use metadata
without sending the concealed value again. The item revision advances for
this metadata-only change. Value versions therefore follow the Connect item
revision when a value is written and may have gaps; the separate stored item
revision checks for changes outside Studio. Do not edit these managed fields
outside Studio.

External value reads are cached for the selected 1-300 second TTL. A local
write, revoke or delete invalidates its cached values. Backend errors are
reported without response bodies, values or credentials. One unavailable
backend appears as an unreadable item for its scope; other scopes keep
listing. Value operations in that scope fail closed rather than reading the
built-in store. Switching a scope does not migrate existing secrets: copy
them explicitly before changing the selection, and verify the new backend's
health and contents. Audit entries for external operations use the same
append-only audit log as the built-in store, including failures.

## References and scopes

Every value is addressed as `secret://<scope>/<name>[@version]`. Configuration,
deploy declarations, audit entries and logs carry references, never values.

| Scope | Reference | Example |
|---|---|---|
| Workspace | `secret://workspace/<name>` | `secret://workspace/MAIL_KEY` |
| Project | `secret://<project>/<name>` | `secret://memowikis/STRIPE_KEY` |
| Project environment | `secret://<project>/<environment>/<name>` | `secret://memowikis/prod/DATABASE_URL@3` |

`workspace` is reserved and cannot be a project name. Secret names start with a
letter and use letters, digits, `.`, `_` or `-`, up to 64 characters. Project
names also allow internal spaces so existing watched projects can use their
display names as scope keys.
Deeper scopes inherit and may override. A reference to
`secret://memowikis/stage/MAIL_KEY` resolves the environment secret, then the
project secret, then the workspace secret. The first secret that exists
answers the reference. A revoked override does not fall back to a parent
secret.

## Operations

| Operation | Effect |
|---|---|
| Put | Creates version 1, or replaces the secret: a new version is created and every earlier version is revoked at once. |
| Rotate | Adds a new current version. Earlier versions stay resolvable by `@version` until they are revoked. |
| Revoke | Revokes one version, or every active version, and deletes the sealed file of each revoked version. New resolutions fail at once. |
| Delete | Removes the metadata and every sealed version. |
| List | Metadata only: name, scope, versions, created, rotated, last used, and who. An item that fails its key or integrity check is listed as unreadable; the others still list. |
| Get for workload | Issues one value to a verified workload for a stated purpose. |

Only a person may put, rotate, revoke or delete. Only a workload may receive a
value. No human route returns a value, and the UI clears each value field after
saving.

## Rights

| Who | Metadata | Put, rotate, revoke, delete | Value |
|---|---|---|---|
| Owner | every scope | every scope | never |
| Operator | workspace and member projects | member projects; the workspace only with access to every project | never |
| Viewer | workspace and member projects | no | never |
| Product role | no | no | never |
| Deploy job (`runner.deploy`) | no | no | only the names its environment declares, in its project and environment |
| Task run (`runner.lease`) | no | no | the workspace and its project scope |

In the `local` profile, the local operator acts as owner and is recorded as
`human:local-operator`.

## HTTP contracts

All management routes require a signed-in person (or the local operator in
the `local` profile). Their responses contain metadata only. The scope routes
below use one of `/api/secrets/workspace`, `/api/projects/{project}/secrets`,
or `/api/projects/{project}/environments/{environment}/secrets` as `{base}`.

| Method and path | Request | Success response |
|---|---|---|
| `GET /api/secrets` | None | Overview object described below. |
| `PUT /api/secrets/backends` | Backend binding object described below. | `200` with the saved binding; selecting `builtin` removes the external binding. |
| `GET /api/secrets/audit?scope=<scope>&limit=<n>` | Both query fields optional; default limit is 200. | `200` with `{ "entries": [...] }`, filtered to visible scopes. |
| `GET {base}` | None | `200` with a metadata array for that exact scope. |
| `GET {base}/{name}` | None | `200` with one metadata object. |
| `POST {base}` | `{ "name": "API_KEY", "value": "..." }` | `201` with metadata and a `Location` header for the new item. A put also replaces an existing item. |
| `POST {base}/{name}/rotate` | `{ "value": "..." }` | `200` with metadata. |
| `POST {base}/{name}/revoke` | `{ "version": 2 }`, or `null` for all active versions. | `200` with metadata. |
| `DELETE {base}/{name}` | None | `204` with no body. |

The overview has `store` (`builtin` or `scoped`), `keySource`,
`canWriteWorkspace`, `secrets`, `unreadable`, `backends`, and
`backendHealth`. Each `secrets` item has `reference`, `scope`, `scopeKind`,
`name`, `status`, `currentVersion`, creation/update/rotation/last-use actor
and time fields, and a `versions` array with version, origin, active flag,
creation/revocation/last-use metadata. No human response has a `value` field.
Each `unreadable` item has `id`, claimed `scope` and `name`, `problem`, and
sanitized `reason`. One failed adapter contributes an unreadable item for its
scope while healthy scopes remain available.

The backend binding request and response use `scope` (`workspace`,
`<project>`, or `<project>/<environment>`), `kind` (`builtin`, `vault`, or
`onepassword`), `url`, `mount` (Vault/OpenBao), `vaultId` (1Password),
`credentialReference` (`secret://workspace/NAME`) or
`credentialEnvironmentVariable`, and `ttlSeconds` (1 to 300). Credential
values are never in this contract. Only workspace-wide secret writers may
change a binding, even for a project or environment. The server saves the
selection before checking reachability. Read `backendHealth` on a subsequent
overview to see `{ scope, kind, state, reason }`, where state is `healthy` or
`unavailable`; the reason is sanitized. An unavailable selection remains
selected and fails closed for its scope.

Errors use `{ "error": "<code>", "message": "<safe explanation>" }`.
Malformed bindings and values return `400`; an unauthenticated person gets
`401`, denied rights get `403`, a missing secret gets `404`, and a revoked
secret gets `409`. Backend failures, including `backend-auth-failed`,
`backend-unreachable`, `backend-misconfigured`,
`backend-rotation-unsupported`, `backend-revoke-unsupported`, and
`backend-version-unsupported`, return `503`. Backend response bodies and
secret values are never copied into these messages. A backend selection is
audited as `backend-select`; secret operations use the normal audit actions.

Workloads: `POST /api/runner/secrets/resolve` with an enrolled service
credential. The workload block names the lease the credential runs under:

```json
{ "reference": "secret://memowikis/stage/DATABASE_URL", "purpose": "deploy:stage",
  "workload": { "kind": "deploy-job", "project": "memowikis", "jobId": "<id>", "leaseToken": "<token>" } }
```

A `task-run` workload names `taskKey` and `leaseId`. The server derives the
project and environment from the live lease, not from the request. A browser
session never receives a value, in any profile. The response carries
`Cache-Control: no-store` and has
`{ "reference": "...", "resolvedReference": "...@<version>",
"version": <number>, "value": "..." }`. This is the only route that returns a
value. A missing Runner credential returns `401`; a denied or mismatched
workload lease returns `403` and is audited. An unavailable selected backend
returns `503` for that resolution without falling back to a parent scope.

## Project environment bindings and delivery (AGT-3246)

The project Deploy area's Secrets view offers Preview plus every declared
environment. It reuses the provider's write-only add and rotate controls and
stores only name-to-reference bindings as control metadata. An environment
declaration's `secretRefs` names must each be bound before its job starts.
Names are converted to uppercase container variables, with `.` and `-`
replaced by `_`. Declarations that produce the same variable name are invalid,
including case-only collisions. The host also rejects older queued jobs with
colliding names before resolving any value. The resolution audit purpose is
`deploy:<environment-id>` when it fits the 64-character provider limit;
longer IDs use `deploy:` followed by a 16-character SHA-256 prefix of the ID.
An older queued job with a collision reports `secret-reference-collision`.
The binding may address the environment, its project, or the workspace;
provider inheritance still chooses the closest existing secret. Preview
bindings may be prepared before a deploy declaration exists.

`GET /api/projects/{project}/secret-bindings/{environment}` returns bindings
and a metadata-only preflight. Each missing entry names the reference, scope,
and reason (`not-bound`, `not-found`, `revoked`, `unreadable`, or
`approval-required`).
`PUT /api/projects/{project}/secret-bindings/{environment}/{name}` accepts
`{ "reference": "secret://...", "approve": true|false }`. Only the current
Owner or Operator secret-management check may write. A Production binding
also needs an interactive Owner or project production approver with
`approve: true`; the binding records that approval. This is the single
permission seam for AGT-3228 to replace with scope roles.
The Secrets view uses the environment declaration's `kind` to show the
Production approval control, even when its ID is not `production` or `prod`.

Bindings are persisted under
`<TaskRepository>/.security/secret-bindings/<key>.json`, separate from the
provider's sealed values. `<key>` is the lowercase hexadecimal SHA-256 of
the project ID, one LF byte, and the environment ID encoded as UTF-8, using
the IDs passed to the binding service. Each file is a JSON array of
`{ name, reference, boundBy, boundAt, approvedBy }` records. A rebind replaces
the record with the same name. The service writes a temporary file in the
same directory, sets mode 600 on Unix, and atomically replaces the file.
Missing files mean no bindings. Back up this metadata with `TaskRepository`;
restoring it alone does not restore the secret values or their key. No value,
lease token, or resolved version is stored in a binding.

The claim response gives the host only references and preflight problems.
Health-only retries of an already deployed container skip secret preflight and
resolution because they run the health check without starting a new container.
The host fails a job with missing references before touching its artifact.
The queue stores the declaration's secret names and environment kind with each
job. Claim preflight and workload issuance use that snapshot even if the live
declaration later changes. For older queue records without a kind, promotion
history supplies stage or production when available; otherwise the job uses
the production approval rule. A storage or provider error after a claim returns
the claimed job with `preflight-unavailable` missing references, so the host
can report a failed deployment without receiving any secret value.
For a container job it resolves each bound reference over the authenticated
workload route using the claimed job's lease on the run host. It writes the
container environment file under Linux `/dev/shm` in a private directory,
passes that file to Docker, and removes it in `finally`. Values never enter
the job record, release, image, control-plane deploy log or host error text.
The existing production deployment approval remains required before the
Production job is queued. Rotation takes effect on the next deploy.

`agent-host deploy-poll` takes `<server-url> <host-id> <token-file>
<artifact-store>`. The artifact store remains the fourth argument for static
deployments. For compatibility, the command also accepts a fifth positional
argument formerly used as a local secret-store path, but ignores it. Container
deployments obtain values from `POST /api/runner/secrets/resolve` on the run
host using the claimed job's lease; a local secret directory is no longer a
source of deploy values. A claim with missing references reports
`secret-reference-missing` without resolving values or starting the deploy.

## Built-in store

The store lives in `<TaskRepository>/.security/secrets/`. The directory is
owner-only, and so is every file in it (see [File protection](#file-protection)).

```
items/<id>/v000001.sealed.json   immutable, one file per active version
items/<id>/metadata.json         replaced atomically, HMAC-authenticated
audit/<yyyy-MM>.jsonl            append-only audit
```

- **Envelope encryption.** Each version gets its own random 256-bit data key.
  The value is sealed with AES-256-GCM under that data key. The data key is
  sealed with AES-256-GCM under a wrap key that HKDF-SHA256 derives from the
  key-encryption key (KEK). The metadata MAC key is a separate HKDF output, so
  the raw KEK never keys a cipher or a MAC itself. Both seals bind the secret
  id and version as associated data, so a version file copied to another
  secret fails authentication. Sealed files carry format 2; format 1 (data
  keys wrapped under the raw KEK) is refused.
- **Integrity on read.** A modified ciphertext, nonce, wrapped key or metadata
  file fails its integrity check, and the provider refuses it. Metadata is
  authenticated with HMAC-SHA256 under a key derived from the KEK, so a
  revocation cannot be undone by editing the file. The store checks that a
  version is still active under its own lock right before it decrypts, so a
  revocation that lands while a resolution is in flight stops that issuance.
- **Revocation survives a metadata restore.** The MAC carries no generation
  counter, so an older `metadata.json` from a backup still verifies. Revoking
  a version (or replacing it with a put) therefore deletes its sealed file once
  the metadata records the revocation. A restored older metadata file can name
  that version as active again, but resolving it fails with
  `secret-integrity-failed` because the sealed file is gone. Only restoring the
  whole directory, sealed files included, brings a revoked version back; that
  is a deliberate full restore.
- **Atomic and backup-safe.** A version file is durable before the metadata
  names it. A copy of the directory taken at any moment therefore restores to
  a consistent state. At worst it holds one version file that nothing names,
  or the sealed file of a revoked version that the next change removes.
  A delete renames the directory before removing it.

### Key-encryption key

The KEK is never stored next to the data. The provider looks for it in this
order:

1. The environment variable named by `Secrets:KeyEnvironmentVariable`, by
   default `AGENT_STUDIO_SECRETS_KEK`. It holds 32 bytes in base64. The
   backend reads it once while it starts, before any hosted service runs. It
   then removes the variable from the environment that .NET passes to child
   processes, so CLI runs, build and test gates and project URL processes
   started by the backend do not inherit it. It also clears the copy that
   the environment-variable configuration provider holds in `IConfiguration`.
   Supply the variable from the service manager on every start. Its text is
   registered with `CredentialRedactor` and never evicted there, so a printed
   environment or command line is masked as `[REDACTED_CREDENTIAL]` in
   redacted logs.
2. The key file `Secrets:KeyFile`. In the `local` profile the default is
   `~/.config/agent-studio/keys/secrets.kek` (or `$XDG_CONFIG_HOME/...`). On
   Windows it is `%LOCALAPPDATA%\AgentStudio\keys\secrets.kek`. The key file
   text is registered with `CredentialRedactor` when it is loaded.

**What the KEK protects.** The KEK protects copies and backups of the
workspace: a copy of `.security/secrets` without the key reveals no value. It
does not protect against code that runs as the service account. That code
can read the key in several ways:

- On Linux, `/proc/<pid>/environ` keeps the environment the backend started
  with. Removing the variable inside the process does not change that block,
  so every process of the same account can still read the variable there.
- In key-file mode the key file is readable by the service account, and so by
  every process of that account.
- That code can also ask the running backend for values through any service
  credential it holds.

CLI agents, build gates and project processes that run under the backend's
account therefore count as trusted with the KEK. To keep them out, run agent
and project processes under a separate account or in a separate container
that has no access to the key file, the backend's `/proc` entries or the
workspace's `.security` directory.

**Creating a key.** The store creates a key file only on the first write to a
store that holds no item. Once items exist, a missing key file is an error
(`secret-key-unavailable`) and no new key is created, so a redeploy that
mounts only the workspace cannot fork the store under a second key. Restore
the original key to open the items again.

A valid but wrong key (a stale or mistyped environment key) cannot fork the
store either. While items exist, a put of a new name is sealed only if at
least one stored metadata file names the loaded key's fingerprint. Otherwise
the put fails with `secret-key-unavailable` and writes nothing. A rotate or
put of an existing name already needs that item's metadata to verify under
the loaded key.

**Profiles.** Outside the `local` profile the default key-file location is not
used: set the environment variable or `Secrets:KeyFile` explicitly. With an
explicit `Secrets:KeyFile`, the first write to an empty store may create the
file there. To provide the key yourself, write 32 random bytes in base64, for
example `head -c 32 /dev/urandom | base64 > secrets.kek && chmod 600 secrets.kek`.

The provider refuses a key file inside the workspace. It also refuses a key
file that other users can access (see [File protection](#file-protection)).
Workspace backups therefore never contain the KEK. Back up the KEK
separately: a restore needs the same key, and without it the sealed versions
cannot be opened. Each sealed version and each metadata file records the KEK
fingerprint, so a mismatched key fails with `secret-key-unavailable` instead
of returning wrong data. The overview (`GET /api/secrets`) and the Secrets
page list such items under `unreadable` with the problem
(`key-unavailable`, `integrity-failed` or `read-failed`); the readable secrets
still list. The scope and name shown for an unreadable item are unverified.
The reason of a `read-failed` item names only the exception type, never a
file path.

### File protection

| What | Unix | Windows |
|---|---|---|
| Key file | Created with mode 600. Every load refuses it when a group or other bit is set. The owner is not compared: a mode-600 file of another account cannot be opened unless the service runs as root. | Created with a protected ACL (inherited entries removed) that grants full control to the service account only. Every load refuses it when its owner is not the service account, `SYSTEM` or the local Administrators group, or when an allow entry grants read, write, permission or ownership rights to anyone else. |
| Key directory, store directories, audit directory | Created with mode 700. | Created with a protected ACL that grants full control to the service account only, inherited by everything created inside. |
| Version, metadata and audit files | Created with mode 600. | Inherit the directory ACL. |

Directories that already exist are not changed or re-checked: a store or audit
directory created by another tool keeps the access it has. Parent directories
above the protected ones (for example `.security`) are created with default
access.

## Audit and log scrubbing

Every put, rotate, revoke, delete and resolve appends one audit entry. An
entry records time, action, actor, scope, reference, version, purpose,
workload and outcome, never a value. Denied and failed attempts are included:

- `denied:<code>` for a refused attempt. This covers a write the access
  middleware refuses (viewer or Product role, missing CSRF token, foreign
  project), a write the scope rights check refuses, a workload trying to
  write, and every refused resolution.
- `failed:<code>` for a validation failure (`value-required`,
  `secret-not-found`, `secret-revoked`, ...), `failed:integrity`,
  `failed:key-unavailable`, `failed:cancelled`, and `failed:error` for any
  other exception.

A put refused by the access middleware names only its scope
(`secret://<scope>/`), because the middleware does not read the request body.

Refused writes are merged per actor. The first refusal is written at once.
Further refusals with the same actor, action, scope, reference, version and
outcome within one minute of it are only counted. Once that minute has passed,
the next audit entry of any kind first writes one merged entry for them:
`repeated` holds their count and `at` the time of the last one. The count is
held in memory, so a burst that is still open when the backend stops keeps
only its first entry.
Not audited: requests without a signed-in person or service credential (they
carry no actor) and requests whose scope or name is malformed (they name no
secret).

When a value is issued, the provider registers it with `SecretValueScrubber`.
Before runner log lines (`POST /api/runner/logs`, the task folder's
`logs/cli-output.log`) and runner event messages are stored, the scrubber
replaces the value, each line of a multi-line value, and the base64 form with
`[secret:<reference>]`. Registrations expire 24 hours after the last
resolution. The pattern-based `CredentialRedactor` still runs first.

## Studio

The Secrets section is under Settings → Global → Secrets, next to Credentials
(`#/workspace/settings/secrets`). It lists the secrets of the selected scope
(workspace, project, or project environment) with versions and metadata. Owners
and operators can add, rotate and revoke there. Viewers see metadata only.

## Not in this card

- External store adapters (IP-9).
- Migrating the Task Server credential registry metadata into this store.
  That registry records custody of host credentials, not values.
- An OS key store (DPAPI, Keychain, Secret Service) as a KEK source. It can be
  added as another `IKeyEncryptionKeySource`.
