# Remote runner: persistent connection

Status: product-owned interim connection for unattended remote operation until
the remote Task Server removes the workstation from the execution path. This is
the companion to [linux-runner-host.md](./linux-runner-host.md).

The current topology deliberately exposes no Task Server port to the network:

```text
[Windows Task Server]                    [agent-runner-01]
127.0.0.1:5031  <--- SSH reverse link --- 127.0.0.1:15031
                                             agent-host polls here
```

The Task Server process owns this link. Its hosted `LinkSupervisor` starts and
stops `ssh`, judges health by runner capability heartbeats, recovers failed
routes, and exposes the state to Studio. A Windows Scheduled Task is no longer
part of normal operation.

## Configure the product-owned link

`RunnerLinks` is empty in `backend/appsettings.json`, so a checkout does not dial
an SSH host by default. Stable's gitignored `backend/appsettings.Local.json`
contains this entry:

```json
"RunnerLinks": [
  {
    "RunnerId": "agent-runner-01",
    "Kind": "ssh-reverse",
    "SshTarget": "agent-runner",
    "RemotePort": 15031,
    "LocalPort": 5031,
    "ExtraForwards": [
      "5031:127.0.0.1:5031",
      "4011:localhost:4011"
    ],
    "HeartbeatTimeoutSeconds": 90,
    "BackoffSeconds": [5, 10, 30, 60, 120]
  }
]
```

`agent-runner` is an alias in the Task Server service account's `~/.ssh/config`.
The key stays there. Configuration contains no address, key path, private key,
or passphrase, and the supervisor never reads key material.

The resulting long-lived command is equivalent to:

```text
ssh -N -T -o BatchMode=yes -o ExitOnForwardFailure=yes \
  -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o LogLevel=VERBOSE \
  -R 15031:127.0.0.1:5031 \
  -R 5031:127.0.0.1:5031 \
  -R 4011:localhost:4011 agent-runner
```

The child uses `CreateNoWindow`, hidden window style, and redirected stdout and
stderr. Each link has a bounded active log plus one rotation under
`<TaskRepository>/.logs/runner-links/`. On Windows, the child is assigned to a
kill-on-close job object. On Linux, it remains in the service cgroup. Graceful
Task Server shutdown also terminates the child explicitly.

## Health and recovery

The resource states are `down`, `connecting`, `up`, `degraded`, `reconnecting`,
and `paused`.

- A capability heartbeat no older than `HeartbeatTimeoutSeconds` is `up`. SSH
  process presence alone never establishes health.
- A late heartbeat changes `up` to `degraded` and triggers one bounded route
  probe: `ssh agent-runner curl --max-time 5
  http://127.0.0.1:15031/healthz`.
- A failed route probe or an exited child starts recovery. The supervisor stops
  its child, uses the established non-sudo `ss` and `kill` sequence to free only
  `127.0.0.1:15031`, waits for the listener to clear, and starts a replacement
  with `ExitOnForwardFailure=yes`.
- Retry delays follow the configured ladder. The first new heartbeat resets the
  attempt counter and retry delay.
- Laptop resume is handled as an ordinary late heartbeat. There is no separate
  resume hook or external watchdog.

At Task Server startup, a successful bounded functional route probe adopts one
already-running matching route. This prevents a flap during migration from the
Scheduled Task. The next failed probe moves ownership to a supervisor child.

## API and Studio operation

`GET /api/v1/management/links` returns one entry per configured link with
`state`, `since`, `lastHeartbeatAt`, `lastProbe`, `lastError`, `attempt`,
`nextRetryAt`, and `childPid`. These authenticated mutations are appended to
`<TaskRepository>/.audit/runner-links.jsonl`:

```text
POST /api/v1/management/links/agent-runner-01/reconnect
POST /api/v1/management/links/agent-runner-01/pause
POST /api/v1/management/links/agent-runner-01/resume
```

Execution Hosts displays the resource in its Link column and exposes the last
error in the tooltip. Reconnect replaces a failed route. Pause is the only
operator-set state and intentionally tears down the child; Resume restarts the
normal connection flow.

Every relevant transition writes an operator-feed event named `link_down`,
`link_up`, or `link_reconnect_failed`. If a non-paused link stays down for five
minutes while a Ready card targets that runner, the Task Server emits one
`link_down_notification` with the last error. A new heartbeat clears the acute
state. Ready-card copy reads this same resource, so a link failure never appears
as a provider sign-in failure.

## Verify

1. Restart Stable and confirm the Task Server process has one `ssh` child and no
   console window appears.
2. Request `GET /api/v1/management/links`. Confirm `agent-runner-01` becomes
   `up`, `lastHeartbeatAt` is current, and `childPid` names the owned child.
3. On the runner host, confirm `curl -fsS http://127.0.0.1:15031/healthz` and
   `agent-host --health-check --server http://127.0.0.1:15031` succeed.
4. Kill only the `childPid`. Within the configured recovery ladder, confirm the
   resource returns through `reconnecting` and `connecting` to `up` without an
   operator action.
5. Confirm the operator feed contains `link_down`, any failed-attempt events,
   and `link_up`. Preserve the resource JSON and event excerpt in the delivery
   card's absolute `results/` directory.
6. Stop the Task Server and confirm its SSH child no longer exists. Start it and
   confirm a fresh child and heartbeat restore `up` without manual steps.

## Emergency path only

The assets under `deploy/windows/agent-runner-tunnel/` are retained for an
explicit rollback while the migration soaks. They are not installed or invoked
by the application. If the Task Server cannot own the link, an operator may
temporarily register `AgentRunner-TunnelKeeper` and its watchdog by following
the scripts' own parameters, then disable the product `RunnerLinks` entry to
avoid two owners. This path requires PowerShell and Task Scheduler and can show
desktop-window behavior when configured incorrectly, which is why it is an
emergency path rather than the product implementation.

The earlier host-initiated `autossh` plus systemd topology is also a fallback
only. It applies when the Linux host can reach a protected SSH endpoint on the
Studio side and uses `-L 15031:localhost:5031`. Do not run it alongside a
Task Server-owned entry.

## Target topology

The reverse link remains interim. The remote Task Server programme gives the
runner a private authenticated endpoint and removes the workstation from the
claim, lease, log, and completion path. At that cutover, retain this resource
and its UI semantics for Task Server connection health, but retire the SSH
transport rather than adding a second production route.
