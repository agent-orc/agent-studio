[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallRoot = 'C:\AgentOrchestrator\current',

    [string] $EnvFile = 'C:\ProgramData\AgentOrchestrator\studio-connector.env',

    [ValidateRange(1, 300)]
    [int] $RestartDelaySeconds = 5,

    [string] $TaskName = 'AgentOrchestrator-StudioConnector',

    [string] $StartScriptPath = (Join-Path $PSScriptRoot 'start-studio-connector.ps1'),

    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)

# Registers the loopback Studio connector (agent-studio-bff.exe) described in
# docs/operations/remote-task-server-local-studio.md: a stateless credential
# and transport boundary between Robert's Angular Studio and whichever Task
# Server is currently upstream (remote WireGuard origin or the local fallback
# Task Server). switch-upstream.ps1 flips studio-connector.env's
# TaskServer__BaseUrl and restarts this task; it never edits Angular.

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

if ($PSCmdlet.ShouldProcess($TaskName, 'Register or update the Studio connector scheduled task')) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Description 'Runs the loopback Studio connector (agent-studio-bff) as a non-interactive S4U scheduled task, restarting it if it exits.' `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Get-ScheduledTask -TaskName $TaskName
}
