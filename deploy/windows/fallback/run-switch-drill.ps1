[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $RemoteBaseUrl,
    [string] $RemoteToken,

    [string] $LocalListenUrl = 'http://127.0.0.1:5071',
    [string] $LocalServerEnvPath = 'C:\ProgramData\AgentOrchestrator\server.env',
    [Parameter(Mandatory)] [string] $LocalDataDirectory,
    [string] $BackupId,
    [string] $TaskServerTaskName = 'AgentOrchestrator-TaskServer',

    [string] $ConnectorEnvFile = 'C:\ProgramData\AgentOrchestrator\studio-connector.env',
    [string] $ConnectorListenUrl,
    [string] $ConnectorTaskName = 'AgentOrchestrator-StudioConnector',

    [string] $TunnelKeeperTaskName = 'AgentRunner-TunnelKeeper',
    [string] $AgentRunnerSshTarget = 'agent-runner-01',
    [int] $RunnerLocalPort = 15031,
    [string] $RunnerEnvPath = '/etc/agent-host/runner.env',
    [string] $RunnerServiceName = 'agent-host',
    [string] $SshExecutable = 'ssh.exe',

    [string] $ResultsDirectory = $env:JOB_RESULTS_DIR
)

<#
The rollback-in-under-15-minutes drill from
docs/operations/remote-task-server-local-studio.md ("Rollback in less than
15 minutes"). Each phase below is that section's own timeline row, in order,
so the produced report reads against the same table an operator rehearses
by hand. -WhatIf previews every mutating step (remote fence, local restore,
connector switch, Runner repoint) without touching a live topology; that is
the mode the Windows CI job runs (deploy-windows-fallback-validation) since
it has no real Hetzner VM or agent-runner-01 to drive.
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
        Name         = $Name
        StartedAt    = $startedAt
        FinishedAt   = $finishedAt
        ElapsedSeconds = [Math]::Round(($finishedAt - $startedAt).TotalSeconds, 1)
    })
    Write-FallbackLog "Phase done: $Name ($([Math]::Round(($finishedAt - $startedAt).TotalSeconds, 1))s)"
}

$drillStartedAt = [DateTime]::UtcNow

Invoke-Phase '0-2 min: stop admission and fence the remote VM' {
    if (-not $PSCmdlet.ShouldProcess($RemoteBaseUrl, 'Drain and fence the remote Task Server')) { return }
    try {
        if (-not (Wait-TaskServerDrain -BaseUrl $RemoteBaseUrl -Token $RemoteToken -Reason 'switch drill' -TimeoutSeconds 90)) {
            Write-FallbackLog 'Remote drain did not settle in time; proceeding on the assumption the host is unreachable and must be fenced by the Hetzner control plane instead.'
        }
    }
    catch {
        Write-FallbackLog "Remote Task Server was not reachable at $RemoteBaseUrl ($($_.Exception.Message)); fence it through the Hetzner control plane (power off or detach network) before continuing. Loss of heartbeat alone is not fencing."
    }
}

$selectedBackupId = $null
Invoke-Phase '2-5 min: select and verify the newest local backup' {
    $selectedBackupId = if ($BackupId) { $BackupId } else { Get-LatestVerifiedFullBackupId -DataDirectory $LocalDataDirectory }
    if ([string]::IsNullOrWhiteSpace($selectedBackupId)) {
        throw "No complete local backup set was found under $LocalDataDirectory\backups\full. Run pull-warm-standby-backup.ps1 first."
    }
    Write-FallbackLog "Selected backup set $selectedBackupId."
    if (-not $PSCmdlet.ShouldProcess($selectedBackupId, 'Verify the selected backup set offline')) { return }
    $taskServerExe = 'C:\AgentOrchestrator\current\task-server.exe'
    $verifyOutput = & $taskServerExe backup verify-full $selectedBackupId "--TaskServer:DataDirectory" $LocalDataDirectory --json
    if ($LASTEXITCODE -ne 0) { throw "Backup set $selectedBackupId failed verification: $verifyOutput" }
    $verification = $verifyOutput | ConvertFrom-Json
    if (-not $verification.verified) { throw "Backup set $selectedBackupId did not verify (setSha256 mismatch)." }
    Write-FallbackLog "Backup set $selectedBackupId verified (setSha256 $($verification.summary.setSha256))."
}

