# Windows Task Server fallback runbook

Phase B slice B4 (AGT-2735) of
[Remote Task Server with local Agent Studio](../remote-task-server-local-studio.md):
a version-matched, warm, local Windows Task Server that a Studio connector
can flip to in under 15 minutes if the remote Hetzner control plane is
unreachable. This page is the one-page operator runbook; the underlying
service contract (executables, env files, Scheduled Task shape) is documented
in [Task Server deployment and recovery](task-server.md#windows-install-and-supervision).

## What is pre-installed

One release package, `agent-orchestrator-<version>-win-x64`, published by the
`publish-windows` job in `.github/workflows/release.yml` next to the
`linux-x64` archives with the same `VERSION`/`RELEASE-SHA` identity contract.
It contains `task-server.exe`, `orchestrator-engine.exe`,
`agent-studio-bff.exe`, `OrchestratorApi.exe`, `agent-host.exe`, the browser
bundle, and a copy of `deploy/windows/` (`deploy-windows/`
inside the package) so the target machine needs neither a repository
checkout nor a .NET install.

```powershell
Expand-Archive agent-orchestrator-<version>-win-x64.zip -DestinationPath C:\Downloads
& C:\Downloads\agent-orchestrator-<version>-win-x64\deploy-windows\fallback\install-fallback-profile.ps1 `
    -ReleasePackageRoot C:\Downloads\agent-orchestrator-<version>-win-x64 `
    -DataDirectory D:\AgentOrchestratorData
```

This registers three Scheduled Tasks (`AgentOrchestrator-TaskServer`,
`AgentOrchestrator-Engine`, `AgentOrchestrator-StudioConnector`), proves each
one reaches `Normal`/healthy once, then rests the Task Server in
`Maintenance`. Maintenance already refuses every mutating route without
stopping the process, so the fallback stays warm (no cold-start latency
inside the 15-minute budget) without ever being a second writer while the
remote Task Server is authoritative. Re-running the install script with the
same `-DataDirectory` keeps the existing `server.env`/`engine.env`/
`studio-connector.env` and their bootstrap credentials untouched; only a
fresh `C:\ProgramData\AgentOrchestrator` gets new ones. A re-run with a newer
package stops the registered tasks and their executables before it moves the
`current` junction. Credential files are restricted to the installing account,
SYSTEM, and Administrators. `agent-studio-setup --target native` runs this
script with `-RestMode Normal -FullStudio`: the local Task Server is then the
primary one and five boot-time tasks start: Task Server, Engine, Studio
connector, Studio backend, and Runner. The browser UI is served by the
connector on port 4011 ([install guide](./install.md)).

The script's default remains the three-task warm fallback. For a complete
local Studio, pass `-RestMode Normal -FullStudio`. The native setup executable
supplies these switches and handles the guided decisions. Direct script users
can pass the same choices:

| Switch | Effect with `-FullStudio` |
| --- | --- |
| `-ProviderCli <choice>` | Select `codex`, `claude`, or `skip`. `skip` leaves provider login for later. |
| `-GitAccess <choice>` | Select `gcm`, `token`, `deploy-key`, or `skip` for the Runner's GitHub credential source. |
| `-GitTokenFile <path>` | Required for `token`; the script copies the token into a protected credential file. |
| `-GitRemote <url>` | Verify access to this repository. Required as a GitHub SSH URL for `deploy-key`; use a GitHub HTTPS URL for `gcm` or `token`. |
| `-RunnerPasswordFromStdin` | Read the Runner account password from one stdin line for a boot-time task with that account's credential profile. |
| `-Unattended` | Suppress the one-time owner-code console display. The code remains in its restricted file. |

The script also accepts the older `-NonInteractive` switch. The owner-code
display is controlled by `-Unattended`.

The script writes `server.env`, `engine.env`, and `studio-connector.env` under
`ConfigRoot`. Full Studio also writes `studio-backend.env` and `runner.env`.
In full mode, `server.env` gains `OWNER_BOOTSTRAP_CODE_FILE` (the protected
one-time code), `BOOTSTRAP_RUNNER_ID`, and `BOOTSTRAP_RUNNER_AUTH_TOKEN_FILE`;
`studio-connector.env` gains `Studio__BrowserRoot` pointing at the packaged
browser bundle and `Studio__BackendBaseUrl` pointing at the local backend;
the connector serves port 4011. `studio-backend.env` sets
`ASPNETCORE_URLS` to port 5031 and `TaskServer__BaseUrl` to port 5071.
`runner.env` records the selected provider, protected Runner token path,
work and state directories, and Git configuration for the selected access
method. The setup executable records these choices without secrets in
`installation.json` as described in the [install guide](./install.md).

Point the Studio connector back at the remote origin once the install is
proved:

```powershell
.\deploy-windows\fallback\switch-upstream.ps1 -UpstreamProfile Remote -RemoteBaseUrl https://<wireguard-address>
```

Uninstall with `.\deploy-windows\fallback\uninstall-fallback-profile.ps1`. It
only removes the versioned binaries and the Scheduled Tasks by default;
pass `-RemoveData -DataDirectory <path>` to also delete the local store and
pulled backups.

## Staying within one backup interval: warm standby

```powershell
.\deploy-windows\fallback\register-warm-standby.ps1 `
    -SshTarget task-server-01 -LocalDataDirectory D:\AgentOrchestratorData -IntervalMinutes 5
```

Registers `AgentOrchestrator-WarmStandby`, a Scheduled Task that runs
`pull-warm-standby-backup.ps1` on the same interval as the remote backup
timer (default 5 minutes, matching the recovery point in
[Remote Task Server with local Agent Studio](../remote-task-server-local-studio.md#data-and-backup-layout)).
Each run finds the newest complete full-backup set under
`<RemoteDataDirectory>/backups/full` on the remote host over SSH, copies it
locally with `scp`, and verifies it offline with
`task-server.exe backup verify-full <id> --TaskServer:DataDirectory <path>`.
It never starts or mutates the local Task Server; a corrupt or partial pull
is deleted and the previous verified set is left in place.

## The switch drill

`run-switch-drill.ps1` reproduces
[the rollback timeline](../remote-task-server-local-studio.md#rollback-in-less-than-15-minutes)
phase by phase, times each one, and writes a Markdown report to
`$env:JOB_RESULTS_DIR` (or stdout without it):

```powershell
.\deploy-windows\fallback\run-switch-drill.ps1 `
    -RemoteBaseUrl https://<wireguard-address> -RemoteToken <break-glass-or-studio-token> `
    -LocalDataDirectory D:\AgentOrchestratorData `
    -AgentRunnerSshTarget agent-runner-01
```

It fences the remote Task Server, selects and verifies the newest local
backup (or a specific `-BackupId`), restores it onto the local Task Server
and brings that server to `Normal`, atomically switches the Studio connector
to the local upstream through `switch-upstream.ps1`, then re-enables the
preserved reverse SSH tunnel and repoints `agent-runner-01` at
`127.0.0.1:15031` over SSH. The script throws if the total elapsed time is
not under 15 minutes; the report records the actual number either way.

`switch-upstream.ps1` recognises two connector env files (AGT-2984). For the
hardened connector profile (`OrchestratorApi__Profile=connector`) it rewrites
`Connector__Upstream__*` and `Connector__CredentialTarget`, bumps the
generation, and gates on the connector's `/readyz`. That endpoint is ready
only after the attach handshake has proved the stored credential and a shared
protocol version; a refusal reason is written to the switch log. For the
legacy `agent-studio-bff.exe` env file that `install-fallback-profile.ps1`
writes, it switches only to `Local` and refuses `Remote`, because that
forwarder has no Origin, CSRF, or protocol negotiation. Before a reverse
drill, attach the connector profile as in the
[single-host connector attach procedure](single-host-task-server.md#studio-connector-attach).

`run-reverse-drill.ps1` undoes exactly that (quiesces the local Task Server
back to `Maintenance`, switches the connector back to `Remote`, repoints
`agent-runner-01` back at the WireGuard origin, and disables the reverse
tunnel again) so a drill is repeatable. It is a rehearsal reverse, not the
production "Failback to Hetzner" runbook: a real failback is its own
migration window with a fresh freeze, inventory, and backup, never an
automatic resync.

Both scripts support `-WhatIf`, which previews every mutating step without
touching live infrastructure; `.github/workflows/windows-fallback-validation.yml`
runs that dry-run form on every push that touches `deploy/windows/**`, so a
parameter or sequencing regression is caught before an operator schedules a
real rehearsal. The timed, real-infrastructure rehearsal remains a Phase B
slice B6 operator drill, run against the actual Hetzner VM,
`agent-runner-01`, and the Windows device.

## What Windows CI proves on every change

`.github/workflows/windows-fallback-validation.yml` (`windows-latest`):

- the full backup round trip, including the case-only path collision guard
  in `task-server/FullBackupService.cs`, on real NTFS;
- `install-fallback-profile.ps1` actually registers all three Scheduled
  Tasks and each one reaches ready/healthy;
- `uninstall-fallback-profile.ps1` removes all three and, with
  `-RemoveData`, the local store;
- both drill scripts run end to end in `-WhatIf` mode and produce a report.

Release CI's `.github/workflows/release.yml` `publish-windows` job separately
proves the win-x64 binaries publish and the packaged archive lands next to the
Linux ones with one combined `SHA256SUMS`. Its native-install smoke test checks
Task Server readiness, Studio health and UI, owner bootstrap, and Runner
registration before uninstalling.
