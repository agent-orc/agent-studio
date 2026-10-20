# External secret stores

Studio uses the built-in encrypted store by default (Dossier IP-D7 option A).
The Secrets settings page can select Vault/OpenBao KV v2 or 1Password Connect
for one exact workspace, project, or environment scope. Deeper scopes keep their
own binding or the built-in default; `secret://` lookup still checks the
requested scope and then its parents. A configured adapter that fails to
authenticate or respond stops lookup at its scope, so a parent value cannot
silently replace it.

## Configure

1. Put the adapter token in the process environment or create a workspace
   secret in the built-in store. The setting accepts `env:VARIABLE_NAME` or
   `secret://workspace/TOKEN_NAME` (optionally pinned to `@version`). Repository
   files must carry only these references, never tokens.
2. In Settings > Access > Secrets, select the scope and provider. Supply the
   HTTPS endpoint, KV v2 mount or 1Password vault ID, credential source, and
   cache TTL (0 to 300 seconds). Loopback HTTP is allowed for local testing.
3. Save and check the reachability indicator. Only Owner can
   change a binding. The equivalent API is `GET/PUT /api/secrets/adapters` and
   `DELETE /api/secrets/adapters/{scope}`.

The setting is saved in the workspace `.security/secrets/adapter-bindings.json`
without a credential value. Switching a scope does not move existing secrets;
switch back to reach values in its previous store. Secret metadata for external
versions is kept in an encrypted local companion store, with a dummy value in
place of each remote value. Back up that metadata together with the built-in
store. The companion metadata assigns version numbers. Vault writes use KV v2
CAS and require the returned Vault version to match. 1Password Connect stores
each version as a separate concealed-field item with an `agent-studio/` title.
The adapter needs permission to create, list, read, and delete those items.
Vault health also reads the KV v2 mount's `/config` endpoint so a missing or
wrong-version mount is reported as unavailable; grant the adapter token read
access there.

Vault revocation destroys the matching KV v2 version. 1Password revocation
deletes the matching item. Both actions remove read-cache entries immediately.
Values are cached in process memory for at most the configured TTL, while the
local revocation check runs on every issue. The provider continues to enforce
workload scope, audit each operation, and scrub issued values from logs. Remote
HTTP error bodies and values never enter Studio error messages.

The Vault paths and CAS behavior follow the [KV v2 HTTP API](https://developer.hashicorp.com/vault/api-docs/secret/kv/kv-v2).
The 1Password item calls follow the [Connect API](https://www.1password.dev/connect/api-reference).
