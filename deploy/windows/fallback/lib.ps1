# Shared helpers for the Windows fallback profile (Phase B slice B4):
# install/uninstall, the warm-standby backup pull, and the switch drills.
# Dot-source this file; it defines functions only and has no side effects on
# its own.
#
# This is the Windows analog of deploy/release/agent-orchestrator/lib.sh's
# api_request/set_mode/drain_for_switch/wait_ready helpers. Task Server
# semantics (Mode enum values, /readyz shape, /prepare-shutdown contract) are
# kept in exact parity with that file; change both together.

$ErrorActionPreference = 'Stop'

function Write-FallbackLog {
    param([Parameter(Mandatory)] [string] $Message)
    Write-Host "[agent-orchestrator-fallback] $Message"
}

function Get-EnvFileValue {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Key
    )
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $prefix = "$Key="
    $match = Get-Content -LiteralPath $Path |
        Where-Object { $_.TrimStart().StartsWith($prefix, [StringComparison]::Ordinal) } |
        Select-Object -Last 1
    if ($null -eq $match) { return $null }
    return $match.Substring($match.IndexOf('=') + 1).Trim()
}

function Set-EnvFileValue {
    <#
    Upserts one KEY=VALUE line in an environment file, preserving every other
    line and its order. Mirrors install-task-server-release.ps1's inline
    environment-line reconciliation, generalised to one key at a time so
    switch-upstream.ps1 can flip a single setting without touching the rest
    of studio-connector.env.
    #>
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Key,
        [Parameter(Mandatory)] [string] $Value
    )
    $parent = Split-Path -Parent $Path
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $lines = if (Test-Path -LiteralPath $Path) { @(Get-Content -LiteralPath $Path) } else { @() }
    $pattern = '^\s*{0}\s*=' -f [regex]::Escape($Key)
    $matched = $false
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match $pattern) {
            $lines[$index] = '{0}={1}' -f $Key, $Value
            $matched = $true
        }
    }
    if (-not $matched) { $lines += '{0}={1}' -f $Key, $Value }
    $lines | Set-Content -LiteralPath $Path -Encoding ascii
}

