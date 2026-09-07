# Remote runner: persistent connection

Status: interim operations runbook for unattended remote operation
("Dauerbetrieb"), until the central URL lands
([distributed-agent-studio-target-architecture.md](../../concepts/distributed-agent-studio-target-architecture.md),
Task Server / central-URL phase). It is the companion to the host provisioning runbook
[linux-runner-host.md](./linux-runner-host.md); read that first.

The standalone runner reaches the Task Server **only over an SSH tunnel** during
the MVP (no inbound port on the host beyond SSH until central-URL auth exists).
A one-off `ssh` invocation is enough to run one task by hand, but unattended
operation needs the tunnel to be a **supervised service that reconnects on its
own** after a reboot, a laptop sleep, or a network blip. Without that, the
tunnel dies silently and every later run fails at the first API call.

This page covers two things:

1. Keep the tunnel up as a service (autossh/systemd on the host, or a scheduled
   task on the Windows studio side).
2. The runner's connectivity **health-check**, so a dropped tunnel is reported
   once, cleanly, instead of cascading into a launch failure.

## The connection

```
[Windows studio]                         [Linux runner host: agent-runner]
  Task Server  127.0.0.1:5031  ◄───SSH reverse tunnel───  127.0.0.1:15031
                                                             agent-host --server http://127.0.0.1:15031
```

- The Task Server runs on the studio (stable, `127.0.0.1:5031`). It is **not**
  exposed on the network; the tunnel is the only path in.
- The tunnel maps the studio's `5031` onto the host's loopback port `15031`.
  On the host, `RUNNER_SERVER_URL=http://127.0.0.1:15031` reaches the Task
  Server. Pick any free host port; `15031` is the convention used here.
- Which end **initiates** the SSH connection depends on reachability, and that
  decides which forwarding flag you use:
  - The studio is a workstation behind home NAT; the host has a public IP with
    inbound SSH. So the studio **dials out** to the host and asks for a
    **reverse** forward (`-R`): the listening port opens on the host. This is
    the current test-host topology and the default below (Option A).
  - If your topology lets the host reach the studio's SSH instead (a bastion, a
    static endpoint, or a VPN), run the supervisor on the host with a **local**
    forward (`-L`) — same resulting port on the host (Option B).

Either way the host ends up with `127.0.0.1:15031 → studio 5031`, and nothing
below the tunnel line changes.

## Option A - Windows scheduled task (studio dials out, `-R`)

Matches the current Hetzner test host: the studio initiates an outbound reverse
tunnel and a Windows Scheduled Task keeps it alive.

`agent-runner` is an SSH alias defined in the studio user's `~/.ssh/config`:

```sshconfig
Host agent-runner
    HostName <runner-host-ip>
    User runner
    IdentityFile ~/.ssh/agent-runner
    ServerAliveInterval 30
    ServerAliveCountMax 3
    ExitOnForwardFailure yes
```

Use the repository-owned functional keeper instead of a bare `ssh -N` loop:

```powershell
Set-Location C:\Projects\agent-studio
.\deploy\windows\agent-runner-tunnel\register-tunnel-keeper.ps1 `
    -SshTarget agent-runner `
    -RemotePort 15031 `
    -OrchestratorPort 5031 `
    -IntervalMinutes 5

.\deploy\windows\agent-runner-tunnel\register-tunnel-watchdog.ps1 `
    -DevspacePath C:\Projects\agent-taskboard-devspace `
    -SshTarget agent-runner `
    -RemotePort 15031 `
    -KeeperTaskName AgentRunner-TunnelKeeper `
    -ProbeIntervalSeconds 60 `
    -FailureThreshold 2 `
    -OperatorAlarmPath C:\Projects\agent-taskboard-devspace\.operator-alarm.log
```

Both registrations are idempotent and use `IgnoreNew`. The keeper starts at
boot and user logon, starts immediately when registered, and retains its
five-minute fallback trigger. Its registration also sets `StartWhenAvailable`,
`AllowStartIfOnBatteries`, and `DontStopIfGoingOnBatteries`, so re-registering
does not restore the power-policy failure that caused the 2026-09-06 outage.
The independent `AgentRunner-TunnelWatchdog` starts at boot and owns a
60-second probe loop. Both tasks use an S4U principal, so they do not depend on
an interactive logon session. The selected identity must own a local protected
SSH key and a non-interactive `agent-runner` alias. Run the registration from
an elevated PowerShell session because an at-startup task can require that
authority.

The keeper task has no execution time limit and remains the owner of its
`ssh.exe` child until SSH exits. It deliberately has no Task Scheduler retry:
the watchdog owns the two-probe recovery sequence, while the keeper's periodic
trigger remains a slower fallback if the watchdog task itself is unavailable.
On the first run after upgrading, the keeper replaces a matching pre-existing
Windows forward once so the long-lived SSH process moves under this ownership
and logging contract.

The keeper in
[`deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1`](../../../deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1)
does not equate a local `ssh.exe` process with a working route. It asks the
Linux host to request `http://127.0.0.1:15031/healthz` and accepts the probe only
when SSH returns the exact `AGENT_TASK_SERVER_ROUTE_OK` sentinel. If that
functional probe fails, the keeper stops only `ssh.exe` processes matching the
configured target and exact reverse-forward tuple, waits for the old listener
to clear, then starts:

