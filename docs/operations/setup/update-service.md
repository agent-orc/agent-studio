# Update Service operations

The Update Service is the standalone process that applies Stable updates for a
dev or Stable checkout and keeps answering while the Studio backend restarts.
`./update-service.sh start` runs it from `update-service/`. Placement rules
(which updater owns which installation) live in the
[installation upgrade contract](./installation-upgrade-contract.md).

## Bind address

The service listens on loopback only: `http://127.0.0.1:5039` and
`http://[::1]:5039` by default. `ATP_UPDATE_LISTEN` (or
`UpdateService:ListenUrl`) overrides it. Keep it on loopback.

## Trigger token

Every state-changing route (`POST /update/trigger`, `POST /update/rollback`)
requires the header `X-Update-Token`. A missing or wrong token gets `401`. The
service compares the token in constant time and logs each refusal with the
caller address, origin and user agent, never the token.

The service never runs without a token. It takes the first of:

1. `ATP_UPDATE_TOKEN` (or `UpdateService:TriggerToken`).
2. The token file, when it exists and is not empty.
3. A new token: on its first start the service generates 32 random bytes,
   writes them base64url-encoded to the token file and uses them.

The token file is `update-service/.update-token`, beside the service's
`appsettings.json`. `ATP_UPDATE_TOKEN_FILE` (or
`UpdateService:TriggerTokenFile`) moves it. The service creates the file
owner-only: mode `600` on Linux and macOS, a protected ACL that grants only the
service account on Windows. On Linux and macOS it restricts an existing file
with wider permissions back to `600` at start and logs that it did. The file is
listed in `.gitignore`; never commit it.

The start log names the token source (`configured`, `file` or `generated`) and
the file path.

### Rotate the token

Stop the service, delete the token file, start the service. It generates a new
token. Reload open Studio tabs, or wait for their next trigger: the Update
Center fetches the token again after a `401`.

## How Studio sends the token

The Studio backend reads the token from the same file and serves it to the
Update Center at `GET /api/update-service/token` (`Cache-Control: no-store`).
The route is mapped in the local security profile only. The backend takes the
first of `UpdateService:TriggerToken`, `ATP_UPDATE_TOKEN`, and the token file.
The file defaults to `../update-service/.update-token` relative to the backend
content root, which is the same file in the same checkout.
`UpdateService:TriggerTokenFile` or `ATP_UPDATE_TOKEN_FILE` points it
elsewhere. Set the same path for both processes if you move the file. A
backend in a different checkout from the running service, such as the dev
backend behind the 4010 frontend while Stable's service owns port 5039, needs
`UpdateService:TriggerTokenFile` set to that service's token file.

The Update Center fetches the token after its first successful status read
and keeps it in memory. A rollback therefore still works while the backend is
down after a failed update. Reloading the page needs the backend again to read
the token.

The networked and public-demo profiles do not map the token route. A hosted
Studio does not use the machine-local service unless
`__AGENT_STUDIO_UPDATE_SERVICE_URL__` is set. Trigger such an installation from
the machine with the token, as in the next section.

## Trigger from a shell

Read the token from the file and send it:

```sh
token="$(cat update-service/.update-token)"
curl -fsS http://127.0.0.1:5039/update/preflight
curl -fsS -X POST http://127.0.0.1:5039/update/trigger \
  -H 'Content-Type: application/json' -H "X-Update-Token: ${token}" \
  --data '{"reason":"manual","force":false}'
```

```powershell
$Token = (Get-Content .\update-service\.update-token -Raw).Trim()
Invoke-RestMethod -Method Post -ContentType 'application/json' `
  -Headers @{ 'X-Update-Token' = $Token } `
  -Body '{"reason":"manual","force":false}' `
  http://127.0.0.1:5039/update/trigger
```

`scripts/restart-continuity-drill.sh` reads the token from `UPDATE_TOKEN`.

## Forced triggers

`"force": true` overrides manual apply mode and queues behind a run in flight.
It is accepted only on a request with a valid token, because the token check
runs before the request body is read. Every forced trigger is logged as a
warning with the caller address, origin, user agent and trigger reason.

## Allowed origins

The Update Center calls the service from the browser, so the service sends
CORS headers to configured Studio origins only. `ATP_UPDATE_ALLOWED_ORIGINS`
(or `UpdateService:AllowedOrigins`) is a `;`-separated list of exact origins.
The default is the loopback dev and Stable frontends:

```text
http://127.0.0.1:4010;http://localhost:4010;http://127.0.0.1:4011;http://localhost:4011
```

Add an origin only for a Studio frontend you serve from another address. A
wildcard `*` entry is ignored. An empty value grants no origin at all, for a
Studio that calls the service server-to-server. The policy allows `GET` and
`POST`, the headers `Content-Type` and `X-Update-Token`, and never allows
credentials.

## Settings summary

| Environment variable | Configuration key | Default |
|---|---|---|
| `ATP_UPDATE_LISTEN` | `UpdateService:ListenUrl` | `http://127.0.0.1:5039;http://[::1]:5039` |
| `ATP_UPDATE_TOKEN` | `UpdateService:TriggerToken` | unset, so the token file is used |
| `ATP_UPDATE_TOKEN_FILE` | `UpdateService:TriggerTokenFile` | `update-service/.update-token` |
| `ATP_UPDATE_ALLOWED_ORIGINS` | `UpdateService:AllowedOrigins` | loopback ports 4010 and 4011 |
