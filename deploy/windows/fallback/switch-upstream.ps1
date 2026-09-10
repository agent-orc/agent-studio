[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [ValidateSet('Remote', 'Local')] [string] $UpstreamProfile,

    [Parameter(Mandatory)] [string] $RemoteBaseUrl,
    [string] $LocalBaseUrl = 'http://127.0.0.1:5071',

    [string] $ConnectorEnvFile = 'C:\ProgramData\AgentOrchestrator\studio-connector.env',
    [string] $ConnectorListenUrl,
    [string] $TaskName = 'AgentOrchestrator-StudioConnector',

    [int] $HealthTimeoutSeconds = 30
)

<#
The atomic remote/local upstream switch for the loopback Studio connector
(gap #4 in docs/operations/remote-task-server-local-studio.md). "Atomic"
here means the same thing it means for install.sh's switch_with_health_gate:
rewrite the one setting, restart the connector under a health gate, and
automatically restore the previous setting if the new upstream does not
prove reachable. agent-studio-bff.exe reads TaskServer:BaseUrl once at
startup, so there is no in-process hot reload; the restart is the switch.
#>

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')

if ([string]::IsNullOrWhiteSpace($ConnectorListenUrl)) {
    $ConnectorListenUrl = Get-EnvFileValue -Path $ConnectorEnvFile -Key 'ASPNETCORE_URLS'
    if ([string]::IsNullOrWhiteSpace($ConnectorListenUrl)) {
        throw 'ConnectorListenUrl was not supplied and ASPNETCORE_URLS is not set in the connector env file.'
    }
}

$previousUpstream = Get-EnvFileValue -Path $ConnectorEnvFile -Key 'TaskServer__BaseUrl'
$targetUpstream = if ($UpstreamProfile -eq 'Remote') { $RemoteBaseUrl } else { $LocalBaseUrl }

if ($previousUpstream -eq $targetUpstream) {
    Write-FallbackLog "Studio connector already points at the $UpstreamProfile upstream ($targetUpstream); no switch needed."
    return
}

if (-not $PSCmdlet.ShouldProcess($TaskName, "Switch Studio connector upstream to $UpstreamProfile ($targetUpstream)")) {
    return
}

function Test-ConnectorUpstream {
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

function Set-ConnectorUpstream {
    param([string] $Upstream)
    Set-EnvFileValue -Path $ConnectorEnvFile -Key 'TaskServer__BaseUrl' -Value $Upstream
    Restart-FallbackScheduledTask -TaskName $TaskName
    if (-not (Wait-HttpReady -Url "$($ConnectorListenUrl.TrimEnd('/'))/healthz" -TimeoutSeconds $HealthTimeoutSeconds -ExpectedText '"status":"live"')) {
        return $false
    }
    return Test-ConnectorUpstream -ConnectorUrl $ConnectorListenUrl
}

Write-FallbackLog "Switching Studio connector upstream: $previousUpstream -> $targetUpstream ($UpstreamProfile)."
if (Set-ConnectorUpstream -Upstream $targetUpstream) {
    Write-FallbackLog "Studio connector now serves the $UpstreamProfile upstream ($targetUpstream)."
    return
}

Write-FallbackLog "Candidate upstream $targetUpstream did not prove reachable; restoring $previousUpstream."
if ([string]::IsNullOrWhiteSpace($previousUpstream) -or -not (Set-ConnectorUpstream -Upstream $previousUpstream)) {
    throw "Switch to $UpstreamProfile failed and the previous upstream could not be automatically restored. Inspect the Studio connector Scheduled Task and $ConnectorEnvFile by hand."
}
throw "Switch to $UpstreamProfile failed; the previous upstream ($previousUpstream) was restored and nothing else changed."
