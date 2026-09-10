[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $SshTarget,
    [Parameter(Mandatory)] [string] $LocalDataDirectory,

    [string] $RemoteDataDirectory = '/var/lib/agent-orchestrator',
    [string] $TaskServerExe = 'C:\AgentOrchestrator\current\task-server.exe',

    [ValidateRange(1, 1440)] [int] $IntervalMinutes = 5,

    [string] $TaskName = 'AgentOrchestrator-WarmStandby',
    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
)

<#
Registers pull-warm-standby-backup.ps1 on a repeating trigger so the
fallback device is never more than one backup interval behind
(docs/operations/remote-task-server-local-studio.md: "The maximum planned
recovery point is five minutes" for the remote backup timer). Matches the
default 5-minute recovery point; raise -IntervalMinutes only alongside that
timer's own interval.
#>

$ErrorActionPreference = 'Stop'
$pullScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'pull-warm-standby-backup.ps1')).Path
$powerShell = (Get-Command 'powershell.exe' -ErrorAction Stop).Source
$quotedPullScript = '"{0}"' -f ($pullScript -replace '"', '""')
$quotedLocalDataDirectory = '"{0}"' -f ($LocalDataDirectory -replace '"', '""')
$quotedTaskServerExe = '"{0}"' -f ($TaskServerExe -replace '"', '""')
$arguments = @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy', 'Bypass',
    '-File', $quotedPullScript,
    '-SshTarget', $SshTarget,
    '-RemoteDataDirectory', $RemoteDataDirectory,
    '-LocalDataDirectory', $quotedLocalDataDirectory,
    '-TaskServerExe', $quotedTaskServerExe
) -join ' '

$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) -RepetitionDuration ([TimeSpan]::MaxValue)
$principal = New-ScheduledTaskPrincipal -UserId $RunAsUser -LogonType S4U -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 10) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

if ($PSCmdlet.ShouldProcess($TaskName, "Register the warm-standby backup pull every $IntervalMinutes minute(s)")) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Description 'Pulls and verifies the newest remote Task Server backup set so the Windows fallback stays within one backup interval of the remote store.' `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Get-ScheduledTask -TaskName $TaskName
}
