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

On 2026-09-25 the operator disabled `AgentRunner-TunnelKeeper` on the Windows
workstation. It remains installed but disabled during the seven-day acceptance
window. Do not enable it while `RunnerLinks` is configured for this runner.

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
Adoption proves the route works, not which process created it. The 2026-09-25
handover did not exercise successful adoption: a foreign SSH process held the
remote listener, the cleanup probe exited 124, and the operator stopped that
process. The current resource reports a supervisor `childPid` and fresh
heartbeats. If cleanup cannot remove a foreign listener, stop its owner on the
host before expecting the supervisor to bind the port.

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

## Seven-day migration acceptance

The S3 soak starts at the latest verified `link_up` after the operator's final
handover action. The 2026-09-25 resource capture showed `state: up`, a fresh
`lastHeartbeatAt`, and a non-null `childPid`; its `since` timestamp was
2026-09-25 10:23:58 UTC. The workspace operator feed for that day contained
`link_down` and `link_up` transitions, including the 10:23:40 UTC down and
10:23:58 UTC recovery. These captures are in the AGT-2764 delivery results.
The earliest possible acceptance time is 2026-10-02 10:23:58 UTC. A later
manual intervention moves the start to the next verified recovery.

From the workstation's loopback Task Server endpoint, save both views at the
start and end of the window (and after the sleep or resume):

```sh
curl -fsS -H 'X-Client-Id: local-default' \
  http://127.0.0.1:5031/api/v1/management/links > link-resource.json
curl -fsS \
  'http://127.0.0.1:5031/api/bus/_workspace/messages?tag=runner-link&since=2026-09-25T10%3A23%3A58Z&limit=5000' \
  > link-events.json
```

Record the capture time separately. The resource's `lastError` can retain a
previous failure after `state` returns to `up`; judge current health from
`state`, `since`, `lastHeartbeatAt`, and `childPid` together. If the feed query
returns 5000 lines, collect shorter UTC intervals so older events are not lost.

For acceptance, preserve the link resource and the workspace operator-feed
lines with timestamps for the entire window. Confirm that the resource is
`up`, heartbeats remain fresh, recovery after any down transition is automatic,
and no operator reconnect, SSH kill, or keeper restart occurred. Include one
recorded workstation sleep or standby and resume during the window, followed by
an automatic `link_up` and fresh heartbeat. A pre-window sleep does not satisfy
this check. The current resource is a snapshot; use the feed for transition
history and the operator log for manual actions and sleep or resume evidence.
Do not mark S3 done from an `up` snapshot alone.

The disabled Scheduled Task may be deleted after this acceptance is documented
and the operator confirms that the repository emergency scripts can register it
again. Keep `deploy/windows/agent-runner-tunnel/` for decision D3's explicit
emergency path even after deleting the task. Until then, leave the task
installed and disabled for a quick rollback.

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

Once the Docker control plane's WireGuard origin is live
([control-plane-docker.md](./control-plane-docker.md)), `agent-runner-01`
switches `RUNNER_SERVER_URL` to the WireGuard-only origin and stops relying on
this tunnel for normal operation. Disable the scheduled task or systemd unit
rather than deleting it: it stays installed, tested, and documented as the
fallback path for the migration and rollback runbook in
[remote-task-server-local-studio.md](../remote-task-server-local-studio.md#rollback-in-less-than-15-minutes).

When a tunnel remains necessary, supervision belongs on the side that can
initiate the connection. The current topology requires Windows to dial the
public Linux SSH endpoint, so the Task Server's `LinkSupervisor` owns it there (the
repository keeper stays the documented emergency path).
If the Linux host can reach a protected studio or bastion endpoint, Option B's
`autossh` plus systemd gives stronger boot-time ownership. A central private
Task Server is preferable to either form because no workstation process then
sits on the claim, lease, log, and completion path.
