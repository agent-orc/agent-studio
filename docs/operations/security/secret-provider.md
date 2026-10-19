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
| Revoke | Revokes one version, or every active version. New resolutions fail at once. |
| Delete | Removes the metadata and every sealed version. |
| List | Metadata only: name, scope, versions, created, rotated, last used, and who. |
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
owner-only, and so is every file in it.

```
items/<id>/v000001.sealed.json   immutable, one file per version
items/<id>/metadata.json         replaced atomically, HMAC-authenticated
audit/<yyyy-MM>.jsonl            append-only audit
```

- **Envelope encryption.** Each version gets its own random 256-bit data key.
  The value is sealed with AES-256-GCM under that data key, and the data key is
  sealed with AES-256-GCM under the key-encryption key (KEK). Both seals bind
  the secret id and version as associated data, so a version file copied to
  another secret fails authentication.
- **Integrity on read.** A modified ciphertext, nonce, wrapped key or metadata
  file fails its integrity check, and the provider refuses it. Metadata is
  authenticated with HMAC-SHA256 under a key derived from the KEK, so a
  revocation cannot be undone by editing the file.
- **Atomic and backup-safe.** A version file is durable before the metadata
  names it. A copy of the directory taken at any moment therefore restores to
  a consistent state. At worst it holds one version file that nothing names.
  A delete renames the directory before removing it.

### Key-encryption key

The KEK is never stored next to the data. The provider looks for it in this
order:

1. The environment variable named by `Secrets:KeyEnvironmentVariable`, by
   default `AGENT_STUDIO_SECRETS_KEK`. It holds 32 bytes in base64.
2. The key file `Secrets:KeyFile`. The default is
   `~/.config/agent-studio/keys/secrets.kek` (or `$XDG_CONFIG_HOME/...`). On
   Windows it is `%LOCALAPPDATA%\AgentStudio\keys\secrets.kek`. The file is
   created with mode 600 on the first write.

The provider refuses a key file inside the workspace. It also refuses a key
file that other users can read. Workspace backups therefore never contain the
KEK. Back up the KEK separately: a restore needs the same key, and without it
the sealed versions cannot be opened. Each sealed version and each metadata
file records the KEK fingerprint, so a mismatched key fails with
`secret-key-unavailable` instead of returning wrong data.

## Audit and log scrubbing

Every put, rotate, revoke, delete and resolve appends one audit entry. Denied
and failed attempts are included. An entry records time, action, actor, scope,
reference, version, purpose, workload and outcome, never a value.

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
