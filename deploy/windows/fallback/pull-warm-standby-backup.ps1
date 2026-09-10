[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SshTarget,
    [string] $RemoteDataDirectory = '/var/lib/agent-orchestrator',

    [Parameter(Mandatory)] [string] $LocalDataDirectory,
    [string] $TaskServerExe = 'C:\AgentOrchestrator\current\task-server.exe',

    [ValidateRange(1, 50)] [int] $KeepCount = 3,

    [string] $SshExecutable = 'ssh.exe',
    [string] $ScpExecutable = 'scp.exe'
)

<#
Warm standby (Phase B slice B4): pulls the newest verified full backup set
from the remote Task Server host to this Windows device on a schedule, so
the fallback is at most one backup interval behind
(docs/operations/remote-task-server-local-studio.md, "Data and backup
layout" and the release-gate "off-host backup restore succeeds on Linux and
Windows"). It only ever reads the remote host over SSH and verifies the
pulled set offline through the CLI; it never starts or mutates the local
Task Server instance. Actually applying a pulled set is
run-switch-drill.ps1's job, not this script's.
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

$localFullRoot = Join-Path $LocalDataDirectory 'backups\full'
New-Item -ItemType Directory -Path $localFullRoot -Force | Out-Null
$markerPath = Join-Path $localFullRoot '.last-warm-standby-pull'

$listCommand = "for candidate in `"$RemoteDataDirectory/backups/full`"/*/; do id=`$(basename `"`$candidate`"); [ -f `"`${candidate}complete.json`" ] && printf '%s\n' `"`$id`"; done | sort | tail -n 1"
$listResult = Invoke-RemoteSshCommand -SshTarget $SshTarget -Command $listCommand -SshExecutable $SshExecutable
if ($listResult.ExitCode -ne 0) {
    throw "Could not list remote backup sets on $SshTarget`: $($listResult.Output)"
}
$latestId = ($listResult.Output | Select-Object -Last 1).ToString().Trim()
if ([string]::IsNullOrWhiteSpace($latestId)) {
    Write-FallbackLog "No complete backup set was found under $SshTarget`:$RemoteDataDirectory/backups/full; nothing to pull."
    return
}

$previouslyPulled = if (Test-Path -LiteralPath $markerPath) { (Get-Content -LiteralPath $markerPath -TotalCount 1).Trim() } else { $null }
if ($previouslyPulled -eq $latestId -and (Test-Path -LiteralPath (Join-Path $localFullRoot $latestId))) {
    Write-FallbackLog "Already holding the newest remote backup set ($latestId); nothing to pull."
    return
}

$destination = Join-Path $localFullRoot $latestId
$staging = "$destination.pulling"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }

Write-FallbackLog "Pulling backup set $latestId from $SshTarget`:$RemoteDataDirectory/backups/full/$latestId."
& $ScpExecutable -o BatchMode=yes -r "${SshTarget}:$RemoteDataDirectory/backups/full/$latestId" $staging
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    throw "scp of backup set $latestId failed with exit code $LASTEXITCODE."
}
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
Move-Item -LiteralPath $staging -Destination $destination

$verifyOutput = & $TaskServerExe backup verify-full $latestId "--TaskServer:DataDirectory" $LocalDataDirectory --json
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $destination -Recurse -Force
    throw "Pulled backup set $latestId failed local verification: $verifyOutput"
}
$verification = $verifyOutput | ConvertFrom-Json
if (-not $verification.verified) {
    Remove-Item -LiteralPath $destination -Recurse -Force
    throw "Pulled backup set $latestId did not verify (setSha256 mismatch); removed the staged copy."
}

Set-Content -LiteralPath $markerPath -Value $latestId -Encoding ascii -NoNewline
Write-FallbackLog "Warm standby now holds verified backup set $latestId (setSha256 $($verification.summary.setSha256))."

$kept = Get-ChildItem -LiteralPath $localFullRoot -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'complete.json') } |
    Sort-Object -Property Name -Descending
foreach ($stale in ($kept | Select-Object -Skip $KeepCount)) {
    Write-FallbackLog "Pruning older warm-standby backup set $($stale.Name)."
    Remove-Item -LiteralPath $stale.FullName -Recurse -Force
}
