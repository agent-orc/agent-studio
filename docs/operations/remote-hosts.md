# Execution hosts runbook

This runbook is the operator path for adding, connecting, draining, retiring,
reviving, and permanently removing a remote agent host. The detailed Linux
installation reference remains [linux-runner-host.md](setup/linux-runner-host.md).

## Add a host

Open **Workspace Settings > Execution Hosts > Add execution host**. The wizard introduced in
AGT-1922 asks for a stable runner name such as `agent-runner-01` and an SSH
target such as `runner@host.example.com`.

On Ubuntu, install the base tools and the agent host dependencies:

```bash
sudo apt-get update
sudo apt-get install -y git curl build-essential
npm i -g @anthropic-ai/claude-code @openai/codex
npx playwright install --with-deps chromium
```

Provision Claude authentication in the wizard, or afterward with **Sign in
Claude** on the host's provider badge (see [Provider
auth](#provider-auth) below). The wizard's manual path sends
`CLAUDE_CODE_OAUTH_TOKEN` or `ANTHROPIC_API_KEY` through SSH stdin and installs
`/etc/agent-runner/provider-auth.env` as `root:agent` mode `640`. The value is
never retained in Studio, a task, or the repository. Both runner units load the
same file; the runner probe verifies only the process environment and CLI
status. Do not copy credential files from the operator workstation.

## Give the host push identity

The organization must allow repository deploy keys. Generate a unique key for
each host and repository:

```bash
install -d -m 700 ~/.ssh
ssh-keygen -t ed25519 -f ~/.ssh/agent-studio-deploy -C 'agent-runner-01:agent-taskboard' -N ''
cat ~/.ssh/agent-studio-deploy.pub
```

Register the printed public key on the repository with write access, using the
repository API or its **Settings > Deploy keys** UI. Then configure SSH to use
that key and keep fetch and push identities separate:

```sshconfig
Host github-agent-studio
  HostName github.com
  User git
  IdentityFile ~/.ssh/agent-studio-deploy
  IdentitiesOnly yes
```

```bash
git -C /opt/agent-host/source remote set-url origin https://github.com/ORG/REPO.git
git -C /opt/agent-host/source remote set-url --push origin git@github-agent-studio:ORG/REPO.git
git -C /opt/agent-host/source push --dry-run origin HEAD
```

Set the same URLs for the daemon:

```bash
RUNNER_GIT_REMOTE=https://github.com/ORG/REPO.git
RUNNER_GIT_PUSH_REMOTE=git@github-agent-studio:ORG/REPO.git
```

The host card shows the fallback-repository probe separately from project
delivery. A failed fallback probe does not classify every project as read-only
and does not suppress claims whose own repository preflight succeeds.

AGT-2141 added per-project repository URLs and isolated shared clones. Configure
each project repository to move from one remote project to all projects remote.
The fallback URLs above remain startup probe inputs only. Each project clone
uses its registry URL for both fetch and push and repairs both values on every
refresh.

The first claim for each host/project pair, and the first claim after a
five-minute proof expiry, is a preflight offer, not a lease.
The daemon prepares the project's real shared clone, requires its fetch and push
URLs to match the registered repository URL, fetches, verifies the exact
integration branch, then creates and removes a temporary runner ref. This real
write exercises server-side hooks and permissions that a dry-run can miss. A
failure or missing repository URL keeps only that project's card Ready and
appears with its target branch and reason on both the host card and the
project's Execution card. Other projects on the host continue independently.

## Connect the daemon

Register the host identity once and use its returned `id` as
`RUNNER_CLIENT_ID`. Every daemon request presents that value as `X-Client-Id`:

```bash
curl -sS -X POST https://tasks.example.com/api/clients/register \
  -H 'Content-Type: application/json' \
  -d '{"displayName":"agent-runner-01","kind":"service"}'
```

Write `/etc/agent-runner/runner.env` with at least:

```bash
RUNNER_SERVER_URL=https://tasks.example.com
RUNNER_ID=agent-runner-01
RUNNER_NAME=agent-runner-01
RUNNER_CLIENT_ID=agent-runner-01
RUNNER_GIT_REMOTE=https://github.com/ORG/REPO.git
RUNNER_GIT_PUSH_REMOTE=git@github-agent-studio:ORG/REPO.git
# Seeds a newly registered host and remains the fallback for an older server.
RUNNER_MAX_PARALLELISM=2
# Optional repository-specific requirements:
RUNNER_REQUIRED_CAPABILITIES=toolchain:dotnet,toolchain:node,toolchain:playwright
```

Enable and verify the service:

```bash
sudo systemctl enable --now agent-host
sudo systemctl is-active agent-host
sudo journalctl -u agent-host -n 100 --no-pager
curl -sS https://tasks.example.com/api/clients/agent-runner-01
```

The host card reports daemon state (`running`, `read-only`, or `stopped`), last
claim, active slots, task inflow, last contact, and push status. Active slots
come from the daemon's latest telemetry sample. If that sample or the host
heartbeat is older than five minutes, the last slot value is explicitly marked
stale and rendered quietly instead of being presented as live.

The same card shows the server-owned **Runtime capacity** policy. Change the
slot ceiling, target load, or ramp strategy there and choose **Apply**. The
Task Server versions the update, enforces the ceiling across Coding RUN leases
on the host, and returns it on the next claim poll. The Coding daemon adopts it
without a restart. Existing work continues when the ceiling is lowered; only
new admission stops. The card distinguishes the central ceiling from the last
version explicitly confirmed by the daemon. A matching value alone is not an
acknowledgement. The Task Server records `runtime-capacity.updated` and the
first matching runner poll records `runtime-capacity.applied`, both with actor
and timestamp in the audit ledger. Canonical Remote review commands are
admitted by the Review Executor and do not consume a Coding RUN slot. The host
card no longer projects a separate process-local GATE pool.

`RUNNER_MAX_PARALLELISM` is no longer the live operator control for a versioned
Task Server. It seeds the first registration and remains a compatibility
fallback. Subsequent file changes do not replace the central policy. The daemon
also writes the last accepted policy to
`RUNNER_STATE_DIR/configuration/runtime-capacity.json`. This file is an atomic
last-known-good cache, not an operator configuration surface. A replacement
daemon on the same host restores it before connecting, while the next valid
Task Server response remains authoritative.

Provision a host policy before the first daemon connects by using expected
version `0`:

```bash
curl -sS -X PUT https://tasks.example.com/api/v1/hosts/build-host-02/runtime-capacity \
  -H 'Content-Type: application/json' \
  -H 'X-Client-Id: local-default' \
  -d '{"maxParallelism":4,"targetLoadPercent":80,"rampStrategy":"balanced","expectedVersion":0}'
```

The first Coding runner registered with `hostId=build-host-02` receives that
record instead of seeding a value from its environment. During a Task Server
outage, no new work is claimed and no policy is invented locally. Already
running work continues only inside its existing lease safety window, using the
in-memory or cached last-known-good capacity until normal fenced reconciliation
resumes.

The same host card owns **Project access**. Select all projects or enter the
stable Task Server project ids that this host may claim. The Task Server stores
the allowlist as a separate versioned host policy and applies it to both direct
claims and host-orchestrator work permits. The daemon does not resolve or cache
this list because the server already owns task selection. Removing a project
stops new admission but never interrupts an active fenced lease.

An absent policy preserves migration compatibility and allows all projects. An
explicit selected-project policy with an empty list blocks every new claim.
Creation uses expected version `0`; later writes use the version returned by
the server:

```bash
curl -sS -X PUT https://tasks.example.com/api/v1/hosts/build-host-02/project-policy \
  -H 'Content-Type: application/json' \
  -H 'X-Client-Id: local-default' \
  -d '{"allowAllProjects":false,"allowedProjectIds":["PROJ-002"],"expectedVersion":0}'
```

Unknown project ids are rejected, stale writes return a version conflict, and
the audit ledger records `host-project-policy.created` or
`host-project-policy.updated` with actor and timestamp. Capability health is
not copied into this policy: the existing Task Server capability advertisement
and canary admission remain the single capability mechanism.

The workspace status bar defines `running` from the Board's `3-progress`
snapshot: a local run needs a running process execution and a remote run needs
an active fenced lease. Its tooltip shows the total as local plus remote. The
Remote Hosts view independently shows telemetry `activeSlots`; when fresh
telemetry and Board leases disagree, both surfaces show a warning icon and keep
the two values visible instead of silently choosing one.

## Capability admission

Each coding and review service refreshes a versioned capability advertisement
every minute. The Task Server treats an advertisement as fresh for three
minutes. New claims declare their role, provider authentication, source,
repository, disk, Task Server connectivity, and any configured toolchain
requirements. Review claims also add the immutable subject's semantic, vision,
Git, or source-bundle requirements.

A first correlated fault marks one capability suspect. Repetition drains that
capability and starts a bounded cooldown. When cooldown expires, exactly one
matching claim becomes the half-open canary. An authoritative typed coding
terminal, with an immutable handoff when required, or an authoritative
non-infrastructure review report reopens normal capacity. Product findings
prove that review infrastructure recovered without becoming a product pass.
Canary failure returns to a longer cooldown. Do not lower
the central runtime capacity as a repair. Healthy capabilities and unrelated
services on the same host continue using the configured slots.

Execution Hosts shows the capability state, reason, first and last failure,
cooldown, canary claim, affected coding and review attempts, and recovery
history. A stale advertisement is explicitly stale. AGT-2142 telemetry appears
as live meters only while both its sample and the host heartbeat are fresh.
Coding and Review processes that advertise the same host id appear below one
physical-machine row. Their role-local slot ceilings stay separate, while CPU,
memory, activity, and release identity appear once for the machine. Missing
values use a quiet dash rather than an empty meter.

Automatic whole-host drain is reserved for shared foundations: disk full,
invalid lease authority, host network isolation, repository filesystem
corruption, or Task Server authority uncertainty. The UI labels it
**Automatic whole-host drain**. An operator action is stored and labeled
separately as **Operator-requested host drain**. Both block new claims, but
neither kills an existing lease.

## Provider auth

Every host holds its own Claude and Codex session; never copy a credential
file from another machine (see the runbook's [prohibited recovery
paths](setup/cli-relogin-runbook.md#5-prohibited-recovery-paths)). Both
providers now have a host-owned interactive sign-in reachable from the badge
or a Ready-card wait chip: **Sign in Claude** and **Sign in Codex** (AGT-2712,
AGT-2759). Each starts a bounded SSH session as the runner user, shows only a
browser verification URL (plus a one-time code for Codex), and polls to
completion; the resulting credential is written on the host and never returns
to Studio. Full steps live in the [renewal
runbook](setup/cli-relogin-runbook.md).

The `provider-auth:<cli>` capability is backed by a real periodic probe
(`claude auth status --text` / `codex login status`), cached for a few
minutes, not a static advertisement. While that probe reports the host as
logged out, the capability-admission gate in [Capability
admission](#capability-admission) above keeps both new claims and review
commands off it, and a review command that still hits an auth failure is
classified as review infrastructure (`ReviewInfra`), never a product failure.

**Process hygiene (AGT-2759).** A review or coding worker that dies without a
live daemon watching it can leave its CLI child process running with no
attempt left to report against. The review and coding daemons reap any such
process rooted in the dead attempt's workspace as soon as reconciliation or
report cleanup notices the worker is gone, logging
`cli-process-reaped pid=<pid> age=<seconds>s attempt=<id>`. A daemon startup
and hourly sweep additionally kills any tracked-CLI process whose working
directory was already removed, or that has run longer than the maximum review
dormant age, independent of any attempt record. The running total is exposed
in host telemetry.

## Drain

Use **Drain** before maintenance. Drain blocks new leases immediately. Running
tasks keep their leases and finish normally. The host remains visible and
drained afterward. The equivalent API call is:

Drain is deliberately not a live migration command. Runner assignment changes,
immediate stop-and-switch, cross-host continuation, and the historical A → B → A
route follow the
[runner provenance and host handoff contract](../concepts/completion-review-and-remote-runner-stability.html#provenance).

```bash
curl -sS -X POST https://tasks.example.com/api/clients/agent-runner-01/drain \
  -H 'X-Client-Id: local-default'
```

### Restarting a Review Executor: drain, never restart

The server-side drain above blocks new leases but does not stop the daemon
process. To stop or restart a Remote Review Executor host, drain it on the host
instead. A running review carries thirty to sixty minutes of gate work in a
detached worker, and restarting on top of it loses that work and requeues the
card.

```bash
sudo /usr/local/sbin/agent-runner-deploy drain     # stop claiming, finish, exit
sudo /usr/local/sbin/agent-runner-deploy           # promote a release
```

The host-side drain waits for a request-id-matched daemon acknowledgement
before trusting the slot census. That acknowledgement is written only after an
already-started claim has returned, so an empty census cannot race a claim in
flight. Guarded replacement keeps that acknowledged admission barrier in place
through old-MainPID exit and resolves the live state directory from that
process, not from whichever legacy environment file happens to exist. An
unreadable slot record fails closed. A successful drain leaves the
Review service stopped; release promotion or an explicit
`agent-runner-deploy restart-review` clears the completed drain state and starts
the next generation.

A direct `systemctl stop` or `systemctl restart` is always refused for Review.
Use `agent-runner-deploy restart-review` when the role is idle; it prints the
drain hint when slots are busy. A root operator's explicit override is
`agent-runner-deploy restart-review --force`. Release promotion uses the same
guarded MainPID handoff path. The full procedure, the log lines to check, and
the deliberate fail-closed first-upgrade procedure for daemons that cannot yet
acknowledge the admission barrier are in
[Restart, drain, handoff](setup/linux-runner-host.md#restart-drain-handoff).

## Retire, revive, delete

### Retire

Use **Retire**, read the confirmation, then choose **Drain and retire**. If work
is running, the server drains first and changes the identity to `retired` only
after the daemon reports zero active slots. With zero active slots it retires
immediately. Retired roles are hidden by default. Use **Show retired** in the
table summary to reveal them; historical attribution remains intact.

```bash
curl -sS -X POST https://tasks.example.com/api/clients/agent-runner-01/retire \
  -H 'X-Client-Id: local-default'
```

### Revive

Choose **Show retired**, then **Revive** on the role row. Start or re-register the daemon
afterward so `LastSeenAt`, daemon state, and the push probe become fresh again.

```bash
curl -sS -X POST https://tasks.example.com/api/clients/agent-runner-01/revive \
  -H 'X-Client-Id: local-default'
sudo systemctl restart agent-host
```

### Delete permanently

Choose **Show retired**, then **Delete** on the retired role row (next to
**Revive**, and also available from the expanded row's Connection section).
Confirm against the runner's own name; the row disappears immediately, no
reload needed. Deletion is only offered for an already-retired client and
cannot be undone.

The server refuses a delete with `409 Conflict` while any of these hold, so an
operator never loses live state by mistake:

| `error` | Meaning |
|---|---|
| `client-online` | The identity is still reporting active work (active slots, or the daemon reports `running`). |
| `client-has-active-lease` | The client currently holds a live run lease on an in-progress task. |
| `client-attempt-process-unknown` | A run lease for this client expired without a confirmed outcome - the local analogue of the standalone Task Server's `process-unknown` lease state. Resolve the task before retrying. |

A client that has not been retired first is refused with `400 Bad Request`
(`client-must-be-retired-before-delete`), not `409`.

```bash
curl -sS -X DELETE https://tasks.example.com/api/clients/agent-runner-01/permanent \
  -H 'X-Client-Id: local-default'
```

A successful delete writes a structured `client-permanently-deleted` log entry
and an Agent Message Bus `lifecycle` event (workspace-scoped, actor and
display name in the payload) alongside removing the identity file.

### Delete retired in bulk

Leftover e2e runs are the common case: dozens of retired identities named
`e2e-<something>-<timestamp>` that will never be revived. The toolbar's
**Delete retired...** action (shown once at least one retired host exists)
opens a dry-run preview: enter a name/id prefix (defaults to `e2e-`), preview
the matching retired identities and their eligibility, then apply. Each
candidate is evaluated with the exact same guards as the single-row delete;
a still-online or still-leased straggler is reported as blocked instead of
silently skipped, and stays retired for a later sweep.

The same flow is available directly over the API:

```bash
# Dry run (default): preview only, never deletes.
curl -sS -X POST https://tasks.example.com/api/clients/retired/purge \
  -H 'X-Client-Id: local-default' -H 'Content-Type: application/json' \
  -d '{"prefix":"e2e-","dryRun":true}'

# Apply: deletes every eligible match.
curl -sS -X POST https://tasks.example.com/api/clients/retired/purge \
  -H 'X-Client-Id: local-default' -H 'Content-Type: application/json' \
  -d '{"prefix":"e2e-","dryRun":false}'
```

The response lists every matched candidate with `eligible`, `refusalReason`
(one of the codes above, or `null`), and `deleted`, plus a `deletedCount`
total. Playwright specs that register throwaway client identities use the
shared `e2e/helpers/e2e-clients.ts` helper, which prefixes every id with
`e2e-` and retires-then-deletes on teardown, so this prefix reliably targets
every fixture leftover even when a spec's own teardown never ran.
