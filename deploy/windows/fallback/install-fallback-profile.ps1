[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $ReleasePackageRoot,

    [Parameter(Mandatory)] [string] $DataDirectory,

    [string] $InstallBase = 'C:\AgentOrchestrator',
    [string] $ConfigRoot = 'C:\ProgramData\AgentOrchestrator',

    [string] $ListenUrl = 'http://127.0.0.1:5071',
    [string] $ConnectorListenUrl = 'http://127.0.0.1:5031',

    [ValidateSet('none', 'bearer')] [string] $AuthMode = 'bearer',

    [switch] $NonInteractive
)

<#
Windows analog of deploy/release/agent-orchestrator/install.sh, for the
fallback profile described in
docs/operations/remote-task-server-local-studio.md (Phase B slice B4):
installs task-server.exe, orchestrator-engine.exe, and agent-studio-bff.exe
from a published win-x64 release package as three Scheduled Tasks under a
versioned C:\AgentOrchestrator\<version> tree with a C:\AgentOrchestrator\
current junction, the same shape task-server already uses in
install-task-server-release.ps1. Configuration lives under
C:\ProgramData\AgentOrchestrator, one env file per service, and is created
once from the release package's templates; a second install onto the same
ConfigRoot keeps the existing operator configuration untouched.
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

$packageRoot = (Resolve-Path -LiteralPath $ReleasePackageRoot).Path
foreach ($required in 'task-server.exe', 'orchestrator-engine.exe', 'agent-studio-bff.exe', 'VERSION', 'RELEASE-SHA') {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $required))) {
        throw "Release package is incomplete; missing $required in $packageRoot."
    }
}
$version = (Get-Content -LiteralPath (Join-Path $packageRoot 'VERSION') -TotalCount 1).Trim()
if ([string]::IsNullOrWhiteSpace($version)) { throw "VERSION file in $packageRoot is empty." }
$releaseSha = (Get-Content -LiteralPath (Join-Path $packageRoot 'RELEASE-SHA') -TotalCount 1).Trim()