Invoke-Phase '5-8 min: restore to the pre-installed Windows Task Server and enter Normal' {
    if (-not $PSCmdlet.ShouldProcess($TaskServerTaskName, "Start the local Task Server and restore backup set $selectedBackupId")) { return }
    Restart-FallbackScheduledTask -TaskName $TaskServerTaskName
    if (-not (Wait-HttpReady -Url "$($LocalListenUrl.TrimEnd('/'))/readyz" -TimeoutSeconds 60 -ExpectedText '"status":"ready"')) {
        throw "Local Task Server did not become ready at $LocalListenUrl."
    }
    $localToken = Get-TaskServerAuthToken -ServerEnvPath $LocalServerEnvPath
    Set-TaskServerMode -BaseUrl $LocalListenUrl -Token $localToken -Mode 'Maintenance' -Reason 'switch drill restore'
    $restoreResult = Invoke-ManagementRequest -BaseUrl $LocalListenUrl -Token $localToken -Method 'POST' `
        -Path "/api/v1/management/backups/full/$selectedBackupId/restore"
    if (-not $restoreResult.restored) { throw "Restore of backup set $selectedBackupId failed: $($restoreResult.message)" }
    Write-FallbackLog "Restored backup set $selectedBackupId onto the local Task Server."
    Set-TaskServerMode -BaseUrl $LocalListenUrl -Token $localToken -Mode 'Normal' -Reason 'switch drill: local Task Server is now authoritative'
    if (-not (Wait-HttpReady -Url "$($LocalListenUrl.TrimEnd('/'))/readyz" -TimeoutSeconds 30 -ExpectedText '"mode":"Normal"')) {
        throw 'Local Task Server did not report mode Normal after restore.'
    }
}

Invoke-Phase '8-10 min: switch the Studio connector to the local Task Server' {
    if (-not $PSCmdlet.ShouldProcess($ConnectorTaskName, 'Switch the Studio connector upstream to Local')) { return }
    & (Join-Path $PSScriptRoot 'switch-upstream.ps1') `
        -UpstreamProfile Local -RemoteBaseUrl $RemoteBaseUrl -LocalBaseUrl $LocalListenUrl `
        -ConnectorEnvFile $ConnectorEnvFile -ConnectorListenUrl $ConnectorListenUrl -TaskName $ConnectorTaskName
}

Invoke-Phase '10-13 min: re-enable the reverse tunnel and repoint agent-runner-01' {
    if (-not $PSCmdlet.ShouldProcess($AgentRunnerSshTarget, 'Repoint the Runner at the reverse tunnel and restart it')) { return }
    $tunnelTask = Get-ScheduledTask -TaskName $TunnelKeeperTaskName -ErrorAction SilentlyContinue
    if ($null -eq $tunnelTask) {
        throw "Reverse tunnel keeper task '$TunnelKeeperTaskName' is not registered. It must stay installed (disabled) even in normal WireGuard operation for this drill to work."
    }
    if ($tunnelTask.State -eq 'Disabled') { Enable-ScheduledTask -TaskName $TunnelKeeperTaskName | Out-Null }
    if ($tunnelTask.State -ne 'Running') { Start-ScheduledTask -TaskName $TunnelKeeperTaskName }

    $repointCommand = "sudo sed -i 's|^RUNNER_SERVER_URL=.*|RUNNER_SERVER_URL=http://127.0.0.1:$RunnerLocalPort|' $RunnerEnvPath && sudo systemctl restart $RunnerServiceName"
    $repoint = Invoke-RemoteSshCommand -SshTarget $AgentRunnerSshTarget -Command $repointCommand -SshExecutable $SshExecutable
    if ($repoint.ExitCode -ne 0) {
        throw "Could not repoint $AgentRunnerSshTarget at the reverse tunnel: $($repoint.Output)"
    }
    Write-FallbackLog "$AgentRunnerSshTarget now targets 127.0.0.1:$RunnerLocalPort through the reverse tunnel and $RunnerServiceName was restarted."
}

$runnerHealthy = $false
Invoke-Phase '13-15 min: verify Runner health and sole-writer status' {
    if (-not $PSCmdlet.ShouldProcess($AgentRunnerSshTarget, 'Verify Runner health through the reverse tunnel')) { return }
    $healthCommand = "curl -sf --max-time 6 http://127.0.0.1:$RunnerLocalPort/readyz >/dev/null && echo ok"
    $health = Invoke-RemoteSshCommand -SshTarget $AgentRunnerSshTarget -Command $healthCommand -SshExecutable $SshExecutable
    $runnerHealthy = ($health.ExitCode -eq 0) -and (($health.Output | Select-Object -Last 1).ToString().Trim() -eq 'ok')
    if (-not $runnerHealthy) {
        throw "Runner health check through the reverse tunnel did not succeed: $($health.Output)"
    }
    Write-FallbackLog "$AgentRunnerSshTarget reached the local Task Server through the reverse tunnel."
}

$drillFinishedAt = [DateTime]::UtcNow
$totalElapsedSeconds = [Math]::Round(($drillFinishedAt - $drillStartedAt).TotalSeconds, 1)
$totalElapsedMinutes = [Math]::Round($totalElapsedSeconds / 60, 2)
$withinBudget = $totalElapsedMinutes -lt 15

$report = @(
    '# Switch drill evidence: remote to local Windows fallback',
    '',
    "- Started (UTC): $($drillStartedAt.ToString('o'))",
    "- Finished (UTC): $($drillFinishedAt.ToString('o'))",
    "- Total elapsed: $totalElapsedMinutes minutes",
    "- Under 15 minutes: $withinBudget",
    "- Restored backup set: $selectedBackupId",
    '',
    '## Phases',
    ''
) + ($phases | ForEach-Object { "- $($_.Name): $($_.ElapsedSeconds)s" })

Write-FallbackReport -Lines $report -FileName 'switch-drill-remote-to-local.md' -ResultsDirectory $ResultsDirectory

if (-not $withinBudget) {
    throw "Switch drill took $totalElapsedMinutes minutes, over the 15-minute release gate."
}
