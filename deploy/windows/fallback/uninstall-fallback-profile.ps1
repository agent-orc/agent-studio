[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallBase = 'C:\AgentOrchestrator',

    [string[]] $TaskNames = @(
        'AgentOrchestrator-TaskServer',
        'AgentOrchestrator-Engine',
        'AgentOrchestrator-StudioConnector',
        'AgentOrchestrator-WarmStandby'
    ),

    <#
    Off by default: uninstall only ever removes the versioned binaries and
    the Scheduled Tasks. Configuration (server.env, tokens) and task data
    (the SQLite store and local backups) are the fallback's whole reason to
    exist and are kept unless explicitly asked to go.
    #>
    [switch] $RemoveData,
    [string] $DataDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

foreach ($taskName in $TaskNames) {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($null -eq $task) { continue }
    if (-not $PSCmdlet.ShouldProcess($taskName, 'Stop and unregister scheduled task')) { continue }
    if ($task.State -ne 'Ready') {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    }
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-FallbackLog "Unregistered scheduled task $taskName."
}

$current = Join-Path $InstallBase 'current'
if (Test-Path -LiteralPath $current) {
    $item = Get-Item -LiteralPath $current -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        if ($PSCmdlet.ShouldProcess($current, 'Remove current junction')) {
            & cmd.exe /d /c "rmdir `"$current`"" | Out-Null
        }
    }
}

if (Test-Path -LiteralPath $InstallBase) {
    Get-ChildItem -LiteralPath $InstallBase -Directory -Filter 'release-*' | ForEach-Object {
        if ($PSCmdlet.ShouldProcess($_.FullName, 'Remove versioned release directory')) {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
    }
}

if ($RemoveData) {
    if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
        throw '-RemoveData requires -DataDirectory so the correct store is removed.'
    }
    if ($PSCmdlet.ShouldProcess($DataDirectory, 'Remove Task Server data directory, including local backups')) {
        Remove-Item -LiteralPath $DataDirectory -Recurse -Force
        Write-FallbackLog "Removed data directory $DataDirectory."
    }
}
else {
    Write-FallbackLog 'Configuration and task data were left in place. Pass -RemoveData -DataDirectory <path> to remove the store and local backups too.'
}
