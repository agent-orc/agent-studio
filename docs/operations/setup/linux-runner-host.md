# Linux agent host (`agent-host`)

Status: Remote daemon runbook. `agent-host` continuously executes server-assigned
projects on a Linux host while retaining the RM-5 one-task diagnostic mode.

Related work: AGT-2092 (runner operations baseline) and AGT-2094 (Admin UI
remote-host onboarding). AGT-2094 uses this runbook as its operational
reference; the UI does not maintain a second copy of these commands.

This is the operator-facing companion to the plan in
[../../concepts/distributed-agent-studio-target-architecture.md](../../concepts/distributed-agent-studio-target-architecture.md)
and the binding lease contract in
[../../concepts/parallel-task-execution.md](../../concepts/parallel-task-execution.md)
§8.2C. The runner code lives under [`runner/`](../../../runner) and consumes only
the Runner API surface added by RM-3 (fenced lease) and RM-4 (log + artifact
upload).

## What the agent host is

A single self-contained .NET console process (`agent-host`) that runs on a
Linux host and fills a bounded set of task slots without owning task state:

- **Code arrives and leaves via git `origin`** - the runner fetches over the
  credential-free URL and pushes with a write-enabled deploy key dedicated to
  this host and repository. A repository-specific delivery preflight proves
  that identity and target branch before the server grants a project lease.
  The daemon's startup probe covers only its configured fallback remote and is
  diagnostic.
- **Results leave through a durable outbox** - protocol 2 journals CLI output,
  status, artifacts, Git facts, terminal facts, and the final result envelope
  under `$RUNNER_WORKDIR/outbox/<run-attempt-id>/` before sending them. The
  Task Server acknowledges one immutable result, then accepts an idempotent
  completion. `Done` and `NoOp` enter normal review; blocked, input, and
  genuinely unknown outcomes remain typed. Remote runs are not labelled as
  out-of-band completions.
- **Exactly-one-runner is enforced by the fenced lease** - acquire mints a
  fencing token, a heartbeat renews it, and a rejected heartbeat (`StaleToken` /
  `Expired`) means another holder took over, so the runner abandons the run
  instead of racing it (the §8.2C split-brain guard, enforced runner-side in
  `runner/LeaseHeartbeat.cs`).
- **Assignment is server-owned** - `ProjectSettings.executionRunner` names the
  daemon that may claim a project's cards. The remote claim endpoint and the
  local in-process runner read that same record, so config drift cannot cause
  double pickup. Lease fencing remains the hard takeover guard.
- **Every slot has its own linked git worktree** under
  `$RUNNER_WORKDIR/<project-id>/worktrees/<task-key>`. Each project has a shared
  clone at `$RUNNER_WORKDIR/<project-id>/repo`; it is fetched before a claimed
  task starts. A coding worktree is removed only after the Task Server has
  durably acknowledged the matching immutable result envelope.

### Remote Review Executor service

Run review as a second systemd identity, even when it shares the physical host
with coding. Set `RUNNER_ROLE=review`, use a different `RUNNER_ID`, service
account, credential file, and `RUNNER_REVIEW_WORKDIR`. The managed agent-host
unit supplies the role cgroup policy described in
[runner-host resource governance](../haertung-verteilte-ausfuehrung/target-architecture/resource-governance.md).
Do not point the review root at `RUNNER_WORKDIR`.

The Review Executor advertises Git/source-bundle, semantic, and vision
capabilities. Each claimed ReviewAttempt receives a fresh workspace, cache,
temporary directory, eight-port block, Compose namespace, database namespace,
and fenced cleanup lifecycle. Child processes start from a cleared environment.
Only names in `RUNNER_REVIEW_CREDENTIAL_ENV` are admitted to cleared review
child-process environments. The shared `provider-auth.env` is loaded by both
service units but does not cross that review boundary unless explicitly
allowlisted. Coding deploy keys must not be present in the review unit.

The executor fetches the immutable result ref or verified Git bundle, proves
repository identity, HEAD, tree, and clean state, then proves HEAD again before
every completion, build/test, requirement, quality, documentation, evidence,
artifact, or vision command. A missing ref reports
`ReviewInfra/SnapshotUnavailable`; there is no coding-worktree or Task Server
checkout fallback.

### Remote Review Executor service

Run review as a second systemd identity, even when it shares the physical host
with coding. Set `RUNNER_ROLE=review`, use a different `RUNNER_ID`, service
account, credential file, cgroup quota, and `RUNNER_REVIEW_WORKDIR`. Do not point
the review root at `RUNNER_WORKDIR`.

The Review Executor advertises Git/source-bundle, semantic, and vision
capabilities. Each claimed ReviewAttempt receives a fresh workspace, cache,
temporary directory, eight-port block, Compose namespace, database namespace,
and fenced cleanup lifecycle. Child processes start from a cleared environment.
Only names in `RUNNER_REVIEW_CREDENTIAL_ENV` are admitted to cleared review
child-process environments. The shared `provider-auth.env` is loaded by both
service units but does not cross that review boundary unless explicitly
allowlisted. Coding deploy keys must not be present in the review unit.

The executor fetches the immutable result ref or verified Git bundle, proves
repository identity, HEAD, tree, and clean state, then proves HEAD again before
every completion, build/test, requirement, quality, documentation, evidence,
artifact, or vision command. A missing ref reports
`ReviewInfra/SnapshotUnavailable`; there is no coding-worktree or Task Server
checkout fallback.

### MVP boundaries (read before relying on it)

- **Push capability is explicit.** Each host/repository assignment has its own
  deploy key and push URL. The platform still owns integration policy, while
  the remote runner may publish its task branch through that bounded identity.
- **Suitability is explicit and narrow.** `remoteExecutionEnabled` defaults to
  true at project level. Set it false only for machine-bound work such as the
  UpdateService Windows machinery or live-checkout drift scans. Headless
  Chromium, UI tests, and screenshots are remote-capable and use the host-owned
  Mode-A stack.
- **The CLI invocation is configurable, not hard-coded.** Headless auth and
  print-mode flags differ per CLI and per version; `RUNNER_CLI_BIN` /
  `RUNNER_CLI_ARGS` select them. See the per-CLI defaults below.

## Test host

`agent-runner` (a Hetzner cloud VM at `<runner-host-ip>`; substitute the address
of your own host). SSH key-auth, one sudo-capable user.
For the local profile, expose no inbound Task Server port and reach it through a
supervised `ssh -R`/`-L` tunnel. For the networked profile, the runner connects
outbound to the authenticated HTTPS origin with its enrolled service identity.
The tunnel procedure and health gate are documented in
[remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md).

### Own the Task Server route

Do not run the tunnel as an unattended bare `ssh -N` process. For the current
Windows-to-Linux reverse route, register the repository-owned functional keeper
from the Studio checkout:

```powershell
.\deploy\windows\agent-runner-tunnel\register-tunnel-keeper.ps1 `
    -SshTarget agent-runner `
    -RemotePort 15031 `
    -OrchestratorPort 5031 `
    -IntervalMinutes 5
```

The keeper probes `/healthz` from the Linux host, removes only the matching dead
forward, and recreates it with SSH keepalives and `ExitOnForwardFailure`. If the
host can initiate the SSH connection, prefer the host-owned `autossh` plus
systemd form in the linked tunnel runbook because it starts before an
interactive Windows logon.

Treat either tunnel form as an interim local-profile topology. Once an
authenticated private Task Server URL is available to the host, point
`RUNNER_SERVER_URL` at it and disable the tunnel keeper instead of carrying both
routes forward.

## Product onboarding from Execution Hosts

The primary setup path is **Workspace Settings -> Execution Hosts -> Set up agent
host**. The action creates a normal visible CLI task, so the existing task
conversation owns live output, operator input, completion, and durable history.
The local controller then runs
[`scripts/remote-runner-onboard.sh`](../../../scripts/remote-runner-onboard.sh);
every provisioning command in that controller is executed through SSH on the
selected host.

Before the task can start, the dialog requires an SSH target, a credential-free
fallback git origin, provider authentication, and one of these Task Server
topologies. The local profile also needs a registered attribution id. The
networked profile instead needs the owner-enrolled `runner_<id>` and a protected
`rnr.*` credential file already on the host:

