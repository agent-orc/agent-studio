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

Provision Claude authentication in the wizard. Studio sends
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

## Retire

Use **Retire**, read the confirmation, then choose **Drain and retire**. If work
is running, the server drains first and changes the identity to `retired` only
after the daemon reports zero active slots. With zero active slots it retires
immediately. Retired roles are hidden by default. Use **Show retired** in the
table summary to reveal them; historical attribution remains intact.

```bash
curl -sS -X POST https://tasks.example.com/api/clients/agent-runner-01/retire \
  -H 'X-Client-Id: local-default'
```

## Revive

Choose **Show retired**, then **Revive** on the role row. Start or re-register the daemon
afterward so `LastSeenAt`, daemon state, and the push probe become fresh again.

```bash
curl -sS -X POST https://tasks.example.com/api/clients/agent-runner-01/revive \
  -H 'X-Client-Id: local-default'
sudo systemctl restart agent-host
```

## Retire, revive, delete

The full lifecycle for one identity is retire (above) to stop new claims,
revive (above) to bring it back, or permanent delete to remove the record for
good. Delete is only reachable from an already-retired role: the role row's
overflow menu on a retired runner offers **Delete** next to **Revive**, with a
confirmation naming the runner; the row disappears without a reload.

## Remove permanently

Permanent removal is only available for an already-retired client, and is
refused with `409 Conflict` while the identity still holds a live run lease
(`client-has-active-lease`) - the guard re-derives occupancy from the same
run-lease authority the claim endpoint uses, not from the identity's own
cached telemetry, so a stale slot count cannot let a still-working runner be
deleted out from under its task. A non-retired identity is refused with
`409 Conflict` (`client-must-be-retired-before-delete`); an unknown id
returns `404` (`client-not-found`). The bootstrap `local-default` identity can
never be deleted. Deletion cannot be undone. Use it only after deciding that
the revive path and the visible historical host entry are no longer needed.
Every successful deletion appends a JSONL row to
`<TaskRepository>/identities/.audit/identity-deletions.jsonl` (id, display
name, actor, timestamp, reason) and emits a workspace-level
`client-identity-deleted` event on the agent message bus.

```bash
curl -sS -X DELETE https://tasks.example.com/api/clients/agent-runner-01/permanent \
  -H 'X-Client-Id: local-default'
```

## Delete retired hosts in bulk

e2e fixtures and decommissioned hosts leave a graveyard of retired identities
behind (`e2e-effective-model-...`, `e2e-owner-...`, one-off runner names).
**Delete retired…** in the Execution Hosts toolbar opens a dry-run preview: a
name-prefix filter (defaulting to `e2e-`) lists every matching retired
identity and what would happen to it, before anything is deleted. The same
per-identity guard applies to every candidate - a match that still holds an
active lease is skipped, not force-deleted.

The underlying API is `POST /api/clients/retired/purge` with an optional
`prefix` and a `dryRun` flag (defaults to `true`):

```bash
# Preview only - nothing is deleted.
curl -sS -X POST https://tasks.example.com/api/clients/retired/purge \
  -H 'Content-Type: application/json' -H 'X-Client-Id: local-default' \
  -d '{"prefix":"e2e-","dryRun":true}'

# Apply: permanently deletes every matching retired identity without an active lease.
curl -sS -X POST https://tasks.example.com/api/clients/retired/purge \
  -H 'Content-Type: application/json' -H 'X-Client-Id: local-default' \
  -d '{"prefix":"e2e-","dryRun":false}'
```

Each row of the response reports one outcome: `would-delete` (dry run),
`deleted`, `skipped-active-lease`, or `skipped-not-retired`. An empty or
absent `prefix` matches every retired identity except `local-default`, which
is never a candidate. Playwright fixtures that register an `e2e-`-prefixed
identity call the shared `purgeE2eClients(prefix)` helper
(`frontend/e2e/helpers/api.ts`) from their `afterAll` so this graveyard stops
growing run over run.
