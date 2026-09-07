[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidatePattern('^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string] $SshTarget = 'agent-runner',

    [ValidateRange(1, 65535)]
    [int] $RemotePort = 15031,

    [ValidateRange(1, 65535)]
    [int] $OrchestratorPort = 5031,

    [ValidateRange(1, 60)]
    [int] $IntervalMinutes = 5,

    [string] $TaskName = 'AgentRunner-TunnelKeeper',

    [string] $KeeperPath = (Join-Path $PSScriptRoot 'tunnel-keeper.ps1')
)

$ErrorActionPreference = 'Stop'
$keeper = (Resolve-Path -LiteralPath $KeeperPath).Path
$powerShell = (Get-Command 'powershell.exe' -ErrorAction Stop).Source
$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$quotedKeeper = '"{0}"' -f ($keeper -replace '"', '""')
$arguments = @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy', 'Bypass',
    '-File', $quotedKeeper,
    '-SshTarget', $SshTarget,
    '-RemotePort', $RemotePort,
    '-OrchestratorPort', $OrchestratorPort
) -join ' '

$action = New-ScheduledTaskAction -Execute $powerShell -Argument $arguments
$periodicTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At ([DateTime]::Now.AddMinutes(1)) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
$startupTrigger = New-ScheduledTaskTrigger -AtStartup
$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$principal = New-ScheduledTaskPrincipal `
    -UserId $userId `
    -LogonType S4U `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

if ($PSCmdlet.ShouldProcess($TaskName, 'Register or update the tunnel keeper scheduled task')) {
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Description 'Functionally probes and repairs the private Agent Host reverse tunnel.' `
        -Action $action `
        -Trigger @($startupTrigger, $logonTrigger, $periodicTrigger) `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    Get-ScheduledTask -TaskName $TaskName
}
