# Studio website

The operator owns the host, DNS, TLS, backups, and access to it. Studio users need only the HTTPS URL and a password. The monolith, static frontend, and Caddy edge run in one Compose stack. The release workflow publishes `agent-studio-api` and `agent-studio-web` images with matching `v<version>` tags. Application lifecycle D15 selected option B: Studio runs on `agent-runner-01` with repository-managed resource containment.

## Install

1. Point the hostname at the VM. Allow inbound TCP 80 and 443. Install Docker Compose, Git, and OpenSSL on the VM. Use an operator account with Docker access. Clone or extract the matching release's `deploy/compose/monolith-networked` directory.
2. Run `bash setup.sh <version> <hostname> /srv/agent-studio-website`. The script initializes the workspace repository, creates data directories, and prints a random installer owner code. Keep that code private until the first owner account is created. The script writes `.env` with mode 0600; keep it out of backups shared with users.
3. A public hostname uses Caddy's automatic ACME certificate. For a private hostname, set `STUDIO_CADDYFILE=./Caddyfile.private`; install the Caddy internal root certificate from `caddy-data/caddy/pki/authorities/local/root.crt` on every browser machine.
4. If the release packages are private, authenticate the VM to GHCR with `docker login ghcr.io`. In this directory run `docker compose pull && docker compose up -d`. Open `https://<hostname>`. On the first visit enter the printed installer owner code, owner name, and password. Later visits use the URL and password. The first remote visitor cannot claim the owner without the code. The code ceases to authorize bootstrap once an owner exists.

The API and frontend have no published ports. Caddy is the only public listener. `AllowedHosts` is the configured hostname. The frontend and edge containers have fixed addresses `172.30.83.3` and `172.30.83.2`. The API trusts forwarded addresses only from these two proxies, and the frontend trusts only the edge. Do not connect untrusted containers to the Compose network. For a different subnet, change both proxy addresses in `Security__TrustedProxyAddresses` and the frontend Caddyfile. The Update Service is not part of this stack and Studio does not poll port 5039 on HTTPS unless explicitly configured.

## Login protection behind Cloudflare

Before proxying `robert.omgai.dev` through Cloudflare, add a WAF rate limiting rule:

| Setting | Value |
|---|---|
| Match expression | `(http.host eq "robert.omgai.dev" and http.request.method eq "POST" and http.request.uri.path eq "/api/auth/login")` |
| Counting characteristic | IP source address |
| Threshold | 20 requests per 1 minute |
| Action | Block for 1 minute |

Count all login requests, including successful ones. This rule reduces work before requests reach the API. A shared public IP can hit the edge limit together, so review Cloudflare's rate limiting events after cutover and adjust the threshold if legitimate users are affected. Keep the origin restricted to Cloudflare source IP ranges at the host firewall; a direct request to the origin bypasses this edge rule. The current source ranges are published at [Cloudflare IP ranges](https://www.cloudflare.com/ips/). Refresh the trusted ranges in `Caddyfile.public` when Cloudflare changes them. Caddy accepts forwarded visitor IPs only from those ranges, reduces the forwarded chain to the verified client IP, and the frontend and API trust only their named proxy peers.

The API also keeps independent failure budgets per client and per username. A client is one IPv4 address or one IPv6 `/64`. Defaults are 10 client failures and 20 username failures in 15 minutes. The client is denied for 15 minutes after its budget is exhausted. When the username budget is exhausted, a client without a successful login to that account in the last 24 hours waits 2 seconds before another password check; a recently successful client can continue immediately. This avoids a stranger locking out every client of a known account. A new legitimate client may need one short retry during an attack, while a distributed attack can still spend hashes across client addresses. At most one password hash per client and a core-sized number of hashes overall run concurrently. The limits are set with `Security__LoginThrottle__ClientMaxFailures`, `UsernameMaxFailures`, `WindowMinutes`, `ClientLockoutMinutes`, `UsernameDelaySeconds`, `RecentSuccessMinutes`, and `MaxConcurrentHashes` on the API container. Keep the Cloudflare rule active even when these limits are tuned.

Login state expires on the injected clock and has separate caps for client attempts, username attempts, recent successes, and per-client delays. Each cap defaults to 8192 entries and can be set with `Security__LoginThrottle__MaxTrackedClients`, `MaxTrackedUsernames`, `MaxRecentSuccesses`, and `MaxDelayEntries` (1 to 65536). Expired entries are removed before capacity is reused. A full failure-budget table keeps its active entries. Unknown usernames are refused before hashing; known accounts from untracked clients may still sign in, while their wrong passwords count against shared overflow client and username budgets. The client overflow budget blocks further hashes until its lockout ends, so a legitimate new client can also be delayed during a flood. A full recent-success table replaces its oldest entry, so that client may face a short delay during an ongoing username attack. When the delay table is full, one extra slot holds a client-specific short retry deadline. A new client may wait for that slot during a flood; an unused slot expires after another delay interval so it cannot deny a new client for the whole username window. The client failure budget and Cloudflare rule limit hash work after the retry. Usernames longer than 64 characters fail without a username entry and still count against the client budget.

