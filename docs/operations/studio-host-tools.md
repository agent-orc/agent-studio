# Studio deploy and runner host tools

| Use | Command from the installed stack directory | Success means |
| --- | --- | --- |
| Before switching a release | `sudo ./preflight-start.sh IMAGE-TAG [DEADLINE-SECONDS]` | The candidate API passed its image health check against the installation's data and cleanup succeeded. |
| Inspect owner pages or compare two releases | `sudo ./shadow-studio.sh start [IMAGE-TAG]` | The API is healthy. The web container is healthy if its image defines a health check, or running if it has none. Forward the printed web address over SSH. |
| Recover runner disk space between attempts | `sudo env RUNNER_WORKDIR="$RUNNER_WORKDIR" ./prune-prep-runs.sh PROJECT` | The sweep completed for that project's preparation cache. |

Install the three executable scripts and `studio-copy.py` together from the release's `deploy/compose/monolith-networked/` directory. Use a trusted, operator-controlled installation directory. Each script accepts `--help`. Use Linux and Python 3.12 or newer. The two Studio tools need root, a local Docker Engine with Compose, util-linux mount tools, and an overlayfs-compatible scratch filesystem. The shadow also needs the system systemd manager. The prune tool runs on the runner host and works independently of Docker.

## Configuration and isolation

The Studio tools run `docker compose config` in their installed directory. Set `STUDIO_STACK_DIR` to use a separate installed stack, including its normal Compose files, overrides and environment. The tools identify the API by its `TaskRepository` setting and the web service by its Caddyfile mount and dependency on that API. Use `STUDIO_COPY_API_SERVICE` and `STUDIO_COPY_WEB_SERVICE` for custom service names when discovery is ambiguous. A supplied tag selects the configured repositories, including registries with ports. Stage the corresponding images locally first. With no tag, the shadow uses the exact image IDs of the running API and web services, including digest-pinned releases.

Each tool gets separate private scratch under `/var/lib/agent-studio-copies`, scoped to the stack directory. Set `STUDIO_COPY_STATE_DIR` to a root-owned, mode 0700 directory on another disk if needed. Use the same stack and scratch settings for subsequent commands. Keep scratch outside the three data directories. Workspace, projects and home are mounted with the live bind sources as overlay lower directories. The API writes into disposable upper directories. The live service continues to change the lower directories, so this view can span different moments in time. Use the backup procedure for a consistent recovery copy. [Studio backup and restore](setup/studio-backup-restore.md)

The copy receives the configured secret-store key file and, if declared as a bind, `/app/appsettings.Local.json`, both read-only. Other live mounts, including backup destinations, host CA, Operations principal files and the live API temp directory, are omitted. Both copies share host CPU, memory and disk with production. Production records and the key that decrypts stored secrets are readable inside the copy. Use trusted release images. Docker administrators and processes with access to the internal bridge remain inside the host trust boundary.

The API environment file contains the single-line values Compose resolves for the API. These include its security profile, allowed hosts, public domain, edge and trusted proxy settings, installer bootstrap code, workspace and home paths, Operations and remote gate settings, backup settings and key-file paths. Installation-specific variables may contain credentials directly. Unset and multiline values are skipped. The tools write the file with mode 0600 inside a mode 0700 run directory and unlink it as soon as Docker starts the API. They keep Compose output and the environment out of their logs. Docker retains the container environment until removal, so restrict access to the Docker daemon.

## Release preflight

Run the command before switching traffic. Its default health deadline is 240 seconds. A deadline of zero samples health once. The API has no network interfaces beyond loopback, a 12 GiB memory limit and six CPUs. Use the timing to spot startup work that grows with the production card count. Calls to runners, providers and remotes cannot succeed. Edge routing, frontend behavior and external dependencies need their own checks.

The tool prints bounded recent API logs, startup phases and memory use. Its private per-tag log lives under the preflight scratch directory. A completed probe ends with a line such as `result=healthy seconds=37`. The other results are `unhealthy` for a deadline or tool failure and `exited` for a stopped API. Exit 0 allows the switch. Exit 1 means the health probe failed, 2 covers input or admission, and 3 covers setup or teardown. Use the process exit status in the deploy job. A busy mount leaves its scratch intact and makes cleanup fail. After resolving the busy mount, rerun preflight to reclaim the stopped session.

A failed container health inspection reports `unknown` in shadow status. Preflight, shadow startup and the shadow watchdog check again on the next poll. Preflight reports `unhealthy` if its deadline expires before it can confirm health or exit.