| Topology | URL entered in setup | Required proof |
|---|---|---|
| Central | Authenticated TLS URL, for example `https://tasks.example.com` | Remote `curl --max-time 10 <url>/healthz` succeeds. Do not expose the workstation with only `X-Client-Id`; it is attribution, not authentication. |
| Tunnel | Remote listener, normally `http://127.0.0.1:15031` | The supervised reverse-tunnel unit from [remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md) is active and the remote health probe succeeds. |
| LAN | Protected workstation LAN address and bound Task Server port | Firewall and access controls are explicit, and the remote health probe succeeds. |

`http://localhost:5031` and `http://127.0.0.1:5031` name the remote host itself
when evaluated there. Setup rejects them for central and LAN modes. Tunnel mode
must name the listener that actually exists on the remote host. A failed probe
stops before package installation and prints central URL, tunnel, and LAN
remediation instead of waiting on a runner request.

The controller is intentionally repeatable after a host wipe:

1. Verify SSH key access, passwordless sudo, .NET 10, and Task Server health
   from the host.
2. Install or update the `CodingAgentRunner` NuGet global tool and require
   version `0.5.0` or newer, then install the Codex and Claude CLIs.
3. Before the visible setup task starts, provision Claude authentication from
   the Studio dialog. Studio sends `CLAUDE_CODE_OAUTH_TOKEN` or
   `ANTHROPIC_API_KEY` only through SSH stdin. The host atomically writes
   `/etc/agent-runner/provider-auth.env` as `root:agent` mode `640`. The value is
   never persisted in Studio, a task, or the repository. Codex uses its
   host-owned `codex login --device-auth` flow; credential files are never
   copied from the operator workstation.
4. Atomically write `/etc/agent-runner/runner.env` with the Task Server URL,
   stable runner identity, optional `RUNNER_CLIENT_ID`, credential-file path,
   and fallback git origin. Install and start `agent-host.service` through
   systemd. Both Coding and Review units load the shared provider-auth file
   after their existing runner EnvironmentFile. The SSH session never owns the
   daemon process.
5. Prove `systemctl is-enabled`, `systemctl is-active`, agent-host health, the
   variable name in `/proc/<MainPID>/environ`, a fresh provider-auth probe, and
   an authenticated claim or empty-queue response before setup completes.

The NuGet package must be published with package type `DotnetTool` and expose
the `agent-host` command. A library-only `CodingAgentRunner` package cannot be
installed with `dotnet tool`; setup reports that packaging mismatch explicitly
and does not silently switch to a source build. The source-publish procedure
below remains a troubleshooting and development fallback, not the product
onboarding path.

## 1. Provision the host

Ubuntu LTS. Install the runtime the runner and the agent CLIs need:

```bash
sudo apt-get update && sudo apt-get install -y git curl build-essential
# dotnet 10 SDK (or runtime) and node 22 via the usual channels, then:
npm i -g @anthropic-ai/claude-code @openai/codex
npx playwright install --with-deps chromium
```

### Per-host provider authentication

Give every host an explicitly provisioned CLI identity. Do **not** copy the
operator's `~/.claude/.credentials.json` / `~/.codex/auth.json` from the studio.
A copied credential can share refresh-token lineage with the operator session,
so an operator-side re-login or rotation can log out a host during a batch. This
drift occurred on 2026-07-09. Claude's supported headless token environment and
Codex's host-owned login are the permanent replacements for credential-file
seeding.

- **Claude.** For a headless host, use the setup-token flow below. An interactive
  host login remains a diagnostic fallback, not the provisioning contract.
- **Codex.** Same rule: run `codex login` on the host so it writes the host's own
  `~/.codex/auth.json`; do not copy the operator's. Verify with `codex --version`.
- **Rotation is now per host.** Replace only the affected host's provider-auth
  file and restart its units. Other hosts and the operator's normal Claude login
  remain independent.

If an operator temporarily uses the interactive Claude fallback for diagnosis,
the host's `~/.claude/.credentials.json` must stay a plain file the runner user
can read and write in place. The supported headless path does not depend on that
file: clean-context launches receive `CLAUDE_CODE_OAUTH_TOKEN` explicitly after
their isolated config home is prepared. See the clean-context section of
[`docs/system/cli/supported-clis.md`](../../system/cli/supported-clis.md).

### Claude authentication on headless hosts

Create a long-lived token once on an operator-controlled workstation:

```bash
claude setup-token
```

Complete the interactive flow locally. Do not paste the resulting token into a
repository, container image, task card, shell command, or log. The host contract
is one file for all provider environment credentials:
`/etc/agent-runner/provider-auth.env`, mode `0640`, owner `root`, group `agent`.
Both `agent-runner.service` and `agent-runner-review.service` load this file with
`EnvironmentFile=` after their role-specific environment file. The auth probe
and CLI launch use the resulting process environment as authentication
authority. A separate metadata-only freshness check reads native
`~/.claude/.credentials.json` and `~/.codex/auth.json` files when they exist and
returns only expiry and modification timestamps. It never exposes token values
or treats an unknown file format as signed out.

Provision the complete file through SSH standard input. This example prompts
without echo and keeps the token out of local history and the remote command
line:

```bash
read -rsp 'Claude setup token: ' claude_setup_token && printf '\n'
printf 'CLAUDE_CODE_OAUTH_TOKEN=%s\n' "$claude_setup_token" |
  ssh agent-runner-01 '
    set -eu
    umask 077
    auth_tmp=$(mktemp)
    trap '\''rm -f "$auth_tmp"'\'' EXIT
    cat >"$auth_tmp"
    getent group agent >/dev/null || sudo groupadd --system agent
    sudo install -d -m 0750 -o root -g agent /etc/agent-runner
    sudo install -m 0640 -o root -g agent "$auth_tmp" /etc/agent-runner/provider-auth.env
  '
unset claude_setup_token
```

For rotation, generate a replacement with `claude setup-token`, repeat the
atomic stdin provisioning command, then restart every installed runner role:

```bash
ssh agent-runner-01 \
  'sudo systemctl restart agent-runner.service agent-runner-review.service'
```

Omit a unit that is not installed on that host. Never append a token with an
interactive editor or pass it as an SSH argument. When more providers gain
environment-token support, send the complete replacement file through the same
stdin path so this remains the single provider-auth source.

Verify without printing the secret:

```bash
ssh agent-runner-01 '
  set -eu
  sudo stat -c "%a %U:%G %n" /etc/agent-runner/provider-auth.env
  for unit in agent-runner.service agent-runner-review.service; do
    systemctl -q is-active "$unit" || continue
    pid=$(systemctl show -p MainPID --value "$unit")
    sudo grep -zq "^CLAUDE_CODE_OAUTH_TOKEN=" "/proc/$pid/environ"
    printf "%s provider environment present\n" "$unit"
  done
  sudo journalctl -u agent-runner.service --since "10 minutes ago" --no-pager |
    grep "runner-provider-auth status=ok binary=claude"
'
```

Finally, run one disposable Claude probe card. On a provisioned host the startup
journal contains `runner-provider-auth status=ok binary=claude`, the capability
`provider-auth:claude` is ready, and the server may offer Claude cards. The five
waiting cards AGT-2490 through AGT-2494 are the live acceptance batch after host
provisioning. A unit test uses a dummy token only to prove environment transport;
only the real `claude auth status --text` probe and a probe card validate a real
token. Provider-auth details and task output must never contain the token.

The Execution Hosts dialog performs the same SSH-stdin provisioning without
placing the secret in a task. It atomically updates the shared file, restarts
both installed units, verifies the variable name in each daemon's
`/proc/<MainPID>/environ`, and waits for a fresh runner probe. It never persists
the value in the Studio database, repository, task, log, or evidence artifact.