```powershell
ssh.exe -N -T `
  -o BatchMode=yes `
  -o ExitOnForwardFailure=yes `
  -o ServerAliveInterval=30 `
  -o ServerAliveCountMax=3 `
  -R 15031:127.0.0.1:5031 agent-runner
```

State and bounded transition logs live under
`%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper\`. Healthy scheduled runs stay
quiet. An ongoing failure is logged on transition and at most hourly, not on
every five-minute invocation. `ExitOnForwardFailure=yes` matters: if the host's
`15031` is still held by a half-dead previous session, SSH fails fast instead
of connecting without the forward.

The watchdog in
[`deploy/windows/agent-runner-tunnel/tunnel-watchdog.sh`](../../../deploy/windows/agent-runner-tunnel/tunnel-watchdog.sh)
runs the functional probe every 60 seconds. After two consecutive failures it
uses the operator recovery sequence in this order:

1. Through the agent SSH account, find and stop a process listening specifically
   on `127.0.0.1:15031`. A missing listener is a successful no-op.
2. Call `Stop-ScheduledTask` and `Start-ScheduledTask` for
   `AgentRunner-TunnelKeeper`.
3. Retry the runner-side health request for up to 30 seconds and journal the
   outcome.

The append-only journal is `<devspace>/.tunnel-watchdog.log`. A second
consecutive failed heal appends one `severity=alarm` line to the configured
operator-alarm channel. The default channel is
`<devspace>/.operator-alarm.log`; pass the path already consumed by the stable
operator watcher when a devspace uses a different path.

Each keeper replacement now writes its stdout and stderr to timestamped files
under `%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper\` and records the paths in
`ssh-attempts.log`. The keeper waits for the SSH process and always records its
eventual exit code. The stderr file includes verbose OpenSSH disconnect and
forwarding diagnostics, so a later incident can distinguish keepalive death,
connection reset, authentication failure, and remote bind refusal.

### Studio-side supervision and reconnect

Execution Hosts reads the configured Scheduled Task on the Windows Studio host
(`RemoteRunnerLink:KeeperTaskName`, default `AgentRunner-TunnelKeeper`), checks
whether a reverse-forward `ssh.exe` process exists, and includes the keeper
state plus the tail of its transition log. Set
`RemoteRunnerLink:KeeperStateDirectory` and `RemoteRunnerLink:KeeperLogPath`
when the live keeper uses an operator-owned location such as
`C:\Users\rmisc\ops\tunnel-keeper.log`; when that setting is omitted, Studio
also discovers a legacy `tunnel-keeper.log` beside the script named in the task
action. The repository keeper defaults to its
`%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper` state directory. The probe is
guarded by an operating system check and is not attempted when Studio runs
anywhere other than Windows.

Capability-snapshot freshness is the runner link signal. A fresh snapshot is
**connected**; an expired snapshot is **stale**; after
`RemoteRunnerLink:SnapshotDownMinutes` (default five) it is **down**. If Ready
cards target a down runner, Studio emits one operator notification describing
whether the keeper task is disabled, not running, has no SSH process, or has a
failing functional probe.

The notification and Execution Hosts row offer **Reconnect**. It calls
`POST /api/v1/management/remote-hosts/{id}/reconnect`, which enables and starts
only the configured Scheduled Task. The response reports the scheduler outcome,
the resulting keeper observation, current link state, and next snapshot age.
It does not read, accept, or transmit credentials. A successful start can still
show the old snapshot age until the runner's next advertisement arrives.

## Option B - autossh + systemd on the host (host dials in, `-L`)

Use this when the host can reach the studio's SSH endpoint. `autossh` is the
supervisor; systemd restarts it across reboots. `StudioSsh` below is the host's
SSH alias for the studio (or bastion).

```ini
# /etc/systemd/system/agent-runner-tunnel.service
[Unit]
Description=Persistent tunnel to the agent-taskboard Task Server
After=network-online.target
Wants=network-online.target