## Screenshot through an SSH forward

The shadow uses an internal Docker network and publishes no host port. The web server shares the API's network namespace and proxies to its loopback listener. For this copy, the local security profile supplies the owner view. The tool removes the production trusted proxies and bootstrap code, disables public edge mode, accepts the forwarded browser host and sets the domain to localhost. The internal request has no authenticated production edge or public client address, and this disposable session has no use for an installer credential. The live site's access policy continues to apply to the live site. The shadow exercises local owner workflows. Public sign-in, MFA, proxy policy and remote effects require separate verification.

Run the following in a shell on the **operator workstation** with an SSH host alias, permission to run the installed scripts via `sudo -n`, and a Playwright CLI with Chromium installed. Set the first two values to match the installation. The control socket closes just this forward. The trap also stops the shadow if the screenshot command fails.

```sh
(
  set -eu
  host=runner-01
  stack=/srv/agent-studio/stack
  socket_dir=$(mktemp -d)
  start_output=$(ssh "$host" "sudo -n '$stack/shadow-studio.sh' start") || {
    rmdir "$socket_dir"
    exit 1
  }
  finish() {
    ssh -S "$socket_dir/control" -O exit "$host" 2>/dev/null || true
    ssh "$host" "sudo -n '$stack/shadow-studio.sh' stop"
    rmdir "$socket_dir"
  }
  trap finish EXIT
  trap 'exit 1' HUP INT TERM
  address=$(printf '%s\n' "$start_output" | awk '$1 == "address" {print $2}')
  test -n "$address"
  ssh -M -S "$socket_dir/control" -fNT -o ExitOnForwardFailure=yes \
    -L "127.0.0.1:18080:$address" "$host"
  ssh "$host" "sudo -n '$stack/shadow-studio.sh' status"
  playwright screenshot --browser chromium --viewport-size '1440,1000' \
    --wait-for-selector '[data-testid="studio-titlebar"]' \
    http://127.0.0.1:18080/ studio-owner.png
)
```

For interactive inspection, open `http://127.0.0.1:18080/` in a browser while the forward is open. For a before/after pair, run the session once per release, supplying the candidate tag after `start` on the second run and a different screenshot filename. Actions and screenshots can contain production data.

Start requires at least 15 percent of the scratch filesystem's capacity available. The defaults are **4 GiB** (`STUDIO_SHADOW_LIMIT_MIB=4096`) and **30 minutes** (`STUDIO_SHADOW_LIFETIME_SECONDS=1800`). Supply either setting via `sudo env` before the start command. Status reports measured bytes, byte limit, age and lifetime in seconds, container states, address and resource usage. For a web image without a health check, `web=running` confirms only that its process is running. The screenshot command waits for the Studio title bar to render. The API is limited to 12 GiB and four CPUs, the web server to 512 MiB and one CPU.

The startup loop checks the limits while waiting for health. A transient systemd service then checks every five seconds and tears down both containers, the network and overlays when either threshold is reached, a container exits, or measurement fails. The size includes allocated blocks in the three upper directories and Docker's two writable container layers. The check can overshoot between samples. Docker logs, image storage, anonymous volumes and host-wide free space are outside that measurement. Use `sudo ./shadow-studio.sh stop` to release the session early or retry a failed cleanup. The next start also reclaims stopped leftovers after a host restart.

## Prune preparation copies

Use the runner service's configured `RUNNER_WORKDIR` and the project's directory name. The sweep visits only immediate, lowercase 32-hex directory names in `$RUNNER_WORKDIR/PROJECT/caches/preparation/.runs`. It checks directory age (over 30 minutes) and reads host process environments for the preparation path and attempt ID. The suffix matches paths inside work containers too. A permission error aborts the sweep. Run as root with the full host PID namespace and procfs visible. `STUDIO_PROC_ROOT` selects an alternate mount of that same host procfs.

The sweep preserves recent, referenced and unrecognized entries, symbolic links and the published cache. The process scan is a snapshot. References held only in an open file or acquired after the scan are outside its evidence. This is a temporary operator remedy until runner-owned end-of-attempt cleanup is deployed.

To schedule it, adapt the three installation values in this example for `/etc/cron.d/studio-prune-prep`. The command writes one summary to stdout per successful sweep. Cron reports errors on stderr. Remove the cron entry when the runner takes ownership.

```cron
RUNNER_WORKDIR=/home/agent/runner-work
*/15 * * * * root /srv/agent-studio/stack/prune-prep-runs.sh PROJ-002 >> /var/log/studio-prune-prep.log
```
