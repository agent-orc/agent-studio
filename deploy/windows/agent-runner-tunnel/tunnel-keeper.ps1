[CmdletBinding()]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidateRange(1, 65535)]
    [int] $OrchestratorPort = 5031,

    [string] $SshExecutable = 'ssh.exe',

    [string] $StateDirectory = (Join-Path $env:LOCALAPPDATA 'AgentTaskboard\tunnel-keeper'),

    [ValidateRange(10, 180)]
    [int] $RecoveryWaitSeconds = 45
)

$ErrorActionPreference = 'Stop'
$sentinel = 'AGENT_TASK_SERVER_ROUTE_OK'
$healthUrl = "http://127.0.0.1:$RemotePort/healthz"
$forward = "${RemotePort}:127.0.0.1:${OrchestratorPort}"
$statePath = Join-Path $StateDirectory 'state.json'
$logPath = Join-Path $StateDirectory 'events.log'
$sshAttemptLogPath = Join-Path $StateDirectory 'ssh-attempts.log'
$mutexName = "Local\AgentTaskboardTunnelKeeper-$RemotePort-$SshTarget"
$mutex = [Threading.Mutex]::new($false, $mutexName)
$ownsMutex = $false

function Read-KeeperState {
    if (-not (Test-Path -LiteralPath $statePath)) { return $null }
    try { return Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json }
    catch { return $null }
}

