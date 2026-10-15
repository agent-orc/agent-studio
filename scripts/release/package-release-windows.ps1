param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $Sha,
    [Parameter(Mandatory)][string] $PublishRoot,
    [Parameter(Mandatory)][string] $LinuxReleaseRoot,
    [Parameter(Mandatory)][string] $OutputRoot
)

$ErrorActionPreference = 'Stop'
$component = "agent-orchestrator-$Version-win-x64"
$root = Join-Path $OutputRoot $component
$studioArchive = Join-Path $LinuxReleaseRoot "agent-studio-$Version.tar.gz"
foreach ($required in @(
    "$PublishRoot/task-server/task-server.exe",
    "$PublishRoot/orchestrator-engine/orchestrator-engine.exe",
    "$PublishRoot/studio-bff/agent-studio-bff.exe",
    "$PublishRoot/agent-host/agent-host.exe",
    "$PublishRoot/setup/agent-studio-setup.exe",
    "$PublishRoot/studio-backend/OrchestratorApi.exe",
    $studioArchive
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Missing Windows release input: $required"
    }
}

New-Item -ItemType Directory -Path $root -Force | Out-Null
Copy-Item "$PublishRoot/task-server/task-server.exe" $root
Copy-Item "$PublishRoot/orchestrator-engine/orchestrator-engine.exe" $root
Copy-Item "$PublishRoot/studio-bff/agent-studio-bff.exe" $root
Copy-Item "$PublishRoot/agent-host/agent-host.exe" $root
Copy-Item "$PublishRoot/agent-host/agent-host.exe" "$root/provider-codex.exe"
Copy-Item "$PublishRoot/agent-host/agent-host.exe" "$root/provider-claude.exe"
Copy-Item "$PublishRoot/setup/agent-studio-setup.exe" $root
Copy-Item "$PublishRoot/studio-backend/OrchestratorApi.exe" $root
Copy-Item "$PublishRoot/studio-backend/appsettings.json" $root
if (Test-Path "$PublishRoot/studio-backend/prompts") {
    Copy-Item "$PublishRoot/studio-backend/prompts" "$root/prompts" -Recurse
}
Copy-Item agent-rules "$root/agent-rules" -Recurse
Copy-Item deploy/windows "$root/deploy-windows" -Recurse
New-Item -ItemType Directory -Path "$root/config", "$root/studio/browser" -Force | Out-Null
Copy-Item deploy/release/agent-orchestrator/config/*.template "$root/config/"
$extract = Join-Path $OutputRoot 'studio-extract'
New-Item -ItemType Directory -Path $extract -Force | Out-Null
try {
    & tar -xzf $studioArchive -C $extract
    if ($LASTEXITCODE -ne 0) { throw 'Studio archive extraction failed.' }
    Copy-Item "$extract/agent-studio-$Version/browser/*" "$root/studio/browser/" -Recurse
    if (-not (Test-Path -LiteralPath "$root/studio/browser/index.html")) {
        throw 'Studio UI index.html is missing.'
    }
} finally {
    Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
}
Set-Content -LiteralPath "$root/VERSION" -Value $Version -NoNewline -Encoding ascii
Set-Content -LiteralPath "$root/RELEASE-SHA" -Value $Sha -NoNewline -Encoding ascii
@(
    'component=agent-orchestrator',
    "version=$Version",
    "gitSha=$Sha",
    'runtimeIdentifiers=win-x64',
    'protocolMinimum=1',
    'protocolMaximum=2'
) | Set-Content -LiteralPath "$root/RELEASE" -Encoding ascii
Compress-Archive -Path $root -DestinationPath "$OutputRoot/$component.zip" -Force
