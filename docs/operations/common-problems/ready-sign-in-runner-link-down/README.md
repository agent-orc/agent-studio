---
id: ready-sign-in-runner-link-down
title: "Ready shows waiting for sign-in but the host is logged in"
status: fixed
first-seen: 2026-09-06T00:00:00Z
last-seen: 2026-09-06T00:00:00Z
severity: blocker
category: runner
tags: [ready, provider-auth, runner, tunnel, heartbeat, windows]
affects: [frontend, backend, deploy/windows/agent-runner-tunnel]
related-tasks: [AGT-2711]
related-adrs: []
---

# Ready shows waiting for sign-in but the host is logged in

**What.** Every Codex card in Ready said it was waiting for sign-in on
`agent-runner-01`, while `codex login status` on that host reported a valid
ChatGPT login. Both runner services were actually restarting with exit code 4
because `http://127.0.0.1:15031` refused the Task Server connection.

**Why.** The Windows `AgentRunner-TunnelKeeper` Scheduled Task was disabled.
There was no `ssh.exe` reverse forward and no listener on the runner's port
15031. The UI mapped every unknown provider-auth badge to sign-in guidance,
even though an unknown badge meant that no fresh runner probe had arrived.

**Diagnosis now.** Check the Link column in Execution Hosts first. Its state,
heartbeat time, and last error come from the Task Server link resource. On the
Task Server workstation, read the same resource directly:

```sh
curl -fsS -H 'X-Client-Id: local-default' \
  http://127.0.0.1:5031/api/v1/management/links
```

For transition history, read the workspace operator feed's `runner-link`
events. On the runner, confirm both services and the forwarded endpoint:

```bash
systemctl status agent-runner.service agent-runner-review.service
curl -fsS http://127.0.0.1:15031/healthz
```

**Recovery now.** Use **Reconnect** in Execution Hosts. The Task Server's
`LinkSupervisor` replaces its SSH child and waits for a fresh runner heartbeat;
it does not start the Scheduled Task. If reconnect fails, inspect the link
resource's `lastProbe` and `lastError`, then follow
[Remote runner: persistent connection](../../setup/remote-runner-persistent-connection.md).

**Fixed behavior.** Sign-in text now requires two explicit logout probes.
Unknown or stale auth state reports runner/link unreachability. Execution Hosts
and Ready-card wait reasons read the Task Server link resource. The Windows
keeper is disabled and retained only as the documented emergency path.
