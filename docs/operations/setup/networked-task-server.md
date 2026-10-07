# Networked Task Server

This is the reference deployment for a small, single-organization Task Server.
Do not expose the local profile or treat `X-Client-Id` as a credential.
This runbook covers the legacy networked backend. The standalone Task Server
uses a different identity contract; see
[identity-and-project-bootstrap.md](./identity-and-project-bootstrap.md).

## Topology

Caddy listens on ports 80 and 443. It serves the production Angular build and
proxies `/api`, `/hubs`, and `/healthz` to Kestrel on `127.0.0.1:5030`. Studio
therefore uses the same origin for login, API calls, and SignalR. Caddy handles
WebSocket upgrade automatically.

The reference files are:

- [`deploy/networked/Caddyfile`](../../../deploy/networked/Caddyfile)
- [`backend/appsettings.Networked.json.example`](../../../backend/appsettings.Networked.json.example)

## Install and first owner

1. Point the public DNS name at the server. Allow inbound TCP 80 and 443. Do
   not allow inbound 5030.
2. Build Angular and copy its browser output to
   `/srv/agent-studio/frontend/browser`. Run the backend as a dedicated Unix
   user with a writable Task Repository and loopback URL only.
3. Copy the networked settings example to a host-owned configuration file,
   set `AllowedHosts`, and set `ASPNETCORE_ENVIRONMENT=Production`. Keep the
   file and Task Repository readable only by the service user.
4. Install Caddy, copy the reference Caddyfile, and set `STUDIO_DOMAIN` and
   `ACME_EMAIL` in the Caddy service environment.
5. Validate before reload:

   ```bash
   sudo caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
   sudo systemctl reload caddy
   curl -fsS https://tasks.example.com/healthz
   ```

6. Open the HTTPS origin and create the first owner. Bootstrap fails after the
   first owner exists. Create separate operator and viewer accounts as needed.

The backend networked profile rejects cleartext application requests even if a
proxy is misconfigured. The health endpoint returns only `ok`. Debug, internal
probe, and filesystem diagnostic endpoints are not mapped.

## Fail-closed deployment check

Run these checks from a machine with no Studio cookies. Expected status is in
the comment.

```bash
origin=https://tasks.example.com
curl -fsS "$origin/healthz"                         # 200, body: ok
curl -sS -o /dev/null -w '%{http_code}\n' "$origin/api/tasks" # 401
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$origin/api/tasks" # 401
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$origin/api/clients/register" # 404
curl -sS -o /dev/null -w '%{http_code}\n' -X POST "$origin/hubs/jobs/negotiate" # 401
curl -sSI "http://tasks.example.com/healthz" | head -n 1 # 308 redirect
curl -sSI "$origin/healthz" | grep -i strict-transport-security
```

Use browser developer tools after login to confirm the session cookie is
Secure, HttpOnly, SameSite Strict, and host-only. Confirm Angular local storage
and session storage contain no password, session token, or Runner bearer.

## Enroll a Runner

As an owner, call `POST /api/auth/runner-enrollments` from an authenticated
same-origin administration client with its CSRF header. Choose only the scopes
the daemon needs. The default set is:

```text
runner.claim runner.lease runner.logs runner.events runner.artifacts runner.completion
```

Transfer the returned `enr.*` code over the SSH administration channel. On the
Runner host, exchange it once at `POST /api/auth/runner-enroll`. Put the returned
`rnr.*` secret in a protected credential file, not in a command line, task, or
general environment file:

```bash
sudo install -d -m 0750 /etc/agent-runner
sudo sh -c 'umask 0077; read -r secret; printf "%s\n" "$secret" > /etc/agent-runner/runner-auth-token'
# Paste the one-time rnr.* value on stdin, then grant only the service group read access.
sudo chown root:agent-runner /etc/agent-runner/runner-auth-token
sudo chmod 0640 /etc/agent-runner/runner-auth-token

RUNNER_SERVER_URL=https://tasks.example.com
RUNNER_ID=runner_<id-returned-by-enrollment>
RUNNER_NAME=build-runner-01
RUNNER_AUTH_TOKEN_FILE=/etc/agent-runner/runner-auth-token
```

The Runner refuses a non-loopback HTTP URL and
requires a service credential for a non-loopback server. `RUNNER_CLIENT_ID` is
optional attribution only and grants no access.

## Runner secret rotation rehearsal

Both runbooks below are executable, not just prose. Rehearse them with
[`deploy/networked/rehearse-runbooks.sh`](../../../deploy/networked/rehearse-runbooks.sh),
which writes a secret-free evidence log, and see
[runbook-rehearsal-evidence.md](../security/runbook-rehearsal-evidence.md) for a
captured run. The fail-closed invariants are additionally pinned by
`RunbookRehearsalTests` (rotation + certificate) and `RealTlsTransportTests`
(real HTTPS Runner credential) in `backend.Tests`.

```bash
# Live rotation workflow (owner session required); one-time secrets are never logged.
STUDIO_COOKIE=... STUDIO_CSRF=... deploy/networked/rehearse-runbooks.sh rotate https://tasks.example.com runner_<id>
```

Rotation is deliberately overlapping and should be rehearsed before the first
incident.

1. Owner creates a credential with `POST
   /api/auth/runners/{runnerId}/credentials`. Record its id and capture its
   secret once.
2. Write the new secret to a new protected credential file, atomically replace
   `runner-auth-token`, restart the Runner, and wait for its new credential's
   `lastUsedAt` to appear in `GET /api/auth/runners`.
3. Revoke only the old credential with `DELETE
   /api/auth/runners/{runnerId}/credentials/{oldCredentialId}`.
4. Prove an old-secret request returns 401 and the new daemon still claims and
   heartbeats. Record date, operator, Runner id, old credential id, new
   credential id, and result. Never record either secret.
5. For host loss, revoke the whole Runner identity and its repository deploy
   keys. Do not wait for credential expiry.

## Certificate renewal rehearsal

Caddy renews ACME certificates automatically. Exercise the operational path at
initial deployment and quarterly. The self-contained self-test needs no
production certificate and no network — it issues a self-signed cert, simulates
renewal, and asserts the invariants (serial rotates, expiry extends, cert parses,
21-day alert threshold); the live check reads the deployed certificate:

```bash
deploy/networked/rehearse-runbooks.sh cert-selftest            # self-contained evidence
deploy/networked/rehearse-runbooks.sh cert-check tasks.example.com  # live certificate

sudo caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
sudo systemctl reload caddy
sudo journalctl -u caddy --since '15 minutes ago' --no-pager
openssl s_client -connect tasks.example.com:443 -servername tasks.example.com </dev/null 2>/dev/null \
  | openssl x509 -noout -issuer -subject -dates
```

Temporarily block outbound ACME traffic in a staging deployment, confirm Caddy
continues serving the existing certificate, restore traffic, and confirm the
next renewal attempt succeeds. Record the certificate serial, old and new
expiry, reload validation, simulated failure, recovery time, and operator. Do
not force production renewal by deleting Caddy storage.

Alert before 21 days remaining. Back up Caddy storage with the same protection
as other private key material. A renewal failure does not justify bypassing TLS.

## Limits and recovery

- Caddy and Kestrel both cap request bodies at 25 MiB. Raise both together only
  with an artifact-specific need and a regression test.
- Trust forwarded headers only from the local proxy hop. Never publish Kestrel
  directly.
- Back up `<TaskRepository>/.security` with the Task Repository. It contains
  hashes and audit, not plaintext credentials, but still requires confidential
  handling.
- If security state is unreadable, the server fails security operations rather
  than silently opening bootstrap or accepting an identity.
