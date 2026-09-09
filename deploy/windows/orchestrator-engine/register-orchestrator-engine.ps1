[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallRoot = 'C:\AgentOrchestrator\current',

    [string] $EnvFile = 'C:\ProgramData\AgentOrchestrator\engine.env',

    [ValidateRange(1, 300)]
    [int] $RestartDelaySeconds = 5,

    [string] $TaskName = 'AgentOrchestrator-Engine',

    [string] $StartScriptPath = (Join-Path $PSScriptRoot 'start-orchestrator-engine.ps1'),

    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)

# The Windows analog of deploy/systemd/agent-orchestrator-engine.service's
# `After=agent-task-server.service`: this Scheduled Task is registered and
# started after the Task Server task, never before it, so the Engine's first
# poll has a live server to reach.

$ErrorActionPreference = 'Stop'
$startScript = (Resolve-Path -LiteralPath $StartScriptPath).Path
$powerShell = (Get-Command 'powershell.exe' -ErrorAction Stop).Source
$quotedStartScript = '"{0}"' -f ($startScript -replace '"', '""')
$quotedInstallRoot = '"{0}"' -f ($InstallRoot -replace '"', '""')
$quotedEnvFile = '"{0}"' -f ($EnvFile -replace '"', '""')
$arguments = @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy', 'Bypass',
    '-File', $quotedStartScript,
    '-InstallRoot', $quotedInstallRoot,
    '-EnvFile', $quotedEnvFile,
    '-RestartDelaySeconds', $RestartDelaySeconds
) -join ' '

$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal `
    -UserId $RunAsUser `
    -LogonType S4U `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

if ($PSCmdlet.ShouldProcess($TaskName, 'Register or update the Orchestrator Engine scheduled task')) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Description 'Runs the Agent Orchestrator Engine as a non-interactive S4U scheduled task (never a session task), restarting it if it exits.' `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Get-ScheduledTask -TaskName $TaskName
}
