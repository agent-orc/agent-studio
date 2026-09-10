[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $RemoteBaseUrl,
    [string] $RemoteToken,

    [string] $LocalListenUrl = 'http://127.0.0.1:5071',
    [string] $LocalServerEnvPath = 'C:\ProgramData\AgentOrchestrator\server.env',
    [string] $TaskServerTaskName = 'AgentOrchestrator-TaskServer',

    [string] $ConnectorEnvFile = 'C:\ProgramData\AgentOrchestrator\studio-connector.env',
    [string] $ConnectorListenUrl,
    [string] $ConnectorTaskName = 'AgentOrchestrator-StudioConnector',

    [string] $TunnelKeeperTaskName = 'AgentRunner-TunnelKeeper',
    [string] $AgentRunnerSshTarget = 'agent-runner-01',
    [string] $RunnerRemoteServerUrl,
    [string] $RunnerEnvPath = '/etc/agent-host/runner.env',
    [string] $RunnerServiceName = 'agent-host',
    [string] $SshExecutable = 'ssh.exe',

    [string] $ResultsDirectory = $env:JOB_RESULTS_DIR
)

<#
The rehearsal-repeatability half of the switch drill: undoes exactly what
run-switch-drill.ps1 did, so a drill leaves the topology back where it
started instead of leaving the Windows fallback authoritative until the
next scheduled maintenance window. This is a rehearsal reverse, not the
production "Failback to Hetzner" runbook: it does not run a fresh
freeze/inventory/backup cycle, because
docs/operations/remote-task-server-local-studio.md is explicit that a real
failback "is a new migration window with the same freeze, inventory,
backup, and sole-writer gates ... never an automatic resynchronization."
Any real mutation accepted by the local Task Server during the drill is
migration evidence for that window, not something this script silently
discards.
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

$phases = [System.Collections.Generic.List[object]]::new()
function Invoke-Phase {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Action
    )
    $startedAt = [DateTime]::UtcNow
    Write-FallbackLog "Phase start: $Name"
    & $Action
    $finishedAt = [DateTime]::UtcNow
    $phases.Add([pscustomobject]@{
        Name           = $Name
        ElapsedSeconds = [Math]::Round(($finishedAt - $startedAt).TotalSeconds, 1)
    })
    Write-FallbackLog "Phase done: $Name ($([Math]::Round(($finishedAt - $startedAt).TotalSeconds, 1))s)"
}

$drillStartedAt = [DateTime]::UtcNow

Invoke-Phase 'Verify the remote Task Server is reachable and ready' {
    if (-not (Wait-HttpReady -Url "$($RemoteBaseUrl.TrimEnd('/'))/readyz" -TimeoutSeconds 60 -ExpectedText '"status":"ready"')) {
        throw "Remote Task Server did not report ready at $RemoteBaseUrl. Do not repoint the Runner or connector until it does."
    }
}

Invoke-Phase 'Quiesce the local Task Server back to Maintenance' {
    if (-not $PSCmdlet.ShouldProcess($TaskServerTaskName, 'Drain and quiesce the local Task Server')) { return }
    $localToken = Get-TaskServerAuthToken -ServerEnvPath $LocalServerEnvPath
    if (-not (Wait-TaskServerDrain -BaseUrl $LocalListenUrl -Token $localToken -Reason 'reverse drill' -TimeoutSeconds 90)) {
        throw 'Local Task Server drain did not settle; resolve active attempts by hand before reversing the drill.'
    }
    Set-TaskServerMode -BaseUrl $LocalListenUrl -Token $localToken -Mode 'Maintenance' -Reason 'reverse drill: remote is authoritative again'
}

Invoke-Phase 'Switch the Studio connector back to the remote upstream' {
    if (-not $PSCmdlet.ShouldProcess($ConnectorTaskName, 'Switch the Studio connector upstream to Remote')) { return }
    & (Join-Path $PSScriptRoot 'switch-upstream.ps1') `
        -UpstreamProfile Remote -RemoteBaseUrl $RemoteBaseUrl -LocalBaseUrl $LocalListenUrl `
        -ConnectorEnvFile $ConnectorEnvFile -ConnectorListenUrl $ConnectorListenUrl -TaskName $ConnectorTaskName
}

Invoke-Phase 'Repoint agent-runner-01 at the remote WireGuard origin and disable the reverse tunnel' {
    if (-not $PSCmdlet.ShouldProcess($AgentRunnerSshTarget, 'Repoint the Runner at the remote origin and disable the reverse tunnel')) { return }
    if ([string]::IsNullOrWhiteSpace($RunnerRemoteServerUrl)) {
        throw 'RunnerRemoteServerUrl is required to repoint the Runner back to the WireGuard origin.'
    }
    $repointCommand = "sudo sed -i 's|^RUNNER_SERVER_URL=.*|RUNNER_SERVER_URL=$RunnerRemoteServerUrl|' $RunnerEnvPath && sudo systemctl restart $RunnerServiceName"
    $repoint = Invoke-RemoteSshCommand -SshTarget $AgentRunnerSshTarget -Command $repointCommand -SshExecutable $SshExecutable
    if ($repoint.ExitCode -ne 0) {
        throw "Could not repoint $AgentRunnerSshTarget at the remote origin: $($repoint.Output)"
    }

    $tunnelTask = Get-ScheduledTask -TaskName $TunnelKeeperTaskName -ErrorAction SilentlyContinue
    if ($null -ne $tunnelTask -and $tunnelTask.State -eq 'Running') {
        Stop-ScheduledTask -TaskName $TunnelKeeperTaskName
    }
    Write-FallbackLog "$AgentRunnerSshTarget now targets $RunnerRemoteServerUrl and the reverse tunnel keeper was stopped (left installed for the next drill)."
}

$drillFinishedAt = [DateTime]::UtcNow
$totalElapsedMinutes = [Math]::Round(($drillFinishedAt - $drillStartedAt).TotalSeconds / 60, 2)

$report = @(
    '# Switch drill evidence: local Windows fallback back to remote',
    '',
    "- Started (UTC): $($drillStartedAt.ToString('o'))",
    "- Finished (UTC): $($drillFinishedAt.ToString('o'))",
    "- Total elapsed: $totalElapsedMinutes minutes",
    '',
    '## Phases',
    ''
) + ($phases | ForEach-Object { "- $($_.Name): $($_.ElapsedSeconds)s" })

Write-FallbackReport -Lines $report -FileName 'switch-drill-local-to-remote.md' -ResultsDirectory $ResultsDirectory
