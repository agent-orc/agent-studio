[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $ReleasePackageRoot,

    [Parameter(Mandatory)] [string] $DataDirectory,

    [string] $InstallBase = 'C:\AgentOrchestrator',
    [string] $ConfigRoot = 'C:\ProgramData\AgentOrchestrator',

    [string] $ListenUrl = 'http://127.0.0.1:5071',
    [string] $ConnectorListenUrl = 'http://127.0.0.1:5031',

    [ValidateSet('none', 'bearer')] [string] $AuthMode = 'bearer',

    # Maintenance keeps the recovery profile warm but inactive. The product
    # installer (agent-studio-setup --target native) passes Normal because the
    # local Task Server is then the primary one.
    [ValidateSet('Maintenance', 'Normal')] [string] $RestMode = 'Maintenance',

    [switch] $NonInteractive,
    [switch] $Unattended,
    [switch] $FullStudio,
    [ValidateSet('codex', 'claude', 'skip')] [string] $ProviderCli = 'skip',
    [ValidateSet('gcm', 'token', 'deploy-key', 'skip')] [string] $GitAccess = 'skip',
    [string] $GitTokenFile,
    [string] $GitRemote,
    [switch] $PrepareDeployKeyOnly,
    [switch] $RunnerPasswordFromStdin
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
if ($PrepareDeployKeyOnly -and (-not $FullStudio -or $GitAccess -ne 'deploy-key')) {
    throw 'PrepareDeployKeyOnly requires FullStudio with deploy-key GitHub access.'
}

$packageRoot = (Resolve-Path -LiteralPath $ReleasePackageRoot).Path
$serviceTaskNames = 'AgentOrchestrator-TaskServer', 'AgentOrchestrator-Engine', 'AgentOrchestrator-StudioConnector'
if ($FullStudio) { $serviceTaskNames += 'AgentOrchestrator-StudioBackend', 'AgentOrchestrator-Runner' }
$version = (Get-Content -LiteralPath (Join-Path $packageRoot 'VERSION') -TotalCount 1).Trim()
if ([string]::IsNullOrWhiteSpace($version)) { throw "VERSION file in $packageRoot is empty." }
$releaseSha = (Get-Content -LiteralPath (Join-Path $packageRoot 'RELEASE-SHA') -TotalCount 1).Trim()

$normalizedInstallBase = [IO.Path]::GetFullPath($InstallBase).TrimEnd('\')
$normalizedDataDirectory = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
if ($normalizedDataDirectory.Equals($normalizedInstallBase, [StringComparison]::OrdinalIgnoreCase) -or
    $normalizedDataDirectory.StartsWith($normalizedInstallBase + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DataDirectory must be outside the versioned installation root.'
}

$backupDirectory = Join-Path $DataDirectory 'backups'

function New-BearerToken {
    # Instance-based RandomNumberGenerator.GetBytes(byte[]), not the static
    # .NET 6+-only GetBytes(int) convenience overload, so this also runs
    # under the .NET Framework Windows PowerShell 5.1 install base.
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

if (-not $PSCmdlet.ShouldProcess((Join-Path $InstallBase "release-$version"), 'Install the agent-orchestrator Windows fallback profile')) {
    return
}

New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
if ($FullStudio) {
    foreach ($subdirectory in 'workspace', 'runner-work', 'runner-state') {
        New-Item -ItemType Directory -Path (Join-Path $DataDirectory $subdirectory) -Force | Out-Null
    }
}
New-Item -ItemType Directory -Path $ConfigRoot -Force | Out-Null
if ($FullStudio) {
    $installAccount = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls.exe $ConfigRoot /inheritance:r /grant:r "${installAccount}:(OI)(CI)(F)" '*S-1-5-18:(OI)(CI)(F)' '*S-1-5-32-544:(OI)(CI)(F)' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to $ConfigRoot." }
}

$requiredFiles = @('task-server.exe', 'orchestrator-engine.exe', 'agent-studio-bff.exe')
if ($FullStudio) { $requiredFiles += @('OrchestratorApi.exe', 'agent-host.exe', 'browser\index.html') }
$releaseDirectory = Copy-FallbackRelease -PackageRoot $packageRoot -InstallBase $InstallBase -RequiredFiles $requiredFiles

$serverEnvPath = Join-Path $ConfigRoot 'server.env'
$engineEnvPath = Join-Path $ConfigRoot 'engine.env'
$connectorEnvPath = Join-Path $ConfigRoot 'studio-connector.env'
$backendEnvPath = Join-Path $ConfigRoot 'studio-backend.env'
$runnerEnvPath = Join-Path $ConfigRoot 'runner.env'
$ownerCodePath = Join-Path $ConfigRoot 'owner-bootstrap.code'
$runnerTokenPath = Join-Path $ConfigRoot 'runner.token'

if ($FullStudio -and -not (Test-Path -LiteralPath $ownerCodePath)) {
    Set-Content -LiteralPath $ownerCodePath -Value (New-BearerToken) -Encoding ascii -NoNewline
    Protect-FallbackSecretFile -Path $ownerCodePath
    if (-not $Unattended) {
        Write-Host "Owner bootstrap code (shown once): $(Get-Content -LiteralPath $ownerCodePath -Raw)"
        Write-Host 'Open http://127.0.0.1:4011 and enter this code on the first owner sign-up.'
    }
}
if ($FullStudio -and -not (Test-Path -LiteralPath $runnerTokenPath)) {
    Set-Content -LiteralPath $runnerTokenPath -Value (New-BearerToken) -Encoding ascii -NoNewline
    Protect-FallbackSecretFile -Path $runnerTokenPath
}

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
        Protect-FallbackSecretFile -Path $studioTokenFile
        Protect-FallbackSecretFile -Path $engineTokenFile
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
    Protect-FallbackSecretFile -Path $engineEnvPath

    @(
        '# Loopback Studio connector configuration (agent-studio-bff.exe).',
        "ASPNETCORE_URLS=$(if ($FullStudio) { 'http://127.0.0.1:4011' } else { $ConnectorListenUrl })",
        "TaskServer__BaseUrl=$ListenUrl",
        "TaskServer__AuthTokenFile=$studioTokenFile"
    ) | Set-Content -LiteralPath $connectorEnvPath -Encoding ascii

    Write-FallbackLog "Created configuration from the release package's templates in $ConfigRoot."
}
else {
    if (-not (Test-Path -LiteralPath $engineEnvPath) -or -not (Test-Path -LiteralPath $connectorEnvPath)) {
        throw "$serverEnvPath exists but engine.env or studio-connector.env is missing; refusing a partial configuration."
    }
    if ($FullStudio) {
        Set-EnvFileValue -Path $connectorEnvPath -Key 'ASPNETCORE_URLS' -Value 'http://127.0.0.1:4011'
    }
    Write-FallbackLog "Keeping existing operator credentials in $ConfigRoot."
}

if ($FullStudio) {
Set-EnvFileValue -Path $serverEnvPath -Key 'OWNER_BOOTSTRAP_CODE_FILE' -Value $ownerCodePath
Set-EnvFileValue -Path $serverEnvPath -Key 'BOOTSTRAP_RUNNER_ID' -Value 'local-coding'
Set-EnvFileValue -Path $serverEnvPath -Key 'BOOTSTRAP_RUNNER_AUTH_TOKEN_FILE' -Value $runnerTokenPath
Set-EnvFileValue -Path $connectorEnvPath -Key 'Studio__BrowserRoot' -Value (Join-Path $InstallBase 'current\browser')
Set-EnvFileValue -Path $connectorEnvPath -Key 'Studio__BackendBaseUrl' -Value $ConnectorListenUrl
}
if ($FullStudio -and -not (Test-Path -LiteralPath $backendEnvPath)) {
    @(
        "ASPNETCORE_URLS=$ConnectorListenUrl",
        "TaskRepository=$(Join-Path $DataDirectory 'workspace')",
        "TaskServer__BaseUrl=$ListenUrl",
        "TaskServer__AuthTokenFile=$(Join-Path $ConfigRoot 'studio.token')",
        'ASPNETCORE_ENVIRONMENT=Production'
    ) | Set-Content -LiteralPath $backendEnvPath -Encoding ascii
}
if ($FullStudio) {
    Set-EnvFileValue -Path $backendEnvPath -Key 'TaskServer__BaseUrl' -Value $ListenUrl
    Set-EnvFileValue -Path $backendEnvPath -Key 'TaskServer__AuthTokenFile' `
        -Value (Join-Path $ConfigRoot 'studio.token')
}
if ($FullStudio -and -not (Test-Path -LiteralPath $runnerEnvPath)) {
    @(
        "RUNNER_SERVER_URL=$ListenUrl",
        'RUNNER_ID=local-coding',
        'RUNNER_NAME=Local runner',
        'RUNNER_ROLE=coding',
        'RUNNER_CLIENT_ID=local-coding',
        "RUNNER_AUTH_TOKEN_FILE=$runnerTokenPath",
        "RUNNER_WORKDIR=$(Join-Path $DataDirectory 'runner-work')",
        "RUNNER_STATE_DIR=$(Join-Path $DataDirectory 'runner-state')",
        "RUNNER_CLI_TYPE=$(if ($ProviderCli -eq 'skip') { 'codex' } else { $ProviderCli })",
        'RUNNER_MAX_PARALLELISM=1'
    ) | Set-Content -LiteralPath $runnerEnvPath -Encoding ascii
}
if ($FullStudio) {
    if ($ProviderCli -ne 'skip') {
        Set-EnvFileValue -Path $runnerEnvPath -Key 'RUNNER_CLI_TYPE' -Value $ProviderCli
        $nativeCli = Get-Command "$ProviderCli.exe" -ErrorAction SilentlyContinue
        if ($nativeCli) {
            Set-EnvFileValue -Path $runnerEnvPath -Key "RUNNER_$($ProviderCli.ToUpperInvariant())_CLI_BIN" -Value $nativeCli.Source
        }
        else {
            $nodeExe = (Get-Command 'node.exe' -ErrorAction Stop).Source
            $npmRoot = (& npm.cmd root -g | Select-Object -Last 1).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Could not locate global npm packages for the provider CLI.' }
            $providerScript = if ($ProviderCli -eq 'codex') {
                Join-Path $npmRoot '@openai\codex\bin\codex.js'
            } else {
                Join-Path $npmRoot '@anthropic-ai\claude-code\cli.js'
            }
            if (-not (Test-Path -LiteralPath $providerScript)) {
                throw "The $ProviderCli npm entry point is missing at $providerScript. Repair the CLI installation."
            }
            Set-EnvFileValue -Path $runnerEnvPath -Key 'AGENT_STUDIO_NODE_EXE' -Value $nodeExe
            Set-EnvFileValue -Path $runnerEnvPath -Key "AGENT_STUDIO_$($ProviderCli.ToUpperInvariant())_JS" -Value $providerScript
            Set-EnvFileValue -Path $runnerEnvPath -Key "RUNNER_$($ProviderCli.ToUpperInvariant())_CLI_BIN" `
                -Value (Join-Path $InstallBase "current\provider-$ProviderCli.exe")
        }
    }
    if ($GitAccess -eq 'token') {
        if (-not $GitTokenFile -or -not (Test-Path -LiteralPath $GitTokenFile)) { throw 'A protected GitTokenFile is required.' }
        $token = (Get-Content -LiteralPath $GitTokenFile -Raw).Trim()
        if ($token -match '[\r\n]' -or [string]::IsNullOrWhiteSpace($token)) { throw 'GitTokenFile must contain one token.' }
        $credentialPath = Join-Path $ConfigRoot 'github.credentials'
        $encoded = [Uri]::EscapeDataString($token)
        Set-Content -LiteralPath $credentialPath -Value "https://x-access-token:$encoded@github.com" -Encoding ascii -NoNewline
        Protect-FallbackSecretFile -Path $credentialPath
        Set-EnvFileValue -Path $runnerEnvPath -Key 'GIT_CONFIG_COUNT' -Value '1'
        Set-EnvFileValue -Path $runnerEnvPath -Key 'GIT_CONFIG_KEY_0' -Value 'credential.helper'
        Set-EnvFileValue -Path $runnerEnvPath -Key 'GIT_CONFIG_VALUE_0' -Value "store --file=$credentialPath"
        $token = $null
    }
    elseif ($GitAccess -eq 'gcm') {
        & git.exe credential-manager --version | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Git Credential Manager is missing. Repair Git for Windows.' }
        $env:GCM_INTERACTIVE = 'never'
        $credentialLines = "protocol=https`nhost=github.com`n`n" | & git.exe -c credential.helper=manager credential fill
        if ($LASTEXITCODE -ne 0) { throw 'The current Windows account has no GitHub sign-in in Git Credential Manager. Sign in and rerun setup.' }
        $userLine = $credentialLines | Where-Object { $_.StartsWith('username=') } | Select-Object -First 1
        $passwordLine = $credentialLines | Where-Object { $_.StartsWith('password=') } | Select-Object -First 1
        if (-not $userLine -or -not $passwordLine) { throw 'Git Credential Manager returned no GitHub credential.' }
        $gitUser = [Uri]::EscapeDataString($userLine.Substring(9))
        $gitPassword = [Uri]::EscapeDataString($passwordLine.Substring(9))
        $credentialPath = Join-Path $ConfigRoot 'github.credentials'
        Set-Content -LiteralPath $credentialPath -Value "https://${gitUser}:${gitPassword}@github.com" -Encoding ascii -NoNewline
        Protect-FallbackSecretFile -Path $credentialPath
        $credentialLines = $null
        $gitPassword = $null
        Set-EnvFileValue -Path $runnerEnvPath -Key 'GIT_CONFIG_COUNT' -Value '1'
        Set-EnvFileValue -Path $runnerEnvPath -Key 'GIT_CONFIG_KEY_0' -Value 'credential.helper'
        Set-EnvFileValue -Path $runnerEnvPath -Key 'GIT_CONFIG_VALUE_0' -Value "store --file=$credentialPath"
    }
    elseif ($GitAccess -eq 'deploy-key') {
        if ($GitRemote -notmatch '^git@github\.com:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?$') {
            throw 'Deploy-key mode requires a GitHub SSH project remote.'
        }
        $keyRoot = Join-Path $ConfigRoot 'deploy-keys'
        New-Item -ItemType Directory -Path $keyRoot -Force | Out-Null
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $remoteHash = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($GitRemote)) |
                Select-Object -First 10 | ForEach-Object { $_.ToString('x2') })
        } finally { $sha.Dispose() }
        $privateKey = Join-Path $keyRoot $remoteHash
        $keyWasGenerated = -not (Test-Path -LiteralPath $privateKey)
        if ($keyWasGenerated) {
            # Windows PowerShell 5.1 can discard an empty native argument and
            # make ssh-keygen prompt for a passphrase. Pass an explicit empty
            # argv value through ProcessStartInfo instead.
            $keygen = New-Object System.Diagnostics.ProcessStartInfo
            $keygen.FileName = (Get-Command ssh-keygen.exe -ErrorAction Stop).Source
            $keygen.Arguments = '-q -t ed25519 -N "" -f "' + $privateKey + '"'
            $keygen.UseShellExecute = $false
            $keygen.CreateNoWindow = $true
            $keyProcess = [System.Diagnostics.Process]::Start($keygen)
            $keyProcess.WaitForExit()
            $keyExitCode = $keyProcess.ExitCode
            $keyProcess.Dispose()
            if ($keyExitCode -ne 0) { throw 'Could not generate the project deploy key.' }
            Protect-FallbackSecretFile -Path $privateKey
        }
        Set-Content -LiteralPath "$privateKey.remote" -Value $GitRemote -Encoding ascii -NoNewline
        Write-Host "Add this deploy key to $GitRemote with the repository access needed by the runner:"
        Write-Host (Get-Content -LiteralPath "$privateKey.pub" -Raw)
        if ($PrepareDeployKeyOnly) { return }
        if ($keyWasGenerated) {
            if ($Unattended) {
                throw "Add the public key in $privateKey.pub to $GitRemote, then rerun unattended setup to verify access."
            }
            Read-Host 'After adding the deploy key in GitHub, press Enter to verify access' | Out-Null
        }
        $sshConfig = Join-Path $keyRoot 'ssh-config'
        $configLines = @()
        $index = 0
        foreach ($record in Get-ChildItem -LiteralPath $keyRoot -Filter '*.remote' -File | Sort-Object Name) {
            $recordRemote = (Get-Content -LiteralPath $record.FullName -Raw).Trim()
            $recordKey = $record.FullName.Substring(0, $record.FullName.Length - '.remote'.Length)
            $alias = "github.com-$($record.BaseName)"
            $repo = $recordRemote.Substring('git@github.com:'.Length)
            $configLines += @("Host $alias", '  HostName github.com', '  User git',
                "  IdentityFile `"$recordKey`"", '  IdentitiesOnly yes', '  BatchMode yes')
            Set-EnvFileValue -Path $runnerEnvPath -Key "GIT_CONFIG_KEY_$index" -Value "url.git@${alias}:${repo}.insteadOf"
            Set-EnvFileValue -Path $runnerEnvPath -Key "GIT_CONFIG_VALUE_$index" -Value $recordRemote
            $index++
        }
        $configLines | Set-Content -LiteralPath $sshConfig -Encoding ascii
        Set-EnvFileValue -Path $runnerEnvPath -Key 'GIT_CONFIG_COUNT' -Value "$index"
        Set-EnvFileValue -Path $runnerEnvPath -Key 'GIT_SSH_COMMAND' -Value "ssh -F `"$sshConfig`""
    }
    if ($GitRemote) {
        Set-EnvFileValue -Path $runnerEnvPath -Key 'RUNNER_GIT_REMOTE' -Value $GitRemote
        foreach ($line in Get-Content -LiteralPath $runnerEnvPath) {
            if ($line -match '^(GIT_[A-Z0-9_]+)=(.*)$') {
                [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
            }
        }
        & git.exe ls-remote $GitRemote HEAD | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "GitHub access could not be verified for $GitRemote. Grant access and rerun setup." }
    }
}
$current = Set-FallbackCurrentRelease -InstallBase $InstallBase -ReleaseDirectory $releaseDirectory `
    -TaskNames $serviceTaskNames
$scriptRoot = Get-FallbackScriptRoot -Current $current -CallerRoot $PSScriptRoot

& (Join-Path $scriptRoot 'task-server\register-task-server.ps1') `
    -InstallRoot $current -EnvFile $serverEnvPath