function Write-KeeperState {
    param(
        [Parameter(Mandatory)] [string] $Status,
        [Parameter(Mandatory)] [string] $Message,
        [int] $RepairAttempts = 0
    )

    $now = [DateTime]::UtcNow
    $previous = Read-KeeperState
    $lastLoggedAt = if ($previous -and $previous.lastLoggedAt) {
        [DateTime]::Parse($previous.lastLoggedAt).ToUniversalTime()
    } else { [DateTime]::MinValue }
    $shouldLog = -not $previous -or $previous.status -ne $Status -or
        ($now - $lastLoggedAt) -ge [TimeSpan]::FromHours(1)
    if ($shouldLog) {
        $line = '{0:o} status={1} target={2} forward={3} attempts={4} message={5}' -f `
            $now, $Status, $SshTarget, $forward, $RepairAttempts, ($Message -replace '\s+', '_')
        Add-Content -LiteralPath $logPath -Value $line -Encoding utf8
        $lastLoggedAt = $now
    }

    $state = [ordered]@{
        status = $Status
        observedAt = $now.ToString('o')
        lastLoggedAt = $lastLoggedAt.ToString('o')
        sshTarget = $SshTarget
        forward = $forward
        healthUrl = $healthUrl
        repairAttempts = $RepairAttempts
        message = $Message
    }
    $temporary = "$statePath.tmp"
    $state | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $statePath -Force
}

function Test-TaskServerRoute {
    $remoteCommand =
        "curl --fail --silent --show-error --max-time 10 '$healthUrl' >/dev/null && printf '%s\n' '$sentinel'"
    $output = & $script:sshPath `
        -T -o BatchMode=yes -o ConnectTimeout=10 `
        $SshTarget $remoteCommand 2>&1
    if ($LASTEXITCODE -ne 0) { return $false }
    return @($output) -contains $sentinel
}

function Test-NativeArgument {
    param(
        [Parameter(Mandatory)] [string] $CommandLine,
        [Parameter(Mandatory)] [string] $Argument
    )

    $escaped = [Regex]::Escape($Argument)
    return $CommandLine -match ('(?:^|\s)(?:"{0}"|{0})(?=\s|$)' -f $escaped)
}

function Stop-MatchingForwards {
    $reverseForward = '(?:^|\s)-R(?:\s+|=)(?:"{0}"|{0})(?=\s|$)' -f [Regex]::Escape($forward)
    $matches = Get-CimInstance Win32_Process |
        Where-Object {
            $command = [string] $_.CommandLine
            $_.Name -eq $script:sshProcessName -and
            (Test-NativeArgument -CommandLine $command -Argument '-N') -and
            $command -match $reverseForward -and
            (Test-NativeArgument -CommandLine $command -Argument $SshTarget)
        }
    foreach ($process in $matches) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($matches) { Start-Sleep -Seconds 2 }
    return @($matches).Count
}

function Start-CapturedForward {
    $attemptId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $stdoutPath = Join-Path $StateDirectory "ssh-$attemptId.stdout.log"
    $stderrPath = Join-Path $StateDirectory "ssh-$attemptId.stderr.log"
    $arguments = @(
        '-N', '-T',
        '-o', 'BatchMode=yes',
        '-o', 'ExitOnForwardFailure=yes',
        '-o', 'ServerAliveInterval=30',
        '-o', 'ServerAliveCountMax=3',
        '-o', 'LogLevel=VERBOSE',
        '-R', $forward,
        $SshTarget
    )
    $process = Start-Process `
        -FilePath $script:sshPath `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru
    $line = '{0:o} event=ssh_started pid={1} target={2} forward={3} stdout={4} stderr={5}' -f `
        [DateTime]::UtcNow, $process.Id, $SshTarget, $forward, $stdoutPath, $stderrPath
    Add-Content -LiteralPath $sshAttemptLogPath -Value $line -Encoding utf8
    return $process
}

try {
    $ownsMutex = $mutex.WaitOne(0)
    if (-not $ownsMutex) { exit 0 }

    New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    $script:sshPath = (Get-Command $SshExecutable -ErrorAction Stop).Source
    $script:sshProcessName = [IO.Path]::GetFileName($script:sshPath)

    $previous = Read-KeeperState
    $attempts = if ($previous -and $previous.status -eq 'unreachable') {
        [int] $previous.repairAttempts + 1
    } else { 1 }
    $routeWasHealthy = Test-TaskServerRoute
    $stopped = Stop-MatchingForwards
    if ($routeWasHealthy -and $stopped -eq 0) {
        Write-KeeperState -Status 'healthy' `
            -Message 'Remote functional probe passed, but this task found no matching Windows forward to adopt.'
        exit 0
    }
    $repairMessage = if ($routeWasHealthy) {
        "Functional probe passed; stopped $stopped pre-existing matching forward process(es) so the Scheduled Task owns the replacement."
    } else {
        "Functional probe failed; stopped $stopped matching forward process(es) and started a replacement."
    }
    Write-KeeperState -Status 'unreachable' `
        -Message $repairMessage `
        -RepairAttempts $attempts

    $forwardProcess = Start-CapturedForward

    $deadline = [DateTime]::UtcNow.AddSeconds($RecoveryWaitSeconds)
    do {
        Start-Sleep -Seconds 3
        if ($forwardProcess.HasExited) {
            $line = '{0:o} event=ssh_exited pid={1} exit_code={2}' -f `
                [DateTime]::UtcNow, $forwardProcess.Id, $forwardProcess.ExitCode
            Add-Content -LiteralPath $sshAttemptLogPath -Value $line -Encoding utf8
            break
        }
        if (Test-TaskServerRoute) {
            Write-KeeperState -Status 'healthy' `
                -Message 'Replacement forward passed the remote functional probe.' `
                -RepairAttempts $attempts
            $forwardProcess.WaitForExit()
            $line = '{0:o} event=ssh_exited pid={1} exit_code={2}' -f `
                [DateTime]::UtcNow, $forwardProcess.Id, $forwardProcess.ExitCode
            Add-Content -LiteralPath $sshAttemptLogPath -Value $line -Encoding utf8
            Write-KeeperState -Status 'unreachable' `
                -Message "SSH forward exited with code $($forwardProcess.ExitCode); captured output paths are in ssh-attempts.log." `
                -RepairAttempts $attempts
            exit 4
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    $failureMessage = if ($forwardProcess.HasExited) {
        "Replacement SSH exited with code $($forwardProcess.ExitCode) before the remote functional probe passed; captured output paths are in ssh-attempts.log."
    } else {
        'Replacement forward did not pass the remote functional probe before the recovery deadline.'
    }
    Write-KeeperState -Status 'unreachable' `
        -Message $failureMessage `
        -RepairAttempts $attempts
    exit 4
}
catch {
    if (-not (Test-Path -LiteralPath $StateDirectory)) {
        New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    }
    Write-KeeperState -Status 'unreachable' -Message $_.Exception.Message -RepairAttempts 1
    exit 4
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
