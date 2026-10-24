param(
    [Parameter(Mandatory)] [string] $InstallRoot,
    [Parameter(Mandatory)] [string] $ExecutableName,
    [Parameter(Mandatory)] [string] $EnvFile,
    [Parameter(Mandatory)] [string] $TaskName,
    [Parameter(Mandatory)] [string] $StateDirectory,
    [string] $RunAsUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name,
    [switch] $PasswordFromStdin
)

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'start-native-service.ps1'
$arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -InstallRoot "{1}" -ExecutableName "{2}" -EnvFile "{3}" -StateDirectory "{4}"' -f `
    $script, $InstallRoot, $ExecutableName, $EnvFile, $StateDirectory
$action = New-ScheduledTaskAction -Execute (Get-Command powershell.exe).Source -Argument $arguments
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId $RunAsUser -LogonType S4U -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
if ($PasswordFromStdin) {
    $runnerPassword = [Console]::In.ReadLine()
    if ([string]::IsNullOrEmpty($runnerPassword)) { throw 'The Runner account password was not received on stdin.' }
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -User $RunAsUser -Password $runnerPassword -Settings $settings -Force | Out-Null
    $runnerPassword = $null
} else {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
}
Start-ScheduledTask -TaskName $TaskName
