[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallRoot = 'C:\AgentOrchestrator\current',

    # agent-studio-bff.exe is the legacy local-fallback forwarder. The hardened
    # connector that may attach a remote Task Server is OrchestratorApi.exe
    # with OrchestratorApi__Profile=connector in the env file.
    [string] $ExecutableName = 'agent-studio-bff.exe',

    [string] $EnvFile = 'C:\ProgramData\AgentOrchestrator\studio-connector.env',

    [ValidateRange(1, 300)]
    [int] $RestartDelaySeconds = 5,

    [string] $TaskName = 'AgentOrchestrator-StudioConnector',

    [string] $StartScriptPath = (Join-Path $PSScriptRoot 'start-studio-connector.ps1'),

    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name,

    # The connector profile reads its credential from the user's Windows
    # Credential Manager, which an S4U session cannot decrypt (no DPAPI user
    # key without a password logon). It therefore runs in the Studio user's
    # interactive session, started at logon: the browser it serves only
    # exists while that user is logged on. The legacy forwarder keeps S4U.
    [ValidateSet('S4U', 'Interactive')]
    [string] $LogonType = $(if ($ExecutableName -ieq 'agent-studio-bff.exe') { 'S4U' } else { 'Interactive' })
)

# Registers the loopback Studio connector described in
# docs/operations/remote-task-server-local-studio.md: a stateless credential
# and transport boundary between Robert's Angular Studio and whichever Task
# Server is currently upstream (remote WireGuard origin or the local fallback
# Task Server). switch-upstream.ps1 flips the upstream settings in
# studio-connector.env and restarts this task; it never edits Angular.

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
    '-ExecutableName', ('"{0}"' -f ($ExecutableName -replace '"', '""')),
    '-EnvFile', $quotedEnvFile,
    '-RestartDelaySeconds', $RestartDelaySeconds
) -join ' '

$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
$trigger = if ($LogonType -eq 'Interactive') {
    New-ScheduledTaskTrigger -AtLogOn -User $RunAsUser
}
else {
    New-ScheduledTaskTrigger -AtStartup
}
$principal = New-ScheduledTaskPrincipal `
    -UserId $RunAsUser `
    -LogonType $LogonType `
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
        -Description "Runs the loopback Studio connector ($ExecutableName) as a $LogonType scheduled task, restarting it if it exits." `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Get-ScheduledTask -TaskName $TaskName
}