Provider capability snapshots refresh every 60 seconds. Execution Hosts shows
**OK**, **Retrying**, **Limited**, **Expiring**, **Unavailable**, or **Unknown**
per CLI, with the probe detail in the tooltip. Provider auth probes run at most
every five minutes, use a 30-second timeout, and run at lower CPU priority on
Linux. A timeout, network failure, token-refresh race, empty output, launch
failure, unsupported command, tool error, or otherwise indeterminate non-zero
exit keeps the last advertised verdict and writes a
`runner-provider-auth-probe-degraded` journal line. Rate limits retain a
provider-scoped **Limited** state until their parsed or bounded reset time. Two
consecutive explicit logout answers are required for `OK -> Unavailable`; only
that transition creates a sign-in-required operator notification and Ready-card
wait reason. A later successful probe writes
`runner-provider-auth-probe-recovered`, clears the matching capability circuit,
and advertises **OK** without a service restart. When credential metadata exposes
an expiry, Studio gives a quiet warning during the final 14 days. Follow
[cli-relogin-runbook.md](./cli-relogin-runbook.md) for renewal.

Do not create provider-specific files such as `claude.env`.

Ready-card wait text follows the same evidence boundary. A fresh rate-limit
probe shows the bounded provider limit. Two consecutive explicit logout probes
show `Waiting for <provider> sign-in`. An unknown provider badge, an expired
capability snapshot, or a missing runner heartbeat instead names the runner as
unreachable, includes the last snapshot time, and points to the Task Server link
or runner service. An `unknown` badge never produces sign-in guidance.

## 2. Build agent-host

```bash
git clone <origin> agent-taskboard && cd agent-taskboard
release_id="$(date -u +%Y%m%dT%H%M%SZ)-$(git rev-parse --short=12 HEAD)"
staging_root="$(mktemp -d)"
dotnet publish runner/AgentRunner.csproj -c Release -o "$staging_root"
incoming_root=/var/lib/agent-runner/deploy/incoming
test -z "$(find "$incoming_root" -mindepth 1 -print -quit)"
cp -a "$staging_root/." "$incoming_root/"
printf '%s\n' "$release_id" >"$incoming_root/release-id"
sudo /usr/local/sbin/agent-runner-deploy
```

The selected output binary is `/opt/agent-host/current/agent-host`.
`/opt/agent-runner` is a transition symlink for existing automation; new
deployments and units use only `/opt/agent-host`. Every publish must create a
new release directory. Never publish into the active `current` target or over
the files of a running daemon. The CLR can load metadata and method bodies
lazily, so replacing only part of a live multi-file application can corrupt the
running process even before systemd receives the planned restart.

The root-owned deploy helper rejects an invalid or incomplete publish before
changing `current`. It resolves the selected target in `agent-host.deps.json`
and requires every managed runtime assembly by its flattened publish name. It
then runs `agent-host --version` from the root-owned staging directory as the
`agent` service user with a 10-second timeout. The sanitized smoke-check
environment receives the root-resolved .NET installation path so it matches the
host runtime even when .NET is installed outside `/usr/share/dotnet`. Only a
candidate that passes both checks becomes an immutable release.

The validator uses the host's `/usr/bin/python3` standard library. The hardening
migration refuses to install the helper when that interpreter is absent.

Promotion restarts the fixed Coding and Review units, then observes their
systemd `NRestarts` counters and active state for 15 seconds. A counter change,
restart failure, or inactive unit makes the command fail and prints the previous
release id plus an operator one-liner that re-points `current` and restarts both
units. The failed release remains immutable for investigation. The helper does
not add a rollback command or any new sudo argument shape.

## 3. Configure

Every value has an environment-variable default (systemd-friendly); the per-task
identifiers can also be passed as flags. `agent-host --help` prints the full
list.

`RUNNER_*` remains the bootstrap-compatible canonical prefix. Every variable
also accepts the matching `AGENT_HOST_*` alias, for example
`AGENT_HOST_SERVER_URL`; when both forms are set, `RUNNER_*` wins. Stable
identity values such as `RUNNER_ID=agent-runner-01` are not renamed.

| Env var | Flag | Default | Meaning |
|---|---|---|---|
| `RUNNER_SERVER_URL` | `--server` | `http://127.0.0.1:5030` | Task Server base URL (or the tunnelled address). |
| `RUNNER_ID` | `--runner-id` | `agent-runner-<host>` | Stable lease owner identity. Fencing is per task, not per pid. |
| `RUNNER_NAME` | `--runner-name` | `agent-runner-01` | Board-facing runner/project name. |
| `RUNNER_CLIENT_ID` | `--client-id` | (none) | Optional attribution label. It is not authentication and grants no access. |
| `RUNNER_GIT_REMOTE` | `--git-remote` | (none) | Startup push-probe repository and legacy one-shot fallback. It is never inherited by a project clone. |
| `RUNNER_GIT_PUSH_REMOTE` | `--git-push-remote` | (fetch URL) | Startup push-probe and legacy one-shot write URL. It is never inherited by a project clone. |
| `RUNNER_BRANCH` | `--branch` | (base branch) | Branch to check out for the run. |
| `RUNNER_BASE_BRANCH` | `--base-branch` | `main` | Fallback when the task branch is absent on origin. |
| `RUNNER_WORKDIR` | `--workdir` | `$TMPDIR/agent-runner-work` | Where the repo checkout and `results/` live. |
| `RUNNER_ROLE` | `--role` | `coding` | `coding` or the separately registered `review` service. |
| `RUNNER_REVIEW_WORKDIR` | `--review-workdir` | `$TMPDIR/agent-review-work` | Disposable review-only workspace, cache, temp, and evidence root. Must differ from `RUNNER_WORKDIR`. Settled attempt workspaces are removed after report acceptance; inactive attempt remnants older than 72 hours are swept hourly. The reusable `.baseline-cache` is preserved. |
| `RUNNER_REVIEW_CREDENTIAL_ENV` | `--review-credential-env` | (none) | Comma-separated read-only credential variable names admitted into the cleared review environment. |
| `RUNNER_STATE_DIR` | `--state-dir` | `$RUNNER_WORKDIR/.runner-state` | Durable slot, attempt, PID, worker result, and file-backed output state used for planned restart reattachment. Keep it on persistent local storage. |
| `RUNNER_EXEC_ENGINE` | `--exec-engine` | `car` | CLI execution engine inside the detached worker. `car` (default since AGT-2370) drives the CLI through the CodingAgentRunner library: descriptor-built argv, `stream-json` output, permission-mode injection from the card's spec (absent = bypass/yolo), and a task-stable isolated config home whose credential file is linked so OAuth refreshes write through. `legacy` is the pre-AGT-2370 raw spawn and is removed in AGT-2373. |
| `AGENT_STUDIO_CLEAN_CONTEXT_ROOT` | none | `$XDG_STATE_HOME/agent-studio/clean-context` or `~/.local/state/agent-studio/clean-context` | Persistent non-temporary root for task-isolated Claude and Codex homes. Keep it on host-local storage. The same task reuses its marker-validated home across attempts and daemon restarts; inactive homes expire after seven days. |
| `RUNNER_CLI_BIN` | `--cli` | `claude` | Agent CLI binary (or a wrapper script). Under the `car` engine only the binary path and the CLI family derived from it are used. |
| `RUNNER_CLAUDE_CLI_BIN` | `--claude-cli` | `claude` | Claude binary used for Claude-pinned cards when the primary CLI is Codex. The native setup flow writes the discovered path. |
| `RUNNER_CODEX_CLI_BIN` | `--codex-cli` | `codex` | Codex binary used for Codex-pinned cards and the GPT-only project chat path when the primary CLI is Claude. The native setup flow writes the discovered path. |
| `RUNNER_CLI_ARGS` | `--cli-args` | `-p` | Headless CLI args; the prompt is streamed on stdin. **Legacy engine only** — the `car` engine ignores this value (the descriptor owns the argv) and says so at spawn time via the `engine=car` journal line. |
| `RUNNER_CLI_RESUME_ARGS` | `--cli-resume-args` | (none) | Optional provider-specific same-session arguments containing the literal `{sessionId}` placeholder. A supported infrastructure failure resumes at most once; an invalid session falls back to durable salvage once and then escalates. |
| `RUNNER_AUTH_TOKEN_FILE` | `--auth-token-file` | (none on loopback) | Protected file containing the owner-enrolled Runner service credential. Required for every non-loopback Task Server. |
| `RUNNER_AUTH_TOKEN` | none | (none) | Compatibility environment input. Prefer the credential file so the secret is absent from process diagnostics. |
| `RUNNER_TTL_SECONDS` | `--ttl` | `900` | Requested lease TTL; the server clamps it. The default grants a bounded 15-minute authority window so an already-claimed run can survive ten minutes of transport loss. |
| `RUNNER_HEARTBEAT_SECONDS` | | `30` | Renew cadence, kept below the TTL. |
| `RUNNER_RUN_TIMEOUT_SECONDS` | | `3600` | Hard cap on a single CLI run. |
| `RUNNER_MAX_PARALLELISM` | `--max-parallelism` | `2` | Role-local slot ceiling. Coding uses it for bootstrap and as a fallback for an older server; the centrally managed Execution Hosts ceiling can reduce Coding capacity. Review uses the role value directly. Managed hosts accept only values 1 through 6 through the sanctioned role-config command below. |
| `RUNNER_POLL_SECONDS` | `--poll-seconds` | `5` | Delay after an empty claim poll. |
| `RUNNER_SERVER_REQUEST_TIMEOUT_SECONDS` | `--server-request-timeout-seconds` | `60` | Hard deadline for every Task Server HTTP request, including capability advertisement and worker-loss release. |
| `RUNNER_IDLE_WATCHDOG_MINUTES` | `--idle-watchdog-minutes` | `5` | A daemon with no active slots exits after this long without starting a claim poll. The fatal journal line is followed by a service-manager restart. |
| `RUNNER_CLAIM_MAX_LOAD_PER_CORE` | `--claim-max-load-per-core` | `1.5` | Load-per-core ceiling for new work. Coding uses the sustained gate below; Review checks it immediately before each single-slot claim. |
| `RUNNER_LOAD_GATE_SUSTAINED_SECONDS` | none | `120` | Continuous high-load duration before Coding claim admission closes. Review admission does not use this delay. |

