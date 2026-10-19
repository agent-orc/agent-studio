# Studio website

The recommended host is a dedicated small VM (application lifecycle D15, option A). The operator owns the VM, DNS, TLS, backups, and access to it. Studio users need only the HTTPS URL and a password. The monolith, static frontend, and Caddy edge run in one Compose stack. The release workflow publishes `agent-studio-api` and `agent-studio-web` images with matching `v<version>` tags.

## Install

1. Point the hostname at the VM. Allow inbound TCP 80 and 443. Install Docker Compose, Git, and OpenSSL on the VM. Use an operator account with Docker access. Clone or extract the matching release's `deploy/compose/monolith-networked` directory.
2. Run `bash setup.sh <version> <hostname> /srv/agent-studio-website`. The script initializes the workspace repository, creates data directories, and prints a random installer owner code. Keep that code private until the first owner account is created. The script writes `.env` with mode 0600; keep it out of backups shared with users.
3. A public hostname uses Caddy's automatic ACME certificate. For a private hostname, set `STUDIO_CADDYFILE=./Caddyfile.private`; install the Caddy internal root certificate from `caddy-data/caddy/pki/authorities/local/root.crt` on every browser machine. For a hostname behind a Cloudflare-proxied DNS record, run setup with `--cloudflare` (see [Cloudflare mode](#cloudflare-mode)).
4. If the release packages are private, authenticate the VM to GHCR with `docker login ghcr.io`. In this directory run `docker compose pull && docker compose up -d`. Open `https://<hostname>`. On the first visit enter the printed installer owner code, owner name, and password. Later visits use the URL and password. The first remote visitor cannot claim the owner without the code. The code ceases to authorize bootstrap once an owner exists.

The API and frontend have no published ports. Caddy is the only public listener. `AllowedHosts` is the configured hostname. The frontend container has fixed address `172.30.83.3`; only that address may supply HTTPS forwarding information to the API. The edge has fixed address `172.30.83.2`, and the frontend accepts a forwarded client address only from it (`trusted_proxies` in `Caddyfile.frontend`). The edge forwards the client it resolved, so the installer-code rule, login throttling, and API logs see the real client instead of the edge container. Do not connect untrusted containers to the Compose network. For a different subnet, change the edge and frontend addresses, `Caddyfile.frontend`, and `Security__TrustedProxyAddresses` together.

Every response carries a Content-Security-Policy with `frame-ancestors 'self'`, `X-Frame-Options: SAMEORIGIN`, `Cross-Origin-Opener-Policy: same-origin`, a minimal `Permissions-Policy`, and HSTS with `includeSubDomains`. The frontend Caddy sets them for the static shell, the API for its own routes, and every edge mode forces HSTS. The URL preview proxy and task result HTML keep their own sandbox policy. Session and CSRF cookies are host-only `__Host-` cookies, `Secure` and `SameSite=Strict`; the session cookie is also `HttpOnly`. A browser change must carry a matching `Origin` or `Sec-Fetch-Site: same-origin` in addition to its CSRF token. Scripts that use a session cookie with curl must send `-H "Origin: https://<hostname>"`. See [Security overview](../security/overview.md).

## Cloudflare mode

Use this mode when the hostname is a proxied (orange-cloud) Cloudflare DNS record with SSL/TLS mode **Full**. Caddy still obtains its own ACME certificate for the hostname through the proxied record, so keep ports 80 and 443 proxied to the VM.

- New installation: `bash setup.sh --cloudflare <version> <hostname> /srv/agent-studio-website`. This writes `STUDIO_CADDYFILE=./Caddyfile.cloudflare` and `STUDIO_CLOUDFLARE_ALLOW_DIRECT=false` to `.env`.
- Existing installation: set those two lines in `.env`, then run `docker compose up -d`.

The edge treats only peers in `cloudflare-ips.caddy` as proxies and takes the client address from their `CF-Connecting-IP` header. A request from any other address is refused with HTTP 403, so the origin cannot be reached around Cloudflare. To admit direct access deliberately, for example during a DNS cutover or for a monitor that tests the origin, set `STUDIO_CLOUDFLARE_ALLOW_DIRECT=true` (or run setup with `--allow-direct-origin`) and run `docker compose up -d`. A direct peer is then its own client address; its `CF-Connecting-IP` is ignored. A host firewall that admits ports 80 and 443 only from Cloudflare's ranges adds a second layer.

`cloudflare-ips.caddy` is a versioned copy of Cloudflare's published ranges (`https://www.cloudflare.com/ips-v4` and `ips-v6`). Refresh it with `bash update-cloudflare-ips.sh`, then `docker compose restart edge`. The script accepts only CIDR lists, leaves the file unchanged when nothing changed, and with `--check` writes nothing and exits 1 when the published ranges differ. Run the check regularly, for example monthly from cron; a missing new Cloudflare range makes the edge refuse traffic that Cloudflare sends from it. The Update Service is not part of this stack and Studio does not poll port 5039 on HTTPS unless explicitly configured.

This stack starts an empty workspace. Add projects, runner credentials, and CLI authentication through their normal owner/operator flows before running jobs. An existing installation needs a planned workspace and project-checkout migration; copy their data during the agreed outage, then verify runner access and job execution.

## Backup and restore

Stop the stack with `docker compose stop` before copying `/srv/agent-studio-website/workspace`, `projects`, `home`, `caddy-data`, and `caddy-config`. The workspace includes `.security/access-state.json`, so protect the backup as credential material. Keep `.env` with the backup in a separate restricted location. Restore those directories and `.env` onto a replacement VM, retain ownership of workspace, projects, and home by UID 10001, point DNS at it, and run `docker compose up -d`. Test sign-in before retiring the old VM.

## Update and stop

For an update, back up first, change `STUDIO_VERSION` in `.env` to a published release version, then run `docker compose pull && docker compose up -d`. Check `docker compose ps`, `https://<hostname>/api/auth/status`, sign-in, and the board. To roll back, restore the backup and previous `STUDIO_VERSION`, then run `docker compose up -d`. To stop without removing data, run `docker compose stop`. `docker compose down` removes containers and the network but leaves the bind-mounted data directories.