& (Join-Path $scriptRoot 'orchestrator-engine\register-orchestrator-engine.ps1') `
    -InstallRoot $current -EnvFile $engineEnvPath
& (Join-Path $scriptRoot 'studio-connector\register-studio-connector.ps1') `
    -InstallRoot $current -EnvFile $connectorEnvPath
if ($FullStudio) {
& (Join-Path $scriptRoot 'fallback\register-native-service.ps1') `
    -InstallRoot $current -ExecutableName 'OrchestratorApi.exe' -EnvFile $backendEnvPath `
    -TaskName 'AgentOrchestrator-StudioBackend' -StateDirectory (Join-Path $ConfigRoot 'studio-backend')
if ($RunnerPasswordFromStdin -or -not (Get-ScheduledTask -TaskName 'AgentOrchestrator-Runner' -ErrorAction SilentlyContinue)) {
    & (Join-Path $scriptRoot 'fallback\register-native-service.ps1') `
        -InstallRoot $current -ExecutableName 'agent-host.exe' -EnvFile $runnerEnvPath `
        -TaskName 'AgentOrchestrator-Runner' -StateDirectory (Join-Path $ConfigRoot 'runner') `
        -PasswordFromStdin:$RunnerPasswordFromStdin
} else {
    Start-ScheduledTask -TaskName 'AgentOrchestrator-Runner'
}
}

if (-not (Wait-HttpReady -Url "$($ListenUrl.TrimEnd('/'))/readyz" -TimeoutSeconds 60 -ExpectedText '"status":"ready"')) {
    throw "Task Server did not become ready at $ListenUrl within 60 seconds. Inspect its Scheduled Task event log under $env:ProgramData\AgentOrchestrator\task-server."
}
$authToken = Get-TaskServerAuthToken -ServerEnvPath $serverEnvPath
Set-TaskServerMode -BaseUrl $ListenUrl -Token $authToken -Mode 'Normal' -Reason 'fallback profile install proof'
if (-not (Wait-HttpReady -Url "$($ListenUrl.TrimEnd('/'))/readyz" -TimeoutSeconds 30 -ExpectedText '"mode":"Normal"')) {
    throw "Task Server did not report mode Normal at $ListenUrl within 30 seconds."
}
$edgeUrl = if ($FullStudio) { 'http://127.0.0.1:4011' } else { $ConnectorListenUrl }
if (-not (Wait-HttpReady -Url "$($edgeUrl.TrimEnd('/'))/healthz" -TimeoutSeconds 60 -ExpectedText '"status":"live"')) {
    throw "Studio connector did not become healthy at $edgeUrl within 60 seconds. Inspect its Scheduled Task event log under $env:ProgramData\AgentOrchestrator\studio-connector."
}
if ($FullStudio -and -not (Wait-HttpReady -Url "$($ConnectorListenUrl.TrimEnd('/'))/healthz" -TimeoutSeconds 60)) {
    throw "Studio backend did not become healthy at $ConnectorListenUrl within 60 seconds. Inspect its Scheduled Task event log under $env:ProgramData\AgentOrchestrator\studio-backend."
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
if ($RestMode -eq 'Maintenance') {
    Set-TaskServerMode -BaseUrl $ListenUrl -Token $authToken -Mode 'Maintenance' -Reason 'fallback profile installed; dormant until a drill or a real switch'
    Write-FallbackLog 'Task Server rests in Maintenance mode. Point the Studio connector at the remote upstream with switch-upstream.ps1 -UpstreamProfile Remote before returning this device to normal use.'
}
else {
    Write-FallbackLog 'Task Server rests in Normal mode as the primary Task Server on this device.'
}

[pscustomobject]@{
    Version           = $version
    ReleaseSha         = $releaseSha
    Current            = $current
    DataDirectory      = $DataDirectory
    ConfigRoot         = $ConfigRoot
    ListenUrl          = $ListenUrl
    ConnectorListenUrl = $ConnectorListenUrl
}
