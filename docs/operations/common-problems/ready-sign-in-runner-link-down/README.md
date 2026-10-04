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

**Why.** On 2026-09-06, the Windows `AgentRunner-TunnelKeeper` Scheduled Task
was disabled. There was no `ssh.exe` reverse forward and no listener on the
runner's port 15031. The UI mapped every unknown provider-auth badge to sign-in
guidance, even though an unknown badge meant that no fresh runner probe had
arrived. The Task Server's `LinkSupervisor` took ownership of this route on
2026-09-25; the keeper remains disabled.

**Diagnosis.** Check Execution Hosts and
`GET /api/v1/management/links` first. The link resource supplies `state`,
`lastHeartbeatAt`, `lastProbe`, and `lastError`. Check the operator feed for
timestamped `link_down` and `link_up` transitions. On the runner, confirm both
services and the tunnel endpoint:

```bash
systemctl status agent-runner.service agent-runner-review.service
curl -fsS http://127.0.0.1:15031/healthz
```

**Recovery.** Use **Reconnect** in Execution Hosts. It asks the configured
link supervisor to replace the route without handling credentials. The runner
services heal after the listener returns and the next capability advertisement
changes the resource to `up`. If Reconnect fails, inspect `lastError` and the
link log under `<TaskRepository>/.logs/runner-links/`, then follow
[Remote runner: persistent connection](../../setup/remote-runner-persistent-connection.md).

**Fixed behavior.** Sign-in text now requires two explicit logout probes.
Unknown or stale auth state reports runner/link unreachability. Execution Hosts
reads the link resource for state and recovery controls. The Windows keeper
scripts remain a documented emergency path only.