This stack starts an empty workspace. Add projects, runner credentials, and CLI authentication through their normal owner/operator flows before running jobs. An existing installation needs a planned workspace and project-checkout migration; copy their data during the agreed outage, then verify runner access and job execution.

## Backup and restore

Stop the stack with `docker compose stop` before copying `/srv/agent-studio-website/workspace`, `projects`, `home`, `caddy-data`, and `caddy-config`. The workspace includes `.security/access-state.json`, so protect the backup as credential material. Keep `.env` with the backup in a separate restricted location. Restore those directories and `.env` onto a replacement VM, retain ownership of workspace, projects, and home by UID 10001, point DNS at it, and run `docker compose up -d`. Test sign-in before retiring the old VM.

## Update and stop

For an update, back up first, change `STUDIO_VERSION` in `.env` to a published release version, then run `docker compose pull && docker compose up -d`. Check `docker compose ps`, `https://<hostname>/api/auth/status`, sign-in, and the board. To roll back, restore the backup and previous `STUDIO_VERSION`, then run `docker compose up -d`. To stop without removing data, run `docker compose stop`. `docker compose down` removes containers and the network but leaves the bind-mounted data directories.

## Studio on a runner host

On a runner host, install the cgroup v2 slices before starting the website or the [orchestrator engine](../../../deploy/compose/monolith-engine/compose.yaml). The host needs systemd, Docker with its systemd cgroup driver, and at least 2 GiB RAM. From the repository checkout, run `sudo bash deploy/runner-host/apply-containment.sh`. Re-run it after a host resize or repository update; `--remove` removes the installed units, drop-ins, and derived slice properties. The script prints effective before and after properties and the required restarts. It does not restart live jobs or containers.

The script gives `studio.slice` CPU and IO weight 1000 and a memory protection floor. It gives `agent-work.slice` CPU and IO weight 25 and an aggregate CPU quota, memory reclaim threshold, and hard memory limit. It calculates the work quota as all but two logical cores, with one core minimum. Studio gets up to 10 GiB of protected memory on a large host; on a smaller host the floor is a quarter of MemTotal. The work slice has the remaining memory as its hard limit and a reclaim threshold at 90% of that limit. On a 12-core, 62-GiB host these are a 1000% work CPU quota, 10-GiB Studio MemoryLow, about 46.8-GiB work MemoryHigh, and 52-GiB work MemoryMax. CPU weight favors Studio under contention; Studio has no CPU cap. MemoryLow protects memory Studio actually uses under pressure and does not allocate 10 GiB in advance.

After applying, drain coding and review work, then restart `agent-runner.service` and replace `agent-runner-review.service` through `agent-runner-deploy restart-review`. The managed Review unit refuses a direct `systemctl restart`. Their `Slice=` drop-ins move the services into `agent-work.slice`; existing delegated `worker-*` cgroups stay in their old location until those workers exit. Do not interrupt active jobs just to move them. Check placement with `systemctl show -p Slice -p ControlGroup agent-runner.service agent-runner-review.service` and inspect `/sys/fs/cgroup/agent-work.slice/`. Existing per-worker CPU and process envelopes continue to apply inside the aggregate work slice.

In `deploy/compose/monolith-networked`, use both Compose files for every website lifecycle command:

```sh
docker compose -f compose.yaml -f compose.runner-host.yaml pull
docker compose -f compose.yaml -f compose.runner-host.yaml up -d --force-recreate
docker compose -f compose.yaml -f compose.runner-host.yaml ps
```

The override places `api`, `frontend`, and `edge` under `studio.slice` through Docker's systemd cgroup driver. The API has a 1-GiB reservation and 4-GiB limit. The engine Compose file already places `orchestrator-engine` under the same slice; recreate that container after the slice is installed. Check live placement with `docker inspect --format '{{.HostConfig.CgroupParent}}' <container>` and `systemctl show studio.slice agent-work.slice -p CPUWeight -p IOWeight -p CPUQuotaPerSecUSec -p MemoryLow -p MemoryHigh -p MemoryMax`. Keep the override in update, rollback, stop, and restore commands so a recreated website container stays in the slice. After `--remove`, restart the runner services and recreate the containers to leave their current slices.
