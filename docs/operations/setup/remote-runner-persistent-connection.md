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

## Migration record and soak gate

The operator enabled `RunnerLinks` for `agent-runner-01` in Stable on
**2026-09-25 at 08:06** (operator-reported local time). The operator disabled,
but did not delete, `AgentRunner-TunnelKeeper` on **2026-09-25**. Its remaining
SSH process held the remote listener and was stopped so `LinkSupervisor` could
bind it. This switch used a fresh supervisor child; it did not demonstrate
flap-free adoption of the keeper's running forward. Do not re-enable the task
merely to repeat migration while the production link is in use.

For AGT-2764, use the first recorded `link_up` after the switch,
2026-09-25 08:23:51 UTC, as the candidate soak start. Its seven-day endpoint
is 2026-10-02 08:23:51 UTC. The operator feed for this window records 161
`link_up` and 241 `link_down` transitions, eight `link_reconnect_failed`
events, one `link_remote_listener_held` alarm, and one
`link_down_notification`. Every recorded Down interval ended in an Up event;
the longest was 7 minutes 13 seconds on 2026-09-29. The 2026-10-03 resource
snapshot was `up` with a current capability heartbeat and an owned `childPid`.
The event history and resource snapshot are in AGT-2764's delivery results.

The elapsed seven days and automatic recovery are recorded, but the soak is
**not yet accepted**: the available feed does not establish a workstation
sleep and resume within this window, and the workstation's
`<TaskRepository>/.audit/runner-links.jsonl` has not been checked for manual
reconnect, pause, or resume actions. Attach a timestamped Windows sleep/resume
record, its corresponding link-resource and `link_down`/`link_up` evidence,
and the audit excerpt before marking S3 done. If those records show a manual
intervention or another link owner during the window, start a new seven-day
window at the first subsequent `link_up`.

Keep the keeper task registered and disabled through the remote Task Server
cutover and rollback rehearsal. It may be unregistered only after the
operator has verified a rollback that no longer needs the registration. Keep
`deploy/windows/agent-runner-tunnel/` as the documented manual emergency path
under decision D3.

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
  its child, runs a non-interactive `timeout 4s` inspection through SSH, and
  checks `ss -H -ltnp "sport = :15031"`. It signals only an `sshd` process owned
  by the SSH user and holding `127.0.0.1:15031`, then checks the listener again.
  Both remote output streams go to the link log. A timeout names the cleanup
  command in `lastError`.
- If the listener remains held, the resource reports `blockedBy:
  remote-listener-held`, `remoteListenerPid`, and `remoteListenerAgeSeconds`.
  The operator feed receives one `link_remote_listener_held` alarm for that
  incident. Retries continue at the longest configured delay until the port
  clears. `transport: no-route` identifies a dropped local route in the
  resource and error detail.
- Retry delays otherwise follow the configured ladder. The first new heartbeat
  resets the attempt counter and retry delay.
- Laptop resume is handled as an ordinary late heartbeat. There is no separate
  resume hook or external watchdog.

At Task Server startup, a successful bounded functional route probe adopts one
already-running matching route. The next failed probe moves ownership to a
supervisor child. The September 25 migration required stopping a foreign
listener and therefore does not count as a successful live adoption test.

Runner onboarding installs `/etc/ssh/sshd_config.d/05-agent-runner-client-alive.conf`
with `ClientAliveInterval 30` and `ClientAliveCountMax 3`, verifies the effective
sshd settings, and reloads the SSH service. This server-side liveness policy
reaps a dead client after roughly 90 seconds even when the workstation cannot
reach the host to run cleanup. Re-run onboarding to restore the managed file.

## API and Studio operation

`GET /api/v1/management/links` returns one entry per configured link with
`state`, `since`, `lastHeartbeatAt`, `lastProbe`, `lastError`, `attempt`,
`nextRetryAt`, `childPid`, `blockedBy`, `remoteListenerPid`,
`remoteListenerAgeSeconds`, and `transport`. These authenticated mutations are appended to
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
explicit rollback. They are not invoked by the application. If the Task Server
cannot own the link, disable the product `RunnerLinks` entry and stop its SSH
child first. An operator may then temporarily start the disabled
`AgentRunner-TunnelKeeper` task or register it and its watchdog by following
the scripts' own parameters. This path requires PowerShell and Task Scheduler and can show
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
this tunnel for normal operation. Keep the scheduled task disabled until the
rollback rehearsal has passed; it remains documented as the fallback path in
[remote-task-server-local-studio.md](../remote-task-server-local-studio.md#rollback-in-less-than-15-minutes).

When a tunnel remains necessary, supervision belongs on the side that can
initiate the connection. The current topology requires Windows to dial the
public Linux SSH endpoint, so the Task Server's `LinkSupervisor` owns it there (the
repository keeper stays the documented emergency path).
If the Linux host can reach a protected studio or bastion endpoint, Option B's
`autossh` plus systemd gives stronger boot-time ownership. A central private
Task Server is preferable to either form because no workstation process then
sits on the claim, lease, log, and completion path.
