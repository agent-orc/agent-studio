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

**Diagnosis.** Check Execution Hosts first. A stale or down runner link names
the last capability snapshot time and reports the Studio-side keeper cause. On
the runner, confirm both services and the tunnel endpoint:

```bash
systemctl status agent-runner.service agent-runner-review.service
curl -fsS http://127.0.0.1:15031/healthz
```

On Windows, verify the keeper and reverse-forward process:

```powershell
Get-ScheduledTask -TaskName AgentRunner-TunnelKeeper
Get-CimInstance Win32_Process -Filter "Name = 'ssh.exe'" | Select-Object ProcessId, CommandLine
```

**Recovery.** Use **Reconnect** in Execution Hosts. It enables and starts the
configured keeper task without handling credentials. The runner services heal
after the listener returns and the next capability advertisement changes the
link to connected. If Reconnect fails, inspect the keeper log tail shown in the
host details and follow
[Remote runner: persistent connection](../../setup/remote-runner-persistent-connection.md).

**Fixed behavior.** Sign-in text now requires two explicit logout probes.
Unknown or stale auth state reports runner/link unreachability. Execution Hosts
shows connected, stale, or down snapshot state and supervises the Windows
keeper. The registration script preserves the battery, start-when-available,
and logon-trigger hardening.
