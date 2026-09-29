[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $ReleasePackageRoot,

    [string] $InstallBase = 'C:\AgentOrchestrator',
    [string] $ConfigRoot = 'C:\ProgramData\AgentOrchestrator',

    [string] $ConnectorListenUrl = 'http://127.0.0.1:5031',

    # Required on the first install; an update omits both and keeps the
    # existing studio-connector.env and upstream credential.
    [string] $UpstreamUrl,
    [string] $UpstreamTokenFile
)

<#
Connector profile for agent-studio-setup --mode connector: a Windows device
that runs only the loopback Studio connector (agent-studio-bff.exe) against a
remote Task Server. It uses the same versioned release tree, current junction,
and scheduled task as the fallback profile (install-fallback-profile.ps1), but
installs no local Task Server or Engine. switch-upstream.ps1 keeps working
against the studio-connector.env this script writes.
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\fallback\lib.ps1')

$taskName = 'AgentOrchestrator-StudioConnector'
if (Get-ScheduledTask -TaskName 'AgentOrchestrator-TaskServer' -ErrorAction SilentlyContinue) {
    throw 'The Windows services profile is installed on this device; its Studio connector already runs. Use switch-upstream.ps1 to point it at a remote Task Server.'
}
$connectorEnvPath = Join-Path $ConfigRoot 'studio-connector.env'
if ([string]::IsNullOrWhiteSpace($UpstreamUrl) -ne [string]::IsNullOrWhiteSpace($UpstreamTokenFile)) {
    throw '-UpstreamUrl and -UpstreamTokenFile must be given together.'
}
if ([string]::IsNullOrWhiteSpace($UpstreamUrl) -and -not (Test-Path -LiteralPath $connectorEnvPath)) {
    throw "$connectorEnvPath does not exist; the first install needs -UpstreamUrl and -UpstreamTokenFile."
}
if ($UpstreamUrl) {
    $uri = [Uri] $UpstreamUrl
    if (-not ($uri.Scheme -eq 'https' -or ($uri.Scheme -eq 'http' -and $uri.IsLoopback))) {
        throw 'The upstream Task Server URL must use https, or http on a loopback address.'
    }
    $token = (Get-Content -LiteralPath $UpstreamTokenFile -TotalCount 1).Trim()
    if ([string]::IsNullOrWhiteSpace($token)) { throw "Token file is empty: $UpstreamTokenFile" }
}

if (-not $PSCmdlet.ShouldProcess($InstallBase, 'Install the Studio connector profile')) {
    return
}

$releaseDirectory = Copy-FallbackRelease -PackageRoot $ReleasePackageRoot -InstallBase $InstallBase `
    -RequiredFiles 'agent-studio-bff.exe'

if ($UpstreamUrl) {
    New-Item -ItemType Directory -Path $ConfigRoot -Force | Out-Null
    $tokenPath = Join-Path $ConfigRoot 'studio-connector-upstream.token'
    Set-Content -LiteralPath $tokenPath -Value $token -Encoding ascii -NoNewline
    Protect-FallbackSecretFile -Path $tokenPath
    @(
        '# Loopback Studio connector configuration (agent-studio-bff.exe) for a remote Task Server.',
        "ASPNETCORE_URLS=$ConnectorListenUrl",
        "TaskServer__BaseUrl=$($UpstreamUrl.TrimEnd('/'))",
        "TaskServer__AuthTokenFile=$tokenPath"
    ) | Set-Content -LiteralPath $connectorEnvPath -Encoding ascii
    Write-FallbackLog "Configured the Studio connector for $UpstreamUrl in $ConfigRoot."
}
else {
    Write-FallbackLog "Keeping existing connector configuration in $connectorEnvPath."
}

$current = Set-FallbackCurrentRelease -InstallBase $InstallBase -ReleaseDirectory $releaseDirectory `
    -TaskNames $taskName
$scriptRoot = Get-FallbackScriptRoot -Current $current -CallerRoot $PSScriptRoot
& (Join-Path $scriptRoot 'studio-connector\register-studio-connector.ps1') `
    -InstallRoot $current -EnvFile $connectorEnvPath | Out-Null

if (-not (Wait-HttpReady -Url "$($ConnectorListenUrl.TrimEnd('/'))/healthz" -TimeoutSeconds 60 -ExpectedText '"status":"live"')) {
    throw "Studio connector did not become healthy at $ConnectorListenUrl within 60 seconds. Inspect its Scheduled Task event log under $env:ProgramData\AgentOrchestrator\studio-connector."
}

[pscustomobject]@{
    Release            = $releaseDirectory
    Current            = $current
    ConfigRoot         = $ConfigRoot
    ConnectorListenUrl = $ConnectorListenUrl
    Upstream           = Get-EnvFileValue -Path $connectorEnvPath -Key 'TaskServer__BaseUrl'
}