[Service]
User=runner
Environment=AUTOSSH_GATETIME=0
# -M 0 disables autossh's own monitor port; the SSH keepalive below detects a
# dead link and makes ssh exit, which autossh then restarts.
ExecStart=/usr/bin/autossh -M 0 -N \
    -o ServerAliveInterval=30 -o ServerAliveCountMax=3 \
    -o ExitOnForwardFailure=yes \
    -L 15031:localhost:5031 StudioSsh
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo apt-get install -y autossh
sudo systemctl daemon-reload
sudo systemctl enable --now agent-runner-tunnel.service
systemctl status agent-runner-tunnel.service
```

The `ServerAliveInterval` / `ServerAliveCountMax` pair is what turns a silent
half-open TCP connection into a prompt exit + reconnect; keep it on whichever
option you choose.

## Health-check and connection-loss behavior

The runner treats a dropped tunnel as an **expected, recoverable** state, not a
crash. Two mechanisms make that clean:

- **Readiness probe.** `agent-host --health-check` hits the Task Server's
  open `/healthz` and exits `0` when reachable, `4` when not, printing a
  tunnel-pointing reason. It touches no task, needs no task key, and is safe to
  run on a schedule:

  ```bash
  agent-host --health-check --server http://127.0.0.1:15031
  # health-check ok: task server reachable at http://127.0.0.1:15031      -> exit 0
  # health-check failed: cannot reach the task server ... tunnel ... down  -> exit 4
  ```

  Gate work on it before handing the runner a task, e.g.:

  ```bash
  agent-host --health-check && agent-host "$TASK_KEY"
  ```

- **Run preflight.** A normal `agent-host <TASK-KEY>` probes `/healthz`
  **before** it registers, acquires the lease, or spawns the CLI. If the tunnel
  is down it logs one line -
  `connection lost: cannot reach the task server at <url> ...` - and exits `4`
  without a half-started run. This is the "reports connection loss cleanly
  instead of a launch-fail cascade" guarantee: the failure names the tunnel, not
  a phantom lease or CLI problem, and no lease is acquired or CLI spawned on a
  dead link. The probe uses a short (10 s) timeout so a black-holed tunnel fails
  fast rather than hanging on the full request timeout.

Exit `4` is the single "task server unreachable / rejected" code the runbook
already documents; the preflight and `--health-check` simply make it fire early,
first, and with a diagnostic that points at the tunnel.

A mid-run drop is handled separately and already: the lease heartbeat logs
`heartbeat error (will retry)` and rides out a transient blip while the TTL has
headroom; a genuine takeover surfaces as `lease lost` (exit `3`). See
[linux-runner-host.md](./linux-runner-host.md) §4.

The long-running coding and review daemons also publish
`task-server:connectivity` with a three-minute freshness deadline and include
their last local route observation in `HostTelemetrySnapshotDto`. While the
route is broken, the host cannot send a new failure through that route. The
Task Server therefore treats expiration of the connectivity capability as the
remote alarm. Execution Hosts renders that specific capability as **Task Server
route unreachable**. The daemon logs the initial transition, escalates after
five continuous minutes, emits at most one summary per hour, and records one
recovery transition. A day-long outage no longer produces one journal entry per
claim poll.

## Verify

1. Bring the tunnel service up (Option A or B).
2. From the host: `curl -fsS http://127.0.0.1:15031/healthz` succeeds, and
   `agent-host --health-check --server http://127.0.0.1:15031` exits `0`.
3. Run the live fault test from the Windows studio. The fault injector only
   kills the runner-side listener, then waits up to 150 seconds for the
   watchdog-owned Scheduled Task restart and writes its journal excerpt to
   `JOB_RESULTS_DIR`:

   ```powershell
   .\deploy\windows\agent-runner-tunnel\test-tunnel-watchdog-forced-kill.ps1 `
       -SshTarget agent-runner `
       -RemotePort 15031 `
       -TimeoutSeconds 150
   ```

4. Confirm `<devspace>/.tunnel-watchdog.log` contains two `probe_failed` rows,
   followed by `remote_listener_cleanup`, `keeper_restart`, and
   `heal_succeeded`. With the default cadence, recovery should complete in
   about two minutes plus the bounded replacement verification time.
5. Confirm the next capability advertisement returns Execution Hosts to
   **reachable** and preserve the live test report in the task's absolute
   `results/` directory.

## Is the tunnel still the right topology?

The reverse tunnel is an interim local-profile route, not the target control
plane. The central Task Server work tracked by AGT-2404 removes the Windows
workstation from the Runner's availability path: the Agent Host connects
directly to a private authenticated Task Server URL and this scheduled task is
disabled. Do not retain a reverse tunnel merely because the keeper now makes it
safer.

When a tunnel remains necessary, supervision belongs on the side that can
initiate the connection. The current topology requires Windows to dial the
public Linux SSH endpoint, so the repository keeper is the applicable option.
If the Linux host can reach a protected studio or bastion endpoint, Option B's
`autossh` plus systemd gives stronger boot-time ownership. A central private
Task Server is preferable to either form because no workstation process then
sits on the claim, lease, log, and completion path.
