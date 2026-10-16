# Studio website

The recommended host is a dedicated small VM (application lifecycle D15, option A). The operator owns the VM, DNS, TLS, backups, and access to it. Studio users need only the HTTPS URL and a password. The monolith, static frontend, and Caddy edge run in one Compose stack. The release workflow publishes `agent-studio-api` and `agent-studio-web` images with matching `v<version>` tags.

## Install

1. Point the hostname at the VM. Allow inbound TCP 80 and 443. Install Docker Compose, Git, and OpenSSL on the VM. Use an operator account with Docker access. Clone or extract the matching release's `deploy/compose/monolith-networked` directory.
2. Run `bash setup.sh <version> <hostname> /srv/agent-studio-website`. The script initializes the workspace repository, creates data directories, and prints a random installer owner code. Keep that code private until the first owner account is created. The script writes `.env` with mode 0600; keep it out of backups shared with users.
3. A public hostname uses Caddy's automatic ACME certificate. For a private hostname, set `STUDIO_CADDYFILE=./Caddyfile.private`; install the Caddy internal root certificate from `caddy-data/caddy/pki/authorities/local/root.crt` on every browser machine.
4. If the release packages are private, authenticate the VM to GHCR with `docker login ghcr.io`. In this directory run `docker compose pull && docker compose up -d`. Open `https://<hostname>`. On the first visit enter the printed installer owner code, owner name, and password. Later visits use the URL and password. The first remote visitor cannot claim the owner without the code. The code ceases to authorize bootstrap once an owner exists.

The API and frontend have no published ports. Caddy is the only public listener. `AllowedHosts` is the configured hostname. The frontend container has fixed address `172.30.83.3`; only that address may supply HTTPS forwarding information to the API. Do not connect untrusted containers to the Compose network. For a different subnet, change the frontend address and `Security__TrustedProxyAddresses` together. The Update Service is not part of this stack and Studio does not poll port 5039 on HTTPS unless explicitly configured.

This stack starts an empty workspace. Add projects, runner credentials, and CLI authentication through their normal owner/operator flows before running jobs. An existing installation needs a planned workspace and project-checkout migration; copy their data during the agreed outage, then verify runner access and job execution.

## Backup and restore

Stop the stack with `docker compose stop` before copying `/srv/agent-studio-website/workspace`, `projects`, `home`, `caddy-data`, and `caddy-config`. The workspace includes `.security/access-state.json`, so protect the backup as credential material. Keep `.env` with the backup in a separate restricted location. Restore those directories and `.env` onto a replacement VM, retain ownership of workspace, projects, and home by UID 10001, point DNS at it, and run `docker compose up -d`. Test sign-in before retiring the old VM.

## Update and stop

For an update, back up first, change `STUDIO_VERSION` in `.env` to a published release version, then run `docker compose pull && docker compose up -d`. Check `docker compose ps`, `https://<hostname>/api/auth/status`, sign-in, and the board. To roll back, restore the backup and previous `STUDIO_VERSION`, then run `docker compose up -d`. To stop without removing data, run `docker compose stop`. `docker compose down` removes containers and the network but leaves the bind-mounted data directories.
