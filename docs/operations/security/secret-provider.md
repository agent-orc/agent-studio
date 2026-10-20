# Secret provider

The secret provider implements Dossier
[integrierte-plattform](../integrierte-plattform/index.html) §9, sections 7.2
to 7.4, as card IP-6 (AGT-3222). Every part of the platform uses one interface,
`ISecretProvider` (`backend/Features/Secrets/`). A storage adapter,
`ISecretStore`, sits behind it. This card ships the built-in store. Adapters
for Vault or OpenBao, cloud secret managers and 1Password implement
`ISecretStore` in IP-9, without changes to callers.

Recorded options: IP-D7 A (built-in store plus adapters) and IP-D8 A (write
without read). The operator had not recorded a decision, so the Dossier
recommendation applies. This card has no reveal operation. A later card may add
reveal with its own permission, a reason and an audit entry.

## References and scopes

Every value is addressed as `secret://<scope>/<name>[@version]`. Configuration,
deploy declarations, audit entries and logs carry references, never values.

| Scope | Reference | Example |
|---|---|---|
| Workspace | `secret://workspace/<name>` | `secret://workspace/MAIL_KEY` |
| Project | `secret://<project>/<name>` | `secret://memowikis/STRIPE_KEY` |
| Project environment | `secret://<project>/<environment>/<name>` | `secret://memowikis/prod/DATABASE_URL@3` |

`workspace` is reserved and cannot be a project name. Names start with a
letter and use letters, digits, `.`, `_` or `-`, up to 64 characters.
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

## Routes

People: `GET /api/secrets` (overview), `GET /api/secrets/audit?scope=`, and per
scope `GET`, `POST` (put), `GET /{name}`, `POST /{name}/rotate`,
`POST /{name}/revoke` and `DELETE /{name}` under `/api/secrets/workspace`,
`/api/projects/{project}/secrets` and
`/api/projects/{project}/environments/{environment}/secrets`.

Workloads: `POST /api/runner/secrets/resolve` with an enrolled service
credential. The workload block names the lease the credential runs under:

```json
{ "reference": "secret://memowikis/stage/DATABASE_URL", "purpose": "deploy:stage",
  "workload": { "kind": "deploy-job", "project": "memowikis", "jobId": "<id>", "leaseToken": "<token>" } }
```

A `task-run` workload names `taskKey` and `leaseId`. The server derives the
project and environment from the live lease, not from the request. A browser
session never receives a value, in any profile. The response carries
`Cache-Control: no-store`.

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
- Delivery of values into deploy steps (IP-7).
- Migrating the Task Server credential registry metadata into this store.
  That registry records custody of host credentials, not values.
- An OS key store (DPAPI, Keychain, Secret Service) as a KEK source. It can be
  added as another `IKeyEncryptionKeySource`.