$normalizedInstallBase = [IO.Path]::GetFullPath($InstallBase).TrimEnd('\')
$normalizedDataDirectory = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
if ($normalizedDataDirectory.Equals($normalizedInstallBase, [StringComparison]::OrdinalIgnoreCase) -or
    $normalizedDataDirectory.StartsWith($normalizedInstallBase + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DataDirectory must be outside the versioned installation root.'
}

$releaseDirectory = Join-Path $InstallBase "release-$version"
$current = Join-Path $InstallBase 'current'
$backupDirectory = Join-Path $DataDirectory 'backups'

function New-BearerToken {
    # Instance-based RandomNumberGenerator.GetBytes(byte[]), not the static
    # .NET 6+-only GetBytes(int) convenience overload, so this also runs
    # under the .NET Framework Windows PowerShell 5.1 install base.
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

if (-not $PSCmdlet.ShouldProcess($releaseDirectory, 'Install the agent-orchestrator Windows fallback profile')) {
    return
}

New-Item -ItemType Directory -Path $InstallBase -Force | Out-Null
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $ConfigRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $releaseDirectory)) {
    $staging = "$releaseDirectory.staging.$PID"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    Copy-Item -LiteralPath $packageRoot -Destination $staging -Recurse
    Move-Item -LiteralPath $staging -Destination $releaseDirectory
    Write-FallbackLog "Staged release $version at $releaseDirectory."
}
else {
    $installedVersion = (Get-Content -LiteralPath (Join-Path $releaseDirectory 'VERSION') -TotalCount 1).Trim()
    if ($installedVersion -ne $version) {
        throw "Existing release directory has version $installedVersion, expected $version: $releaseDirectory"
    }
    Write-FallbackLog "Release $version is already staged."
}

$serverEnvPath = Join-Path $ConfigRoot 'server.env'
$engineEnvPath = Join-Path $ConfigRoot 'engine.env'
$connectorEnvPath = Join-Path $ConfigRoot 'studio-connector.env'

if (-not (Test-Path -LiteralPath $serverEnvPath)) {
    $studioToken = ''
    $engineToken = ''
    $studioTokenFile = ''
    $engineTokenFile = ''
    if ($AuthMode -eq 'bearer') {
        $studioTokenFile = Join-Path $ConfigRoot 'studio.token'
        $engineTokenFile = Join-Path $ConfigRoot 'engine.token'
        $studioToken = New-BearerToken
        $engineToken = New-BearerToken
        Set-Content -LiteralPath $studioTokenFile -Value $studioToken -Encoding ascii -NoNewline
        Set-Content -LiteralPath $engineTokenFile -Value $engineToken -Encoding ascii -NoNewline
        Write-FallbackLog "Generated Studio and Engine bootstrap credentials in $ConfigRoot. Capture them through the host administration channel; they are never printed again."
    }
    elseif (-not ($ListenUrl -match '^https?://(127\.0\.0\.1|localhost|\[::1\])(:\d+)?/?$')) {
        throw "AuthMode 'none' is permitted only for a loopback ListenUrl."
    }

    @(
        "LISTEN_URL=$ListenUrl",
        "STORE_PATH=$DataDirectory",
        "BACKUP_PATH=$backupDirectory",
        "AUTH=$AuthMode",
        "STUDIO_AUTH_TOKEN_FILE=$studioTokenFile",
        "ENGINE_AUTH_TOKEN_FILE=$engineTokenFile",
        'TaskServer__MinimumLeaseSeconds=30',
        'TaskServer__MaximumLeaseSeconds=600',
        'TaskServer__MaximumEventPayloadBytes=262144'
    ) | Set-Content -LiteralPath $serverEnvPath -Encoding ascii

    @(
        '# Orchestrator Engine API-client configuration.',
        "SERVER_URL=$ListenUrl",
        'CLIENT_ID=orchestrator-engine',
        "CLIENT_CREDENTIAL=$engineToken",
        'REVIEW_CONCURRENCY=4',
        'COUNCIL_CONCURRENCY=4',
        'POST_PROCESSING_CONCURRENCY=3',
        'GATE_DISPATCH_CONCURRENCY=2',
        'COMPLETION_JUDGE_CONCURRENCY=4',
        'POLL_SECONDS=2',
        'LEASE_SECONDS=120'
    ) | Set-Content -LiteralPath $engineEnvPath -Encoding ascii

    @(
        '# Loopback Studio connector configuration (agent-studio-bff.exe).',
        "ASPNETCORE_URLS=$ConnectorListenUrl",
        "TaskServer__BaseUrl=$ListenUrl",
        "TaskServer__AuthTokenFile=$studioTokenFile"
    ) | Set-Content -LiteralPath $connectorEnvPath -Encoding ascii

    Write-FallbackLog "Created configuration from the release package's templates in $ConfigRoot."
}
else {
    if (-not (Test-Path -LiteralPath $engineEnvPath) -or -not (Test-Path -LiteralPath $connectorEnvPath)) {
        throw "$serverEnvPath exists but engine.env or studio-connector.env is missing; refusing a partial configuration."
    }
    Write-FallbackLog "Keeping existing operator configuration in $ConfigRoot."
}

if (Test-Path -LiteralPath $current) {
    $item = Get-Item -LiteralPath $current -Force
    if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to replace non-junction current path: $current"
    }
    & cmd.exe /d /c "rmdir `"$current`""
    if ($LASTEXITCODE -ne 0) { throw 'Could not remove the previous current junction.' }
}
& cmd.exe /d /c "mklink /J `"$current`" `"$releaseDirectory`"" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not create the current junction.' }

& (Join-Path $PSScriptRoot '..\task-server\register-task-server.ps1') `
    -InstallRoot $current -EnvFile $serverEnvPath
& (Join-Path $PSScriptRoot '..\orchestrator-engine\register-orchestrator-engine.ps1') `
    -InstallRoot $current -EnvFile $engineEnvPath
& (Join-Path $PSScriptRoot '..\studio-connector\register-studio-connector.ps1') `
    -InstallRoot $current -EnvFile $connectorEnvPath

if (-not (Wait-HttpReady -Url "$($ListenUrl.TrimEnd('/'))/readyz" -TimeoutSeconds 60 -ExpectedText '"status":"ready"')) {
    throw "Task Server did not become ready at $ListenUrl within 60 seconds. Inspect its Scheduled Task event log under $env:ProgramData\AgentOrchestrator\task-server."
}
$authToken = Get-TaskServerAuthToken -ServerEnvPath $serverEnvPath
Set-TaskServerMode -BaseUrl $ListenUrl -Token $authToken -Mode 'Normal' -Reason 'fallback profile install proof'
if (-not (Wait-HttpReady -Url "$($ListenUrl.TrimEnd('/'))/readyz" -TimeoutSeconds 30 -ExpectedText '"mode":"Normal"')) {
    throw "Task Server did not report mode Normal at $ListenUrl within 30 seconds."
}
if (-not (Wait-HttpReady -Url "$($ConnectorListenUrl.TrimEnd('/'))/healthz" -TimeoutSeconds 60 -ExpectedText '"status":"live"')) {
    throw "Studio connector did not become healthy at $ConnectorListenUrl within 60 seconds. Inspect its Scheduled Task event log under $env:ProgramData\AgentOrchestrator\studio-connector."
}

<#
The install proved every process can reach Normal/healthy; that is the
"documented install brings up the local Task Server profile" acceptance
evidence. Its steady rest state while the remote Task Server is
authoritative is Maintenance, not Normal: Maintenance already refuses every
mutating route without stopping the process, so the fallback stays warm
(no cold-start latency inside a 15-minute drill budget) without ever being a
second writer. warm-standby pulls call `backup verify-full` offline through
the CLI and never touch this running instance; only run-switch-drill.ps1
moves it back to Normal, through a real backup restore.
#>
Set-TaskServerMode -BaseUrl $ListenUrl -Token $authToken -Mode 'Maintenance' -Reason 'fallback profile installed; dormant until a drill or a real switch'
Write-FallbackLog 'Task Server rests in Maintenance mode. Point the Studio connector at the remote upstream with switch-upstream.ps1 -UpstreamProfile Remote before returning this device to normal use.'

[pscustomobject]@{
    Version           = $version
    ReleaseSha         = $releaseSha
    Current            = $current
    DataDirectory      = $DataDirectory
    ConfigRoot         = $ConfigRoot
    ListenUrl          = $ListenUrl
    ConnectorListenUrl = $ConnectorListenUrl
}
