#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
keeper="$repo_root/deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1"
registration="$repo_root/deploy/windows/agent-runner-tunnel/register-tunnel-keeper.ps1"
watchdog="$repo_root/deploy/windows/agent-runner-tunnel/tunnel-watchdog.sh"
watchdog_registration="$repo_root/deploy/windows/agent-runner-tunnel/register-tunnel-watchdog.ps1"
forced_kill_test="$repo_root/deploy/windows/agent-runner-tunnel/test-tunnel-watchdog-forced-kill.ps1"

test -f "$keeper"
test -f "$registration"
test -f "$watchdog"
test -f "$watchdog_registration"
test -f "$forced_kill_test"

grep -Fq "AGENT_TASK_SERVER_ROUTE_OK" "$keeper"
grep -Fq "curl --fail --silent --show-error --max-time 10" "$keeper"
grep -Fq "Get-CimInstance Win32_Process" "$keeper"
grep -Fq "Stop-MatchingForwards" "$keeper"
grep -Fq "Test-NativeArgument" "$keeper"
grep -Fq "[Regex]::Escape(\$forward)" "$keeper"
grep -Fq "ExitOnForwardFailure=yes" "$keeper"
grep -Fq "ServerAliveInterval=30" "$keeper"
grep -Fq "RedirectStandardOutput" "$keeper"
grep -Fq "RedirectStandardError" "$keeper"
grep -Fq "event=ssh_exited" "$keeper"
grep -Fq "WaitForExit" "$keeper"
grep -Fq "pre-existing matching forward" "$keeper"
grep -Fq 'AgentTaskboardTunnelKeeper-$HostIdentity-$RemotePort-$SshTarget' "$keeper"
grep -Fq "'-HostIdentity', \$TaskName" "$registration"
grep -Fq -- "-MultipleInstances IgnoreNew" "$registration"
grep -Fq -- "-RepetitionInterval" "$registration"
grep -Fq -- "-LogonType S4U" "$registration"
grep -Fq -- "-AtStartup" "$registration"
grep -Fq -- "-ExecutionTimeLimit ([TimeSpan]::Zero)" "$registration"
if grep -Fq -- "-RestartCount" "$registration"; then
  printf 'TunnelKeeper must not race the two-probe watchdog with an automatic task retry.\n' >&2
  exit 1
fi
grep -Fq "Register-ScheduledTask" "$registration"
grep -Fq "curl -sf --max-time 6" "$watchdog"
grep -Fq "Stop-ScheduledTask" "$watchdog"
grep -Fq "Start-ScheduledTask" "$watchdog"
grep -Fq "event=heal_succeeded" "$watchdog"
grep -Fq "source=tunnel-watchdog severity=alarm" "$watchdog"
grep -Fq -- "-LogonType S4U" "$watchdog_registration"
grep -Fq -- "-AtStartup" "$watchdog_registration"
grep -Fq -- "-ExecutionTimeLimit ([TimeSpan]::Zero)" "$watchdog_registration"
grep -Fq "forced-kill-pids" "$forced_kill_test"
grep -Fq "tunnel-watchdog-forced-kill--real.md" "$forced_kill_test"

if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -NonInteractive -Command \
    "[void][System.Management.Automation.Language.Parser]::ParseFile('$keeper',[ref]\$null,[ref]\$null); [void][System.Management.Automation.Language.Parser]::ParseFile('$registration',[ref]\$null,[ref]\$null); [void][System.Management.Automation.Language.Parser]::ParseFile('$watchdog_registration',[ref]\$null,[ref]\$null); [void][System.Management.Automation.Language.Parser]::ParseFile('$forced_kill_test',[ref]\$null,[ref]\$null)"
fi

printf 'Tunnel assets contain functional probes, targeted cleanup, captured SSH diagnostics, and session-independent watchdog registration.\n'