### Sanctioned role configuration changes

Do not grant the Runner service account editor, shell, or general write access to
`/etc/agent-runner`. Install or update the root-owned bounded helper from an
operator-owned root session through the host provisioning path:

```bash
cd /path/to/agent-studio
sudo ./scripts/harden-agent-runner-host.sh --apply
```

The migration installs `/usr/local/sbin/agent-runner-deploy`, its root-owned
dependency-closure validator, configuration policy, and the exact sudoers
allowlist. It also preserves the existing no-argument immutable-release
promotion command. An Agent CLI runs with `NoNewPrivileges=true`; initial
installation or replacement of these root-owned assets therefore remains an
operator provisioning action.

The installed helper currently accepts one variable only:

| Role | Unit restarted | Preferred role EnvironmentFile | Clean-install fallback | Accepted value |
|---|---|---|---|---|
| `coding` | `agent-runner.service` | `/etc/agent-runner/runner-coding.env` | `/etc/agent-runner/runner.env` | `RUNNER_MAX_PARALLELISM=1..6` |
| `review` | `agent-runner-review.service` | `/etc/agent-runner/runner-review.env` | `/etc/agent-runner/review.env` | `RUNNER_MAX_PARALLELISM=1..6` |

The helper selects only an approved file that the target unit actually loads,
requires `root:agent` mode `0640`, replaces the value atomically, and restarts
only the mapped role unit. Systemd applies `EnvironmentFile=` values after
`Environment=` values, so the selected role file's
`RUNNER_MAX_PARALLELISM` value overrides a default such as
`Environment=RUNNER_MAX_PARALLELISM=2` in the main unit. After restart, the
helper waits up to 30 seconds for the unit to be active with a nonzero MainPID
that differs from the pre-restart MainPID. Only then does it read
`/proc/<MainPID>/environ`, which avoids selecting either the old daemon or a
detached worker preserved by `KillMode=process`. A restart, handoff timeout, or
process-environment mismatch restores the previous file and retries the old
configuration. Every accepted change writes an `authpriv.notice` journal
record tagged `agent-runner-deploy` with the role, variable, old value, new
value, unit, PID, and result. The sudoers policy independently enumerates both
roles and every integer from 1 through 6. It does not permit another variable,
unit, path, or argument shape.

Set Review to four slots and prove the effective process value without reading
any credential file:

```bash
sudo /usr/local/sbin/agent-runner-deploy \
  config review RUNNER_MAX_PARALLELISM 4

review_pid="$(systemctl show -p MainPID --value agent-runner-review.service)"
tr '\0' '\n' <"/proc/$review_pid/environ" |
  grep '^RUNNER_MAX_PARALLELISM=4$'

# Run this journal check from the operator session used for provisioning.
journalctl -t agent-runner-deploy --since '-5 minutes' --no-pager |
  grep 'action=config role=review .* new=4 .* result=applied'
```

The hard limit of 6 is a host-flood guard, not a capacity recommendation. Keep
Review admission load-aware, compare active slots with host telemetry, and
lower the value if load, memory, or I/O pressure persists. A Coding change sets
the local bootstrap/fallback ceiling; the Task Server remains authoritative for
its centrally versioned live capacity.

Recommended per-CLI headless defaults (verify against your installed version):

When both CLIs are installed, keep both provider-specific binary variables even
though only one CLI is primary. Capability advertisement and card routing use
the provider-specific paths symmetrically. A missing or unauthenticated provider
is advertised as unavailable and only blocks cards pinned to that provider.

- Claude: `RUNNER_CLI_BIN=claude`, `RUNNER_CLI_ARGS="-p"` (prompt on stdin, final
  response on stdout; the runner accepts a `[[TASK_*]]` sentinel only as its
  terminal standalone line).
- Codex: `RUNNER_CLI_BIN=codex`, plus the non-interactive exec flags your version
  exposes. The runner reads the last completed `agent_message`, not raw JSONL
  tool, diff, or diagnostic payloads. When quoting gets awkward, point
  `RUNNER_CLI_BIN` at a small wrapper script instead of fighting the space-split
  arg parser.

### Multi-repository clone layout and eligibility

The claim response contains the durable project id, repository URL, and default
branch. The daemon uses the project id as the cache key, so two projects never
share git metadata even when their task keys happen to look alike:

```text
$RUNNER_WORKDIR/
  PROJ-001/repo
  PROJ-001/worktrees/AGT-2141
  PROJ-007/repo
  PROJ-007/worktrees/QS-104
```

The Task Server resolves the repository URL only from the project's registry URL
entry whose id or label is `repo` (label `repository` is also accepted). The
registered local `RepositoryPath` may supply default-branch metadata, but its
origin is never used as a remote fallback. An assigned project without that
registry URL is reported as not remote-capable and skipped before a lease is
created. The server logs `remote-runner-project-not-remote-capable`; the card
remains Ready and no project clone is created.

On every project-clone contact, including the first clone, the daemon replaces
the complete `remote.origin.url` and `remote.origin.pushurl` value sets with the
project registry URL. This repairs stale existing clones and prevents the
startup probe or one-shot fallback from redirecting project pushes. The runner
emits `git-remote-configured` with `source=project-registry`, the project id,
and both effective URLs.

`RUNNER_GIT_REMOTE` and `RUNNER_GIT_PUSH_REMOTE` are used by the daemon's
one-time startup write probe. They verify baseline host capability only. Every
claimed project must have write credentials for its registry URL; the global
probe URLs do not rewrite project clone remotes.

### Push identity setup

