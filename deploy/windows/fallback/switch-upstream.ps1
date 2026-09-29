[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [ValidateSet('Remote', 'Local')] [string] $UpstreamProfile,

    [Parameter(Mandatory)] [string] $RemoteBaseUrl,
    [string] $LocalBaseUrl = 'http://127.0.0.1:5071',

    [string] $ConnectorEnvFile = 'C:\ProgramData\AgentOrchestrator\studio-connector.env',
    [string] $ConnectorListenUrl,
    [string] $TaskName = 'AgentOrchestrator-StudioConnector',

    # Connector profile only: the pinned SHA-256 of the remote TLS edge
    # certificate and the Credential Manager target holding each upstream's
    # Studio principal credential (see set-studio-credential.ps1).
    [string] $RemoteTlsCertificateSha256,
    [string] $RemoteCredentialTarget = 'AgentStudio/TaskServer/studio-robert-windows',
    [string] $LocalCredentialTarget = 'AgentStudio/TaskServer/studio-robert-windows/local',

    [int] $HealthTimeoutSeconds = 30
)

<#
The atomic remote/local upstream switch for the loopback Studio connector
(gap #4 in docs/operations/remote-task-server-local-studio.md). "Atomic"
here means the same thing it means for install.sh's switch_with_health_gate:
rewrite the upstream settings, restart the connector under a health gate, and
automatically restore the previous settings if the new upstream does not
prove reachable. The connector reads its upstream once at startup, so there
is no in-process hot reload; the restart is the switch.

Two connector env files are recognised:

- The hardened connector profile (OrchestratorApi__Profile=connector, gate 4).
  The switch rewrites Connector__Upstream__* and Connector__CredentialTarget,
  bumps Connector__Upstream__Generation, and gates on the connector's own
  /readyz, which only reports "ready" after the attach handshake proved the
  stored credential and a shared /api/v1 and Studio hub protocol version.
- The legacy agent-studio-bff.exe forwarder written by
  install-fallback-profile.ps1. It may serve only the Local fallback: it
  reads a token file named in its env file and has no Origin, CSRF, or
  protocol negotiation, so a Remote switch through it is refused.
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

$connectorProfile = (Get-EnvFileValue -Path $ConnectorEnvFile -Key 'OrchestratorApi__Profile') -eq 'connector'
if (-not $connectorProfile -and $UpstreamProfile -eq 'Remote') {
    throw "$ConnectorEnvFile configures the legacy agent-studio-bff forwarder. A Remote upstream is attached only through the hardened connector profile (OrchestratorApi__Profile=connector); see the connector attach procedure in docs/operations/setup/single-host-task-server.md."
}

if ([string]::IsNullOrWhiteSpace($ConnectorListenUrl)) {
    $ConnectorListenUrl = if ($connectorProfile) { 'http://[::1]:5031' } else { Get-EnvFileValue -Path $ConnectorEnvFile -Key 'ASPNETCORE_URLS' }
    if ([string]::IsNullOrWhiteSpace($ConnectorListenUrl)) {
        throw 'ConnectorListenUrl was not supplied and ASPNETCORE_URLS is not set in the connector env file.'
    }
}

$targetUpstream = if ($UpstreamProfile -eq 'Remote') { $RemoteBaseUrl } else { $LocalBaseUrl }
$upstreamKey = if ($connectorProfile) { 'Connector__Upstream__BaseUrl' } else { 'TaskServer__BaseUrl' }
$previousUpstream = Get-EnvFileValue -Path $ConnectorEnvFile -Key $upstreamKey

if ($previousUpstream -eq $targetUpstream) {
    Write-FallbackLog "Studio connector already points at the $UpstreamProfile upstream ($targetUpstream); no switch needed."
    return
}

if (-not $PSCmdlet.ShouldProcess($TaskName, "Switch Studio connector upstream to $UpstreamProfile ($targetUpstream)")) {
    return
}

$profileKeys = @(
    'Connector__Upstream__BaseUrl',
    'Connector__Upstream__Mode',
    'Connector__Upstream__Generation',
    'Connector__Upstream__MaskedName',
    'Connector__Upstream__TlsCertificateSha256',
    'Connector__CredentialTarget'
)

function Get-ConnectorSettings {
    $settings = @{}
    foreach ($key in $profileKeys) { $settings[$key] = Get-EnvFileValue -Path $ConnectorEnvFile -Key $key }
    return $settings
}

function Get-TargetConnectorSettings {
    param([hashtable] $Previous)
    $generation = 0
    [void][long]::TryParse([string]$Previous['Connector__Upstream__Generation'], [ref]$generation)
    if ($UpstreamProfile -eq 'Remote') {
        if ([string]::IsNullOrWhiteSpace($RemoteTlsCertificateSha256)) {
            $RemoteTlsCertificateSha256 = $Previous['Connector__Upstream__TlsCertificateSha256']
        }
        if ([string]::IsNullOrWhiteSpace($RemoteTlsCertificateSha256)) {
            throw 'A Remote connector upstream needs -RemoteTlsCertificateSha256 (the pinned TLS edge certificate).'
        }
    }
    return @{
        'Connector__Upstream__BaseUrl' = $targetUpstream
        'Connector__Upstream__Mode' = $UpstreamProfile.ToLowerInvariant()
        'Connector__Upstream__Generation' = [string]([Math]::Max($generation, 0) + 1)
        'Connector__Upstream__MaskedName' = if ($UpstreamProfile -eq 'Remote') { 'remote-task-server' } else { 'local-task-server' }
        'Connector__Upstream__TlsCertificateSha256' = if ($UpstreamProfile -eq 'Remote') { $RemoteTlsCertificateSha256 } else { '' }
        'Connector__CredentialTarget' = if ($UpstreamProfile -eq 'Remote') { $RemoteCredentialTarget } else { $LocalCredentialTarget }
    }
}

function Test-LegacyForwarderUpstream {
    param([string] $ConnectorUrl)
    $tokenFile = Get-EnvFileValue -Path $ConnectorEnvFile -Key 'TaskServer__AuthTokenFile'
    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($tokenFile) -and (Test-Path -LiteralPath $tokenFile)) {
        $token = (Get-Content -LiteralPath $tokenFile -TotalCount 1).Trim()
        $headers['Authorization'] = "Bearer $token"
    }
    $headers['X-Task-Protocol-Version'] = '2'
    try {
        $response = Invoke-WebRequest -Uri "$($ConnectorUrl.TrimEnd('/'))/api/v1/management/status" `
            -Headers $headers -UseBasicParsing -TimeoutSec 10
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Write-AttachRefusal {
    # The connector's /readyz names the refusal (credential, protocol, or
    # reachability) in operator-readable form; surface it in the switch log.
    try {
        Invoke-WebRequest -Uri "$($ConnectorListenUrl.TrimEnd('/'))/readyz" -UseBasicParsing -TimeoutSec 10 | Out-Null
    }
    catch {
        $body = $_.ErrorDetails.Message
        if ($body) { Write-FallbackLog "Connector attach refused: $body" }
    }
}

function Set-ConnectorUpstream {
    param([hashtable] $Settings, [string] $Upstream)
    if ($connectorProfile) {
        foreach ($key in $profileKeys) {
            Set-EnvFileValue -Path $ConnectorEnvFile -Key $key -Value ([string]$Settings[$key])
        }
    }
    else {
        Set-EnvFileValue -Path $ConnectorEnvFile -Key 'TaskServer__BaseUrl' -Value $Upstream
    }
    Restart-FallbackScheduledTask -TaskName $TaskName
    if ($connectorProfile) {
        if (Wait-HttpReady -Url "$($ConnectorListenUrl.TrimEnd('/'))/readyz" -TimeoutSeconds $HealthTimeoutSeconds -ExpectedText '"status":"ready"') {
            return $true
        }
        Write-AttachRefusal
        return $false
    }
    if (-not (Wait-HttpReady -Url "$($ConnectorListenUrl.TrimEnd('/'))/healthz" -TimeoutSeconds $HealthTimeoutSeconds -ExpectedText '"status":"live"')) {
        return $false
    }
    return Test-LegacyForwarderUpstream -ConnectorUrl $ConnectorListenUrl
}

$previousSettings = if ($connectorProfile) { Get-ConnectorSettings } else { $null }
$targetSettings = if ($connectorProfile) { Get-TargetConnectorSettings -Previous $previousSettings } else { $null }

Write-FallbackLog "Switching Studio connector upstream: $previousUpstream -> $targetUpstream ($UpstreamProfile)."
if (Set-ConnectorUpstream -Settings $targetSettings -Upstream $targetUpstream) {
    Write-FallbackLog "Studio connector now serves the $UpstreamProfile upstream ($targetUpstream)."
    return
}

Write-FallbackLog "Candidate upstream $targetUpstream did not prove reachable; restoring $previousUpstream."
if ($connectorProfile -and -not [string]::IsNullOrWhiteSpace($previousUpstream)) {
    # Restoring is a new switch as far as the connector is concerned: keep the
    # previous upstream settings but move the generation forward again.
    $previousSettings['Connector__Upstream__Generation'] = [string]([long]$targetSettings['Connector__Upstream__Generation'] + 1)
}
if ([string]::IsNullOrWhiteSpace($previousUpstream) -or -not (Set-ConnectorUpstream -Settings $previousSettings -Upstream $previousUpstream)) {
    throw "Switch to $UpstreamProfile failed and the previous upstream could not be automatically restored. Inspect the Studio connector Scheduled Task and $ConnectorEnvFile by hand."
}
throw "Switch to $UpstreamProfile failed; the previous upstream ($previousUpstream) was restored and nothing else changed."
