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

   Installation-specific backend settings live in `appsettings.Local.json` at
   the backend content root. The container deployment mounts that file at
   `/app/appsettings.Local.json`. It overrides the packaged appsettings files;
   environment variables take precedence over it. For example,
   `Security__MaxRequestBodyBytes` overrides
   `Security:MaxRequestBodyBytes` in the file. Restart the backend after
   changing a startup limit such as the Kestrel request body cap.

4. Install Caddy, copy the reference Caddyfile, and set `STUDIO_DOMAIN` and
   `ACME_EMAIL` in the Caddy service environment.
5. Validate before reload:

   ```bash
   sudo caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
   sudo systemctl reload caddy
   curl -fsS https://tasks.example.com/healthz
   ```

6. Open the HTTPS origin and create the first owner. Bootstrap fails after the
   first owner exists. In **Settings > Access**, invite people with a project
   scope and the role they need. **Admin** manages settings, operations,
   production deploys and secrets. **Developer** works on cards, runs, chat and
   documents, and deploys outside production. **Product** writes cards and
   accepts them from Human Review in assigned projects. Owner, Admin and
   Developer can also review and accept cards in their scope. **Viewer** reads within
   the assigned scope without secret values. **Approver** approves or rejects
   production releases in scope. The **Owner** retains installation recovery
   and all permissions. Admin and Owner must register a second factor before
   using Studio; Approver needs one to approve a release. Studio recommends
   one to every other invitee after acceptance.

The backend networked profile rejects cleartext application requests even if a
proxy is misconfigured. The health endpoint returns only `ok`. Debug, internal
probe, and filesystem diagnostic endpoints are not mapped.

## Project checkouts

When an Owner registers a project with a repository URL and no checkout path,
the networked monolith clones that URL into a server owned checkout beside the
Task Repository's `projects` directory. It records the checkout as both
`repositoryPath` and `rootPath` before the project is available to cards. The
backend service user needs read access to the URL and write access to that
directory. A failed clone rejects registration with `server-checkout-required`;
give the service user Git access and retry. Registration does not leave a
project that can start a card without its checkout.
Deleting the project removes this managed checkout. Studio leaves manually
supplied checkout paths untouched.

For a private repository, arrange Git access for the backend service user before
registration, or have the Owner enter an existing absolute checkout path on the
Studio server in the project form. The path must name a Git checkout that the
backend service user can read and write. The project settings page also lets an
Owner repair the path of a project registered before this checkout step existed.
Use the project's repository URL as its origin and ensure the integration branch
exists on that remote. A Runner checkout on another host does not replace the
monolith checkout, which owns integration worktrees and refs.

When integration finds no usable checkout, or the integration branch is missing
from it after a fetch, it records the failure code `server-checkout-missing`.
The card then shows the checkout setup links instead of Continue (reissue),
because another coding attempt cannot create the checkout or its missing
integration base. When the base is missing, the remedy names its branch. The
card keeps the failure code even though a project without a checkout has no Git
index. Retry integration appears once the checkout and its selected integration
base are available. It merges the same reviewed delivery again, without a new
coding run or review. An Escalated card whose retry integrates returns to Human
Review for acceptance.

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

As an Owner, open **Settings > Access > Runners > New enrollment code**.
Choose **Coding – read and push** for a coding service or **Review only – read**
for a separate review service. Coding can read and push to its projects'
repositories. Review only can read them. The connected GitHub App supplies
short-lived repository tokens automatically. There is no repository token to
create by hand. Run host (deploy) and Orchestration engine codes carry no
repository access.

An authenticated same-origin administration client can call
`POST /api/auth/runner-enrollments` with its CSRF header. Omitting `scopes`
returns `repositoryAccess: "none"` and grants the following runner scopes:

```text
runner.claim runner.lease runner.logs runner.events runner.artifacts runner.completion
```

Name the projects the runner may work on in `projects` (project ids or names;
`*` for all). A runner enrolled without a project claims, reads and reviews
nothing and receives no repository token. The enrolled credential expires 30
days after enrollment unless `credentialExpiresAt` names another end
(`Security:RunnerCredentialLifetimeDays` changes the default). Change a
runner's projects later with `PUT /api/auth/runners/{id}/projects` or
**Edit projects** in Settings > Access. A runner enrolled before project
binding keeps all projects, and its credentials keep no expiry, until you
choose its projects and rotate; Settings > Access shows both notices. See
[Runner credential binding](../security/overview.md#runner-credential-binding-sm-9).

Transfer the returned `enr.*` code over the SSH administration channel. On each
Runner, Review, or Gate host, run the enrollment command as that service user:

```bash
sudo -u agent-runner -H /opt/agent-host/current/agent-host --enroll https://tasks.example.com 'enr.<one-time-code>'
```

The command validates HTTPS with the host system trust store, then writes a
private `~/.config/agent-host/runner.token` and `installation.env` (mode 0600,
directory mode 0700). Install a private CA in the host trust store before
enrollment. Copy the four `RUNNER_*` entries from `installation.env` into that
service's `runner.env`, preserving the credential-file path. The enrolled
`runner_<id>` is the execution identity; `RUNNER_NAME` keeps the existing board
display name. Enrollment registers that id as a service client, so assign each
project's execution location to the enrolled id before expecting claims.
`RUNNER_CLIENT_ID` remains optional attribution only.
Set `RUNNER_ENROLLMENT_CONFIG_DIR` to an absolute path before enrollment when
the service uses a different private configuration directory; both files are
written there.

For an already enrolled runner, open its details in **Access > Runners**
and choose **New credential**. Select **Read and push to repositories** for
coding or **Read repositories** for review. Copy the credential while it is
shown, replace the contents of the service's `RUNNER_AUTH_TOKEN_FILE` with it,
and keep the file readable only by the service account. If the service uses
`RUNNER_AUTH_TOKEN` directly, move to an owner-only credential file and set
only `RUNNER_AUTH_TOKEN_FILE`. Restart that service and check its readiness
in **Execution Hosts**. Its runner ID and project
bindings stay the same. The previous credential works until it expires or an
Owner revokes it through `DELETE /api/auth/runners/{id}/credentials/{credentialId}`
using the credential ID shown in the detail.

When moving a host from a local tunnel to the networked installation, enroll
each service identity on the networked installation first and switch
`RUNNER_SERVER_URL` to its HTTPS origin. Restart the service and verify
`GET /api/auth/runner` and a claim or idle claim response before removing
the reverse SSH tunnel and its keeper. A `networked` installation returns 426
on its cleartext loopback URL.

Enroll the engine separately with only `orchestration.write`, save its token in
`ENGINE_CREDENTIAL_FILE`, and set `ENGINE_CLIENT_ID` to its enrolled `engine_<id>`.
Set `ENGINE_SERVER_URL` to the same HTTPS origin in the engine Compose environment.
The engine container must trust the installation's issuing CA.

After building the backend, run
`deploy/compose/networked-runner-smoke/run.sh` for an isolated HTTPS Compose
smoke. It starts a temporary networked monolith and Caddy edge, enrolls a
fixture runner, and exercises lease acquisition and completion for a fake card.

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
incident. Rotate each runner before its credential's 30-day end: Settings >
Access marks a credential that ends within 14 days, and the host logs a warning
once a day in its last 7 days. The new credential starts its own 30 days.

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