Project clones keep their registered HTTPS URL for both fetch and push. The
simplest write identity is a fine-grained personal access token owned by a
dedicated machine account. Follow [Token requirements](#token-requirements)
before storing it in the runner user's credential helper. Keep the HTTPS URL
free of embedded secrets. Do not put a token in `runner.env`, a command line,
task output, or evidence.

### Token requirements

The coding runner must be able to publish ordinary source changes and changes
under `.github/workflows`. Use one of these exact permission sets:

| Token type | Repository selection | Required permissions |
|---|---|---|
| Fine-grained personal access token, preferred | Resource owner: the organization that owns the repository, or the personal account for a personally owned repository. Select only repositories assigned to this runner. | Repository permissions: **Contents: Read and write** and **Workflows: Read and write**. Metadata read access is added by GitHub. |
| Personal access token (classic), compatibility fallback | The token follows all repository access of its user. Use only when organization policy or a fine-grained-token limitation requires it. | Scopes: **`repo`** and **`workflow`**. For a public-only repository, `public_repo` plus `workflow` is sufficient, but the runner baseline uses `repo` because private repositories are supported. |

PATs are always tied to the user that creates them. A personal token is not an
organization identity. For an organization repository, prefer a dedicated
machine account, choose the organization as the fine-grained token's resource
owner, and wait for organization approval when its policy requires approval.
The account itself must have repository write access. Authorize a classic token
for the organization's SAML SSO when applicable. For a long-lived integration
that acts on behalf of an organization rather than one user, use a GitHub App
instead of sharing a human user's PAT.

Git credential lookup is path-sensitive when `useHttpPath` is enabled. The
repository URL with `.git` and the same URL without `.git` are different exact
keys. Store the same token under both keys. Run this as the systemd runner user
after configuring an appropriate credential helper:

```bash
git config --global credential.https://github.com.useHttpPath true

read -rp 'GitHub machine-account username: ' runner_github_user
read -rp 'Repository slug (OWNER/REPOSITORY): ' runner_repo_slug
read -rsp 'GitHub token: ' runner_github_token
printf '\n'

for runner_repo_path in "$runner_repo_slug" "$runner_repo_slug.git"; do
  printf 'protocol=https\nhost=github.com\npath=%s\nusername=%s\npassword=%s\n\n' \
    "$runner_repo_path" "$runner_github_user" "$runner_github_token" |
    git credential approve
done
unset runner_github_token

for runner_repo_url in \
  "https://github.com/$runner_repo_slug" \
  "https://github.com/$runner_repo_slug.git"; do
  GIT_TERMINAL_PROMPT=0 git ls-remote "$runner_repo_url" HEAD >/dev/null &&
    printf 'credential ok: %s\n' "$runner_repo_url"
done
unset runner_github_user runner_repo_slug runner_repo_path runner_repo_url
```

Never paste the token into a remote URL. `git credential approve` passes it on
standard input so it does not enter shell history. On a headless host, make sure
the selected credential helper persists for the runner user and protects its
storage with user-only permissions.

Set an expiration date and record the owner, repositories, permissions, expiry,
and runner hosts in the operator inventory. Rotate before expiry:

1. Create and approve the replacement token with the same repository selection
   and permissions.
2. Remove both old exact-match entries, then repeat the copy-paste storage block
   above with the replacement token:

   ```bash
   read -rp 'Repository slug (OWNER/REPOSITORY): ' runner_repo_slug
   for runner_repo_path in "$runner_repo_slug" "$runner_repo_slug.git"; do
     printf 'protocol=https\nhost=github.com\npath=%s\n\n' "$runner_repo_path" |
       git credential reject
   done
   unset runner_repo_slug runner_repo_path
   ```

3. Re-run both `git ls-remote` checks, restart `agent-host.service`, and confirm
   `Fallback repo: ok` plus `Fallback workflow: ok` in
   **Workspace Settings -> Execution Hosts**.
4. Revoke the old token only after every assigned repository and runner is
   green. A token owner's departure or repository-access removal also
   invalidates the runner identity and requires immediate rotation.

The guided installer tracked by AGT-2334 must link to this section and include a
**Create token** step before credential storage. That step shows the
fine-grained and classic checklists above, records both URL forms, runs both
read checks, then waits for the daemon capability result. Installer UI and
secret-handling implementation remain on the installer card.

A repository deploy key also works when the host uses an exact per-repository
Git URL rewrite for transport. Generate it as the systemd runner user. Only the
public key leaves the host:

```bash
install -d -m 0700 ~/.ssh
ssh-keygen -t ed25519 -f ~/.ssh/agent-studio-deploy -C 'agent-runner-01:agent-studio' -N ''
cat ~/.ssh/agent-studio-deploy.pub
```

In GitHub, an organization owner must first allow repository deploy keys in the
organization security/settings policy. If the GitHub API returns `422 Deploy
keys are disabled for this repository`, this organization policy is still off.
After it is enabled, add the public key under repository Settings, Deploy keys,
select **Allow write access**, and keep the private key on the runner. Pin its
use without changing the stored project remote:

```sshconfig
Host github-agent-studio
  HostName github.com
  User git
  IdentityFile ~/.ssh/agent-studio-deploy
  IdentitiesOnly yes
```

```bash
git config --global url."git@github-agent-studio:agent-orc/agent-studio.git".insteadOf \
  https://github.com/agent-orc/agent-studio.git
RUNNER_GIT_REMOTE=https://github.com/agent-orc/agent-studio.git
RUNNER_GIT_PUSH_REMOTE=git@github-agent-studio:agent-orc/agent-studio.git
```

The exact rewrite keeps `remote.origin.url` and `remote.origin.pushurl` equal to
the registry value while Git uses the deploy-key SSH transport. Add one exact
rewrite per assigned repository. The `RUNNER_GIT_PUSH_REMOTE` value above is
still only the startup probe input.

At daemon startup, the runner first performs `git push --dry-run` to
`refs/heads/runner-capability-probe/<runner-id>`. It then commits a disabled
throwaway workflow with `[skip ci]`, pushes it to a unique branch below that
namespace, and immediately deletes the branch. This second push proves the
GitHub workflow permission that a dry-run cannot prove.

The runner publishes one of three statuses:

- `ready`: contents and workflow pushes succeeded.
- `ready-no-workflow-scope`: contents pushes succeeded, but GitHub rejected the
  workflow change. Claims remain enabled because card file scope is not known
  before execution.
- `read-only`: the configured fallback push path failed. Project claims still
  depend on their own repository delivery preflight.

Execution Hosts shows separate **Fallback repo** and **Fallback workflow**
badges without presenting either as fleet-wide delivery truth. A missing
workflow permission links back to this section. The same error classifier also
recognizes GitHub's first real workflow rejection; if salvage fails, its
`worktree-blocked` message includes the exact permission checklist and this
documentation path. Restore or rotate credentials and restart the unit; the
next startup probe replaces the fallback status.

### Remote completion protocol

The Task Server returns the operator-authored `prompt.md` verbatim. Immediately
before spawning either CLI, the standalone runner appends the same mandatory
completion protocol used by the local runner. It requires exactly one final
`[[TASK_DONE]]`, `[[TASK_BLOCKED:<reason>]]`,
`[[TASK_NEEDS_INPUT:<reason>]]`, or `[[TASK_NOOP]]` line. This assembly happens
inside the shared task execution path, so daemon claims and one-task diagnostics
cannot drift. The shipped log contains
`remote-completion-protocol appended to task prompt` before the spawn line.

Codex `exec --json -` output remains JSONL on stdout. The scanner consumes the
complete stdout buffer after the process and asynchronous readers have drained;
the sentinel inside an `item.completed` agent message is recognized without a
Codex-version-specific event parser.

## 4. Assign projects and run the daemon

Assignment is stored through the Task Server API. The following assigns a
remote-capable project to this daemon; use an empty `executionRunner` to hand it
back to the local runner:

```bash
curl -X PUT "$RUNNER_SERVER_URL/api/projects/my-project/execution-runner" \
  -H 'Content-Type: application/json' \
  -H 'X-Client-Id: <registered-operator-client-id>' \
  -d '{"executionRunner":"agent-runner-01","remoteExecutionEnabled":true}'
```

Start the foreground daemon with no task argument or with `--poll`:

```bash
export RUNNER_SERVER_URL=http://<studio-host>:5030
export RUNNER_NAME=agent-runner-01
export RUNNER_MAX_PARALLELISM=2
/opt/agent-host/current/agent-host --poll
```

The daemon registers once, polls `POST /api/runner/claim`, and fills free host
slots. The server only returns pickup-eligible `2-ready` cards from assigned,
remote-capable projects and moves a successful fenced claim to `3-progress`.
Before the first lease for each host/project pair, the server offers the
registered repository without moving the card. The daemon creates or refreshes
`$RUNNER_WORKDIR/<project-id>/repo`, sets both `origin` URLs to the registered
URL, verifies them with `git remote get-url`, fetches, and runs
a real write probe that creates and removes a temporary
`runner/<runner-id>/delivery-preflight-*` ref. It reports that result in a
second claim request. A failed probe refuses the claim with its Git error and
leaves the card in `2-ready`. A successful result is cached for following
cards. Changing the project's repository registration or integration branch
invalidates it automatically.

Ready Epic containers are eligible for a special remote planning claim. They
consume one slot and use the normal lease, heartbeat, telemetry, drain, and
cancellation lifecycle. The server supplies the same rendered Epic
decomposition prompt used by the local runner. The daemon creates a bounded,
detached checkout for read-only repository inspection and removes it after the
run without creating or pushing a runner branch. A valid plan creates child
coding cards and sends the Epic to human review; a planning run owns no
Result-SHA, so it never enters the auto-review code-review lane. Empty or
invalid output, or any attempted source mutation, returns the Epic to Backlog.

Epic planning uses the same repository-specific delivery preflight as coding
claims. A failure blocks only that project's claim and does not reduce the
host's slots for unrelated projects.

At startup the daemon reads `RUNNER_STATE_DIR` before registration or a new
claim. Each live worker directory contains a durable `lease-authority.json`
with the last server-issued expiry, a stop-before deadline one heartbeat before
expiry, and confirmed, uncertain, or rejected replay state. A persisted
deadline watcher runs while registration is unavailable. If stop-before is
reached, it reaps and verifies every process whose cwd belongs to the worktree,
records `authority-deadline-exhausted`, and retains the slot for honest server
reconciliation. It never starts a replacement while death is unproven.

A persisted coding attempt is adopted only when its worker PID still has the recorded
start time and `/proc/<pid>/cwd` resolves to the recorded worktree. The daemon
then restores the same lease, fence, Task Server run id, and attempt instance,
and follows the worker's JSONL output file from the persisted sequence. A
worker that finished during the short restart window is finalized from its
atomically written result file. The same inspection resolves both startup and
the attached-process poll. If the PID exits while that inspection is running,
the daemon re-reads the terminal result before it can classify the worker as
missing. The slot enters `launching` before
`Process.Start`, and the worker writes its own atomic `worker.json` identity
before starting the CLI. Startup briefly waits for that identity, closing the
child-start-to-slot-save handoff window without trusting an unverified PID. A
missing process, reused PID, or worktree mismatch is never heartbeated: the
lease is actively released and the Task Server returns the Progress card to
Ready with the next claim using a higher fence. This recovery preserves the
bounded attempt/autonomy contract; it does not create a second attempt or an
autonomous task store on the Runner.

The Review service uses the same positive process-proof boundary under
`RUNNER_STATE_DIR/reviews`. Before starting the ReviewPlan it persists the
immutable ReviewAttempt, subject, original lease/fence, and exact review
workspace. Its detached worker writes `review-worker.json`,
`review-progress.json`, and `review-result.json`. On restart the replacement
Review daemon verifies PID start time and `/proc/<pid>/cwd`, renews the persisted
lease instance, and completes the same attempt and fence. Completed review
commands and an in-flight command are not relaunched. Recovered slots resume
before the daemon evaluates host load for a fresh claim, so load admission can
close without discarding in-flight test time.

If the Review worker is missing, its PID was reused, or its cwd differs, the
replacement does not adopt or execute it. It submits a fenced
`ReviewInfra / ExecutorRestarted` report containing the failed proof, completed
step ids, completed-command duration, and retry reason. If the old lease already
expired, the daemon reclaims that attempt under a fresh fence solely to deliver
this loss report; it never runs from the unproven process. The deterministic
`review-report:<attempt>:<fence>` key can replay the same terminal payload, but
the authority rejects a conflicting payload.

During a Task Server or transport outage, transient claim, heartbeat, event,
artifact, result-handoff, and completion failures do not terminate the daemon.
Already-claimed workers may continue only until their durable stop-before
deadline. Output and terminal facts accumulate in the monotonic local outbox;
new claims stop, and no queued report is replayed while authority is uncertain.
After connectivity returns, a successful exact-lease and exact-fence renewal
must reconcile authority before replay. Idempotency keys make retrying a lost
response safe. Task Server restart is different from transport recovery: it
quarantines the attempt as `process-unknown` and requires positive containment
proof before a higher-fenced replacement can start.

The per-project probe additionally gates the first claim, including an Epic
planning claim, because a project must have a proven delivery path before the
host takes any of its work. After a daemon interruption, the next claim requeues
assigned Progress work once its lease is free and issues a higher fencing token.

### systemd deployment

Use the product-owned host controller for the first install and every update.
The Coding example is:

```bash
bash scripts/remote-runner-onboard.sh \
  --host <ssh-target> \
  --server <task-server-url> \
  --topology central \
  --runner-id <enrolled-runner-id> \
  --runner-name agent-runner-01 \
  --role coding \
  --git-remote <fetch-url> \
  --git-push-remote <push-url>
```

Use `--role review`, a separate enrolled identity, and a separate credential
file to install or update `agent-runner-review.service`. The controller derives
role resource policy, writes it into the main unit, migrates legacy resource
drop-ins, runs `daemon-reload`, and restarts the selected service. Do not copy
`deploy/systemd/agent-runner.service` directly for a managed host; that file is
the legacy static unit reference and cannot derive a host quota.

At minimum, `runner.env` sets `RUNNER_SERVER_URL`, `RUNNER_ID`, `RUNNER_NAME`,
`RUNNER_GIT_REMOTE`, and `RUNNER_GIT_PUSH_REMOTE`. A networked deployment also
sets `RUNNER_AUTH_TOKEN_FILE`; the credential itself stays in that separate
protected file. `RUNNER_CLIENT_ID` remains optional attribution. The controller
installs `agent-host.service` and declares the transitional `agent-runner.service`
alias; new operations target `agent-host.service`. The unit restarts after
failures, logs to journald,
requests graceful SIGTERM drain, and best-effort starts
`~/bin/stack-start.sh` before the daemon so host-local screenshot runs have a
clean Mode-A Studio stack.

The Coding and Review units also load
`/etc/agent-runner/provider-auth.env` after their role-specific runner
EnvironmentFile. Keep the provider file separate from `runner.env` so ordinary
configuration updates cannot expose or overwrite provider credentials.

The managed units deliberately use `KillMode=process`. This is required:
`control-group` kills detached job workers and makes safe reattachment
impossible. `StartLimitIntervalSec=300`, `StartLimitBurst=5`, and
`RestartSec=10s` bound a broken-binary restart loop while allowing ordinary
recovery. Installing or changing the unit requires root, followed by
`systemctl daemon-reload`.

### Planned daemon restart and deploy

A planned Runner deploy no longer waits for host idle. On a hardened host, stage
the complete application and invoke the no-argument deploy helper as described
in section 2. The helper records the previous release, validates the dependency
closure, runs the service-user boot smoke check, atomically switches
`/opt/agent-host/current`, restarts both main service processes, and watches for
an immediate restart loop.

```bash
sudo /usr/local/sbin/agent-runner-deploy
sudo journalctl -u agent-host --since '-2 minutes' \
  | grep -E 'planned shutdown|persisted attempt accepted|recovered .* persisted slot|releasing dead persisted attempt'

sudo journalctl -u agent-runner-review --since '-2 minutes' \
  | grep -E 'planned shutdown|review daemon handoff|persisted review accepted|adopting persisted review|review adoption failed'
```

On SIGTERM the old daemon stops making claims, leaves detached coding and review
workers running, flushes its already-atomic slot records, and exits. systemd
starts the replacement, which verifies and reattaches those workers before
opening any freed slot to claims. For Coding, confirm every occupied slot reports
either `persisted attempt accepted` or `releasing dead persisted attempt`; the
latter must be followed by a Ready card and a later higher-fence claim. For
Review, confirm `review daemon handoff` is followed by `persisted review
accepted` and `adopting persisted review` under the same attempt and fence. A
`review adoption failed` line must be followed by an accepted
`ExecutorRestarted` infrastructure report with explicit loss extent and retry
reason. Do not change either unit back to `KillMode=control-group`. Retain every release referenced by a
daemon or detached worker; garbage collection is a separate, process-aware
operation. If post-restart observation fails, use the exact rollback one-liner
printed by the helper. Rollback switches `current` to the recorded previous
release and restarts both daemons. It never copies old files over the active
release.

This procedure covers a planned daemon binary restart, not a machine reboot,
power loss, Task Server authority restart, or forced `SIGKILL`. Those cases
still use the existing fenced containment and `process-unknown` recovery
contracts.

## 5. Run one task end-to-end for diagnostics

1. On the **local board**, assign the ready task to project **`agent-runner-01`**
   (the project the remote runner serves) and note its task key.
2. On the **runner host**:

   ```bash
   export RUNNER_SERVER_URL=http://<studio-host>:5030
   export RUNNER_GIT_REMOTE=<origin>
   export RUNNER_BRANCH=task/<the-task-branch>     # optional; falls back to base
   /opt/agent-host/current/agent-host <TASK-KEY>
   ```

The runner then, in order: **preflights connectivity** (probes `/healthz`, so a
dropped tunnel is reported cleanly *before* any lease or CLI work), **registers
its client identity** (see below), acquires the fenced lease, starts
heartbeating, checks out the branch from origin, fetches `prompt.md` over the
API, spawns the CLI in the working tree, journals and ships stdout/stderr,
journals everything under `results/`, secures the exact result on an immutable
remote ref, obtains the durable Task Server acknowledgement, removes the
worktree, posts the idempotent fenced completion, and releases the lease.
Exit code `0` means a clean handoff; `1` a
blocked/needs-input outcome; `2` lease not granted; `3` lease lost mid-run; `4`
the task server was unreachable or rejected a call.

For unattended operation, run `agent-host --health-check` as a readiness probe
(exit `0` reachable, `4` not) before assigning a task, and keep the tunnel up as
a service. Both are covered in
[remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md).

### Durable result handoff before teardown

The runner never removes a checkout that contains work available only on the
host. This rule applies to success, missing terminal sentinels, failure,
cancellation, timeout, graceful systemd shutdown, and debris recovered when the
next process starts:

1. Inspect `git status` in the task worktree.
2. Commit dirty and untracked files with
   `wip(runner): salvage before teardown - outcome <X>`.
3. Preserve the moving salvage ref at `runner/<runner-id>/<task-key>` using only
   a normal create or fast-forward push.
4. Publish the exact result to
   `refs/heads/agent-studio/results/<run-attempt-id>/<result-sha>` and verify that ref.
5. Send the immutable result envelope. It binds repository ID, source
   RunAttempt ID, base SHA, result SHA, immutable ref, artifact-manifest digest,
   and applicable submodule or LFS identities.
6. Persist the matching Task Server acknowledgement locally.
7. Remove the linked worktree only after that acknowledgement. A local commit
   or the moving salvage branch alone is not sufficient.

If upload, push, the connection, the runner process, or the Task Server fails,
the daemon replays the original monotonic outbox only after authority has been
reconciled and before claiming new work. Idempotency keys make a lost response
safe. A transfer failure is retried without starting the coding CLI and without
consuming coding or completion budget. `transfer-recovery` means the host still
owns recoverable transfer work.

Operators can inspect durable server projections without reading host files:

```bash
curl -sS https://tasks.example.com/api/v1/management/status
curl -sS https://tasks.example.com/api/v1/management/outboxes
curl -sS https://tasks.example.com/api/v1/runs/<run-id>/result-handoff \
  -H 'X-Task-Protocol-Version: 2'
```

The status projection includes total backlog, oldest unacknowledged sequence,
and counts by final handoff state. Each outbox row includes its RunAttempt.
Result envelopes are retained through task completion and for at least
`TaskServer:ResultRetentionDays` afterward, default 30 days. The Task Server
runs the result-ref GC sweep immediately after startup and every
`TaskServer:ResultRefGcSweepMinutes` afterward, default six hours. Each pass is
bounded by `TaskServer:ResultRefGcBatchSize`, default 50. The sweep deletes an
immutable ref only when all of these facts hold:

- the retention deadline passed;
- the card is accepted in `6-completed` or `7-archive`;
- the matching review produced a terminal `Pass` or `ProductFailure` report
  and has no queued, leased, or process-unknown retry;
- a newer result-bearing RunAttempt exists for the card, so the source attempt
  is no longer the current review subject; and
- the ref exactly matches
  `refs/heads/agent-studio/results/<run-attempt-id>/<result-sha>`.

The newest result-bearing RunAttempt is retained even for an accepted or
archived card. This keeps current review and integration recovery
materializable. A missing
repository URL, a malformed ref, a Git error, or an unavailable credential
spares the ref. The Task Server service account therefore needs permission to
delete only the `refs/heads/agent-studio/results/**` namespace on each
registered origin. Repository URLs are never written to the sweep log. Every
pass emits one structured `result-ref-gc` line per deleted, spared, or failed
ref plus a summary, and successful deletion is persisted in the GC ledger.
Set `TaskServer:ResultRefGcEnabled=false` to pause remote deletion while
retention and safety facts continue to accumulate.

On a later pickup, the retained local tip and the existing canonical salvage
ref are compared by ancestry. Local-ahead is published by normal fast-forward;
remote-ahead remains authoritative. If the tips diverge, the runner leaves the
canonical ref unchanged and publishes the retained local tip to the deterministic
collision ref
`runner/<runner-id>/<task-key>-collision-<local-sha>-<remote-sha>`. It verifies
both exact tips, then prepares the new checkout from the canonical SHA and starts
the CLI. Repeating the pickup reuses the same collision ref and creates no extra
history. No force push is used.

The result facts retain the salvage branch and commit. A divergent recovery also
records the collision ref, canonical and local SHAs, and the typed recovery
action. Each transfer pass makes three publish attempts. Exhaustion leaves the
worktree and both tips untouched, records `transfer-recovery`, and retries a
transfer pass with backoff. It does not complete, move, requeue, or launch a new
coding attempt. Do not delete that path manually. Restore origin access and let
the outbox converge.

### Local-profile client attribution

This section applies only to the loopback or protected-tunnel `local` profile.
For an internet-facing `networked` Task Server, open registration is disabled;
use the owner-authorized enrollment and `RUNNER_AUTH_TOKEN_FILE` flow in
[networked-task-server.md](./networked-task-server.md). In both profiles,
`X-Client-Id` is attribution and never authentication.

The local-profile Task Server guards every mutation behind an `X-Client-Id` registration
boundary (`ClientIdentityMiddleware`): a POST from an id the server has never
seen is rejected `401 client-unknown`. Reads (prompt fetch) stay open, but the
lease, log, artifact, and completion writes do not. Product onboarding
sets `RUNNER_CLIENT_ID` to the existing host identity. Startup authenticates a
`GET /api/clients/{id}` with that header before its first write; an unknown or
retired id is a hard error, and the runner does not create a replacement. This
verification also refreshes `LastSeen` through the normal middleware path.

When `RUNNER_CLIENT_ID` is omitted for a manual/legacy install, the runner
self-registers before its first write: it POSTs `RUNNER_NAME` to
`/api/clients/register` (an open-path route) and adopts the server-assigned id.
Registration is idempotent on the name. A `401 client-unknown` therefore points
to a wrong configured id or a reverse proxy stripping `X-Client-Id`.

## 6. Acceptance walkthrough

The task passes RM-5 acceptance when, after the runner exits `0`:

- a successful sentinel has moved the card on the **local** board to
  `4-auto-review` with an `agent_run_finished` timeline entry sourced from
  `agent-runner-01`, and no `external_completion` provenance;
- `logs/cli-output.log` on the server shows the remote CLI output; and
- the uploaded evidence is present under the task's `results/` folder and in the
  workspace evidence commit.

For the full Execution Hosts acceptance, also record the setup task id, the exact
Task Server URL/topology, `systemctl is-enabled` and `is-active`, provider-auth
badge states and probe details, and the runner client id from `GET /api/clients`. Its
`lastSeenAt` must become fresh after the daemon begins polling. Finally assign a
Ready probe task through the normal project execution setting and verify that
the remote host badge, fenced lease timeline, CLI log upload, result upload,
and runner completion all name the same runner. This is the AGT-1923 probe
mechanic; do not substitute the static frontend readiness fixture for this
proof.

## Troubleshooting

- **`identity-file-corrupt` or a runner identity that used to return `404`** -
  inspect the Execution Hosts diagnostic and the backend log for the exact file
  under `<TaskRepository>/identities/`. Restore that JSON file from a known-good
  backup or Git revision when one exists. For the local security profile, the
  bounded fallback is to re-register the original display name through the open
  registration route:

  ```bash
  curl -fsS -X POST "$RUNNER_SERVER_URL/api/clients/register" \
    -H 'Content-Type: application/json' \
    -d '{"displayName":"agent-runner-01","kind":"service"}'
  ```

  Registration is idempotent on `displayName`; verify that the returned `id`
  matches `RUNNER_CLIENT_ID`. A restored file is picked up by the next targeted
  `GET /api/clients/{id}` or Clients-list reload, so the backend does not need a
  restart. Networked Task Servers keep open registration disabled; restore the
  server-owned identity or repeat the owner-authorized enrollment flow instead.
- **No task is claimed** - confirm the project's `executionRunner` exactly
  matches `RUNNER_NAME` or `RUNNER_ID`, `remoteExecutionEnabled` is true, and
  the card is pickup-eligible in `2-ready`. Also inspect the backend log for
  `remote-runner-project-skipped`: the project needs a network URL in its `repo`
  registry entry or an origin derivable from `RepositoryPath`. The local runner
  intentionally skips the same assigned project.
- **`lease not granted: Held` in one-task mode** - another runner already holds
  the task. The daemon claim path normally avoids this before launch.
- **`Project delivery preflight failed`** - read the full reason on both the
  Execution Hosts card and the project's Execution card. Run the printed failing
  Git operation on the host against the registered repository URL and confirm
  the named integration branch exists. Repair that repository's credential,
  branch, or registration, then choose **Re-Probe** on the host card or wait for
  the five-minute proof expiry. A fallback-repository warning is diagnostic and
  does not override a successful project delivery preflight.
- **`connection lost: cannot reach the task server ...` at startup** - the
  preflight `/healthz` probe failed, almost always a dropped reverse tunnel.
  Confirm with `agent-host --health-check`; if it also exits `4`, restart the
  tunnel service ([remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md)).
  The runner refuses at preflight by design, so no half-started lease or CLI is
  left behind.
- **Execution Hosts shows `Task Server route unreachable`** - the
  `task-server:connectivity` advertisement is stale or explicitly unavailable.
  This is the board-visible transport alarm even when the host itself is still
  running, because a broken route cannot carry a fresh failure report through
  itself. For the Windows-to-Linux reverse-tunnel topology, inspect
  `%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper\events.log`, then run the
  functional host-side curl from
  [Remote runner: persistent connection](./remote-runner-persistent-connection.md).
  Repair the route instead of restarting the review daemon.
- **`lease lost: StaleToken` mid-run** - a TTL takeover happened; the network was
  slow enough that heartbeats missed the window. Raise `RUNNER_TTL_SECONDS` /
  lower `RUNNER_HEARTBEAT_SECONDS`, or check the tunnel.
- **`Task '<key>' has no prompt.md`** - the task key did not resolve on the
  server, or the job folder has no prompt. Confirm the key against the board.
- **No output shipped** - the server rejects logs for an unknown task key; the
  console still shows the CLI output locally. Check `RUNNER_SERVER_URL` and the
  task key.
- **CLI exits immediately / wrong flags** - the headless flags do not match the
  installed CLI version. Adjust `RUNNER_CLI_ARGS` or wrap the CLI in a script.
- **`outcome Unknown` after a substantive final reply** - first confirm the run
  log contains `remote-completion-protocol appended to task prompt`. Its absence
  means the host is running a pre-AGT-2148 runner build. If the line is present,
  inspect the final stdout event and verify that the agent emitted one canonical
  `[[TASK_*]]` token; Codex JSONL is supported directly and stdout is not tail
  truncated by the runner.
- **`401 client-unknown` on a lease/log/upload call** - the runner's
  `X-Client-Id` never reached the server, so it looks unregistered. The runner
  registers itself automatically, so this almost always means a reverse proxy or
  tunnel is dropping the `X-Client-Id` request header; forward it, or point
  `RUNNER_SERVER_URL` straight at the Studio.
- **`capability-advertisement registration=lost` after a backend restart** -
  the backend's compatibility registry was reset. The daemon now repeats its
  idempotent runner registration and retries the same capability generation.
  Repeated transport failures remain bounded by the request timeout and normal
  connectivity backoff.
- **`daemon-idle-watchdog status=fatal`** - a slot-free daemon did not start a
  claim poll within `RUNNER_IDLE_WATCHDOG_MINUTES`. It exits deliberately so
  `Restart=always` can replace a process whose main loop is no longer making
  progress.
## Reading host telemetry

The runner samples the host every 30 seconds and piggybacks the sample on its existing Task Server claim poll. The Execution Hosts view keeps CPU, memory, Linux load averages, swap traffic, CPU steal time, I/O wait, core count, active runner slots, and the last locally observed Task Server connection state together. Use the 1h, 6h, 48h, and 14d controls to compare load with concurrency. For example, `6 active slots · load 6.4 of 12 cores` is direct evidence for whether the current slot limit leaves headroom.

Task Server reachability has two complementary signals. The telemetry snapshot
contains the daemon's local observation, failure start, consecutive failure
count, escalation time, last error, and last recovery. The
`task-server:connectivity` capability carries a three-minute freshness deadline.
Freshness is the load-bearing remote alarm: when the route is down, the Task
Server cannot receive another telemetry sample, so the last sample must not be
misread as proof that the route remains healthy. The host card marks the route
unreachable as soon as the connectivity capability expires.

Linux values come from `/proc/stat`, `/proc/loadavg`, `/proc/meminfo`, and `/proc/vmstat`. Windows runners report CPU and memory where the operating system exposes them without an additional agent; Linux-only fields remain empty. Raw 30-second samples are retained for 48 hours. Older samples are compacted into five-minute averages and retained for 14 days. The series is persisted below the workspace store in `telemetry/<client-id>.json`, so a backend restart does not erase it.

The host card raises these sustained findings after at least three qualifying
samples. Up to two non-qualifying sample intervals are bridged so load that
flaps around a boundary remains one phase. The card shows at most one active
finding per kind. Completed phases are summarized per kind as an occurrence
count for the selected time window instead of appearing as individual badges.

- **VM throttled**: CPU steal time stays above 5 percent. On a virtual machine, this means the hypervisor is withholding scheduled CPU time.
- **Oversubscribed**: the one-minute load average stays above 1.5 times the
  reported core count and either CPU steal exceeds 5 percent or I/O wait exceeds
  10 percent. Review work is cgroup-capped and can remain runnable without
  displacing Coding, so high load alone is deliberately not treated as damage.
- **Memory pressure**: combined swap-in and swap-out traffic stays above 64 KiB/s. A single historical swap allocation without traffic does not trigger this finding.

Short spikes remain visible in the quiet history chart but do not create a badge. Check I/O wait alongside CPU when load is high: high load with low CPU and elevated I/O wait usually points to storage contention rather than missing cores.

Coding claim admission uses the same one-minute load sample. New Coding claims
stop after load divided by logical CPU cores remains above
`RUNNER_CLAIM_MAX_LOAD_PER_CORE` (default `1.5`) for
`RUNNER_LOAD_GATE_SUSTAINED_SECONDS` (default `120`). Review admission checks a
fresh sample immediately before every claim and admits at most one new slot per
poll only while `Load1 < CpuCores * RUNNER_CLAIM_MAX_LOAD_PER_CORE`. Missing
Linux load evidence closes Review admission. Existing Coding and Review runs
continue. Every immutable ReviewPlan also limits `dotnet test` to two MSBuild
nodes and disables xUnit test-collection parallelism, including baseline and
retry executions.
