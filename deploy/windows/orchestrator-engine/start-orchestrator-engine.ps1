[CmdletBinding()]
param(
    [string] $InstallRoot = 'C:\AgentOrchestrator\current',

    [string] $ExecutableName = 'orchestrator-engine.exe',

    [string] $EnvFile = 'C:\ProgramData\AgentOrchestrator\engine.env',

    [string] $StateDirectory = (Join-Path $env:ProgramData 'AgentOrchestrator\orchestrator-engine'),

    [ValidateRange(1, 300)]
    [int] $RestartDelaySeconds = 5
)

# Detached process supervisor invoked by the AgentOrchestrator-Engine
# Scheduled Task. Same shape as start-task-server.ps1: it owns the child
# process for the life of the task, re-reads engine.env, and restarts the
# executable whenever it exits (Windows analog of `Restart=always`).

$ErrorActionPreference = 'Stop'
$executablePath = Join-Path $InstallRoot $ExecutableName
$logPath = Join-Path $StateDirectory 'events.log'
$mutexName = 'Local\AgentOrchestratorEngineStart'
$mutex = [Threading.Mutex]::new($false, $mutexName)
$ownsMutex = $false

function Write-StartLog {
    param([Parameter(Mandatory)] [string] $Message)
    $line = '{0:o} {1}' -f [DateTime]::UtcNow, $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding utf8
}

function Import-EnvironmentFile {
    param([Parameter(Mandatory)] [string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Environment file not found: $Path"
    }
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
        $separatorIndex = $trimmed.IndexOf('=')
        if ($separatorIndex -lt 1) { continue }
        $key = $trimmed.Substring(0, $separatorIndex).Trim()
        $value = $trimmed.Substring($separatorIndex + 1).Trim()
        [Environment]::SetEnvironmentVariable($key, $value, 'Process')
    }
}

try {
    $ownsMutex = $mutex.WaitOne(0)
    if (-not $ownsMutex) { exit 0 }

    New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    if (-not (Test-Path -LiteralPath $executablePath)) {
        throw "Orchestrator Engine executable not found: $executablePath"
    }

    Write-StartLog "event=supervisor_started install_root=$InstallRoot env_file=$EnvFile"

    while ($true) {
        Import-EnvironmentFile -Path $EnvFile
        $attemptId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
        $stdoutPath = Join-Path $StateDirectory "orchestrator-engine-$attemptId.stdout.log"
        $stderrPath = Join-Path $StateDirectory "orchestrator-engine-$attemptId.stderr.log"
        $process = Start-Process `
            -FilePath $executablePath `
            -WorkingDirectory $InstallRoot `
            -WindowStyle Hidden `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -PassThru
        Write-StartLog "event=process_started pid=$($process.Id) stdout=$stdoutPath stderr=$stderrPath"
        $process.WaitForExit()
        Write-StartLog "event=process_exited pid=$($process.Id) exit_code=$($process.ExitCode)"
        Start-Sleep -Seconds $RestartDelaySeconds
    }
}
catch {
    if (-not (Test-Path -LiteralPath $StateDirectory)) {
        New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
    }
    Write-StartLog "event=supervisor_failed message=$($_.Exception.Message -replace '\s+', '_')"
    exit 1
}
finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