function Wait-HttpReady {
    <#
    Polls a URL until it returns HTTP 200 and, if -ExpectedText is given, the
    body contains that substring. Returns $true/$false; never throws on a
    connection failure while polling (mirrors lib.sh's wait_ready, which
    treats curl failure as "not ready yet", not as a script error).
    #>
    param(
        [Parameter(Mandatory)] [string] $Url,
        [int] $TimeoutSeconds = 60,
        [string] $ExpectedText
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200 -and
                (-not $ExpectedText -or $response.Content -like "*$ExpectedText*")) {
                return $true
            }
        }
        catch {
            # Not ready yet; keep polling until the deadline.
        }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Invoke-ManagementRequest {
    param(
        [Parameter(Mandatory)] [string] $BaseUrl,
        [string] $Token,
        [Parameter(Mandatory)] [string] $Method,
        [Parameter(Mandatory)] [string] $Path,
        [object] $Body
    )
    $headers = @{
        'X-Actor-Id'                    = 'fallback-drill'
        'X-Task-Protocol-Version'       = '2'
    }
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    $uri = "$($BaseUrl.TrimEnd('/'))$Path"
    if ($null -ne $Body) {
        return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers `
            -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 10)
    }
    return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers
}

function Set-TaskServerMode {
    param(
        [Parameter(Mandatory)] [string] $BaseUrl,
        [string] $Token,
        [Parameter(Mandatory)] [ValidateSet('Normal', 'Draining', 'ReadOnly', 'Maintenance')] [string] $Mode,
        [Parameter(Mandatory)] [string] $Reason
    )
    Invoke-ManagementRequest -BaseUrl $BaseUrl -Token $Token -Method 'PUT' -Path '/api/v1/management/mode' `
        -Body @{ mode = $Mode; reason = $Reason } | Out-Null
}

function Wait-TaskServerDrain {
    <#
    Puts the Task Server in Draining and polls /prepare-shutdown until
    safeToStop is true, exactly like lib.sh's drain_for_switch. Restores
    Normal mode and returns $false on timeout instead of leaving admission
    closed with nothing changed.
    #>
    param(
        [Parameter(Mandatory)] [string] $BaseUrl,
        [string] $Token,
        [Parameter(Mandatory)] [string] $Reason,
        [int] $TimeoutSeconds = 900
    )
    Write-FallbackLog "Closing new claims and entering drain mode ($Reason)."
    Set-TaskServerMode -BaseUrl $BaseUrl -Token $Token -Mode 'Draining' -Reason $Reason
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $response = Invoke-ManagementRequest -BaseUrl $BaseUrl -Token $Token -Method 'POST' `
            -Path '/api/v1/management/prepare-shutdown' -Body @{ reason = $Reason }
        if ($response.safeToStop) {
            Write-FallbackLog 'All leases are settled; safe shutdown is prepared.'
            return $true
        }
        Write-FallbackLog "Waiting for $($response.unresolvedAttempts) lease(s) to settle."
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    try { Set-TaskServerMode -BaseUrl $BaseUrl -Token $Token -Mode 'Normal' -Reason 'drain timed out; admission restored' } catch {}
    return $false
}

function Restart-FallbackScheduledTask {
    param(
        [Parameter(Mandatory)] [string] $TaskName,
        [int] $StopTimeoutSeconds = 30
    )
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -ne $task -and $task.State -ne 'Ready') {
        Stop-ScheduledTask -TaskName $TaskName
        $deadline = [DateTime]::UtcNow.AddSeconds($StopTimeoutSeconds)
        do {
            Start-Sleep -Milliseconds 250
            $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        } while ($task.State -ne 'Ready' -and [DateTime]::UtcNow -lt $deadline)
        if ($task.State -ne 'Ready') {
            throw "Scheduled task did not stop before restart: $TaskName"
        }
    }
    Start-ScheduledTask -TaskName $TaskName
}

function Get-LatestVerifiedFullBackupId {
    <#
    Returns the newest full-backup-set id under <DataDirectory>\backups\full
    that has a complete.json marker, or $null. Mirrors FullBackupService's
    own contract: complete.json is written last, so an interrupted set is
    never selected (task-server/FullBackupService.cs ListFullBackupsAsync).
    #>
    param([Parameter(Mandatory)] [string] $DataDirectory)
    $root = Join-Path $DataDirectory 'backups\full'
    if (-not (Test-Path -LiteralPath $root)) { return $null }
    Get-ChildItem -LiteralPath $root -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'complete.json') } |
        Sort-Object -Property Name -Descending |
        Select-Object -First 1 -ExpandProperty Name
}

function Get-TaskServerAuthToken {
    <#
    Resolves the same credential lib.sh's auth_token() uses for every
    management call: the Studio bootstrap token file named in server.env.
    Returns $null under AUTH=none.
    #>
    param([Parameter(Mandatory)] [string] $ServerEnvPath)
    $tokenFile = Get-EnvFileValue -Path $ServerEnvPath -Key 'STUDIO_AUTH_TOKEN_FILE'
    if ([string]::IsNullOrWhiteSpace($tokenFile)) {
        $tokenFile = Get-EnvFileValue -Path $ServerEnvPath -Key 'AUTH_TOKEN_FILE'
    }
    if ([string]::IsNullOrWhiteSpace($tokenFile) -or -not (Test-Path -LiteralPath $tokenFile)) { return $null }
    return (Get-Content -LiteralPath $tokenFile -TotalCount 1).Trim()
}

function Invoke-RemoteSshCommand {
    <#
    Runs one command on the SSH target and returns its stdout. Same shape as
    deploy/windows/agent-runner-tunnel/test-tunnel-watchdog-forced-kill.ps1's
    Test-TunnelHealth/kill helpers: BatchMode so a missing key fails fast
    instead of prompting, a bounded connect timeout, and the caller decides
    whether a non-zero exit is fatal.
    #>
    param(
        [Parameter(Mandatory)] [string] $SshTarget,
        [Parameter(Mandatory)] [string] $Command,
        [string] $SshExecutable = 'ssh.exe',
        [int] $ConnectTimeoutSeconds = 10
    )
    $output = & $SshExecutable -T -o BatchMode=yes -o "ConnectTimeout=$ConnectTimeoutSeconds" $SshTarget $Command 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Write-FallbackReport {
    param(
        [Parameter(Mandatory)] [string[]] $Lines,
        [Parameter(Mandatory)] [string] $FileName,
        [string] $ResultsDirectory = $env:JOB_RESULTS_DIR
    )
    if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
        $Lines
        return
    }
    New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
    $reportPath = Join-Path $ResultsDirectory $FileName
    $Lines | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "Evidence written to $reportPath"
    $Lines
}
