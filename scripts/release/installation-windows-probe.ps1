param(
  [Parameter(Mandatory = $true)][string] $CandidateDir,
  [Parameter(Mandatory = $true)][string] $PreviousCandidateDir,
  [Parameter(Mandatory = $true)][string] $ReportDir
)
$ErrorActionPreference = 'Stop'
$candidate = (Resolve-Path -LiteralPath $CandidateDir).Path
$previous = (Resolve-Path -LiteralPath $PreviousCandidateDir).Path
$setup = Join-Path $candidate 'agent-studio-setup.exe'
$previousSetup = Join-Path $previous 'agent-studio-setup.exe'
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$installDir = Join-Path $temporaryRoot 'agent-studio-installation-scenario'
if (Test-Path -LiteralPath $installDir) { throw 'Scenario install directory already exists. Use a clean host.' }
$tasks = @('AgentOrchestrator-TaskServer', 'AgentOrchestrator-Engine', 'AgentOrchestrator-StudioConnector',
  'AgentOrchestrator-StudioBackend', 'AgentOrchestrator-Runner')
foreach ($task in $tasks) {
  if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
    throw 'Agent Studio scheduled tasks already exist. Use a disposable clean Windows host for this scenario.'
  }
}

function Invoke-Setup([string[]] $SetupArgs) {
  & $setup @SetupArgs | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Setup operation failed with code $LASTEXITCODE." }
}
function Assert-Healthy {
  $ready = Invoke-RestMethod -Uri 'http://127.0.0.1:5071/readyz'
  if ($ready.status -ne 'ready' -or $ready.mode -ne 'Normal') { throw 'Task Server is not ready.' }
  $live = Invoke-RestMethod -Uri 'http://127.0.0.1:5031/healthz'
  if ($live.status -ne 'live') { throw 'Studio backend is not live.' }
  $edge = Invoke-RestMethod -Uri 'http://127.0.0.1:4011/healthz'
  if ($edge.status -ne 'live') { throw 'Studio connector is not live.' }
  $html = (Invoke-WebRequest -Uri 'http://127.0.0.1:4011/' -UseBasicParsing).Content
  if ($html -notmatch '<html') { throw 'Studio browser UI did not load.' }
  $board = Invoke-RestMethod -Uri 'http://127.0.0.1:4011/api/v1/studio/board'
  if ($null -eq $board) { throw 'Studio board did not answer from Task Server.' }
  try {
    Invoke-WebRequest -Uri 'http://127.0.0.1:4011/api/tasks/grouped' -UseBasicParsing | Out-Null
    throw 'Legacy task API is open beside the standalone Task Server.'
  } catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
  }
  $code = Join-Path $env:ProgramData 'AgentOrchestrator\owner-bootstrap.code'
  if (-not (Test-Path -LiteralPath $code) -or (Get-Item -LiteralPath $code).Length -eq 0) {
    throw 'Owner bootstrap code file is missing or empty.'
  }
  $studioTokenFile = Join-Path $env:ProgramData 'AgentOrchestrator\studio.token'
  $token = (Get-Content -LiteralPath $studioTokenFile -Raw).Trim()
  $headers = @{ Authorization = "Bearer $token" }
  $registered = $false
  for ($attempt = 0; $attempt -lt 30; $attempt++) {
    $hosts = Invoke-RestMethod -Uri 'http://127.0.0.1:5071/api/v1/management/hosts' -Headers $headers
    if (@($hosts | Where-Object { $_.runnerId -eq 'local-coding' }).Count -gt 0) {
      $registered = $true
      break
    }
    Start-Sleep -Seconds 1
  }
  if (-not $registered) { throw 'Local Runner did not register with Task Server.' }
}
function Assert-Tasks([bool] $Present) {
  foreach ($task in $tasks) {
    $found = $null -ne (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)
    if ($found -ne $Present) { throw "Scheduled task $task has unexpected state." }
  }
}
function Assert-OwnerBootstrapRequired {
  $url = 'http://127.0.0.1:4011/api/v1/studio/auth'
  $before = Invoke-RestMethod -Uri "$url/status"
  if (-not $before.bootstrapRequired -or -not $before.bootstrapCodeRequired) {
    throw 'Studio did not require the installer owner code.'
  }
}

try {
  $answers = Join-Path $temporaryRoot 'agent-studio-windows-smoke-answers.json'
  $previousAnswers = Join-Path $temporaryRoot 'agent-studio-windows-previous-answers.json'
  @{ target = 'native'; releaseDirectory = $previous; installDirectory = $installDir;
     providerCli = 'skip'; gitAccess = 'skip' } | ConvertTo-Json |
    Set-Content -LiteralPath $previousAnswers -Encoding ascii
  & $previousSetup --unattended --answer-file $previousAnswers | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Previous Windows candidate did not install.' }
  $initialId = (Get-Content -LiteralPath (Join-Path $installDir 'installation.json') -Raw | ConvertFrom-Json).InstallationId
  if (-not $initialId) { throw 'Previous installation ID is missing.' }
  Invoke-Setup -SetupArgs @('update', '--unattended', '--release-dir', $candidate, '--install-dir', $installDir)
  @{ target = 'native'; releaseDirectory = $candidate; installDirectory = $installDir;
     providerCli = 'skip'; gitAccess = 'skip' } | ConvertTo-Json |
    Set-Content -LiteralPath $answers -Encoding ascii
  Assert-Tasks $true
  Assert-Healthy
  Assert-OwnerBootstrapRequired
  $fakeRoot = Join-Path $temporaryRoot 'agent-studio-installation-fake-cli'
  dotnet publish testsupport/installation-fake-cli/installation-fake-cli.csproj --configuration Release `
    --runtime win-x64 --self-contained true -p:PublishSingleFile=true --output $fakeRoot | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'The Windows fake CLI could not be built.' }
  $version = (& $setup --version).Split()[-1]
  $runnerBinary = Join-Path $env:SystemDrive "AgentOrchestrator\release-$version\agent-host.exe"
  if (-not (Test-Path -LiteralPath $runnerBinary)) { throw 'Installed candidate runner is missing.' }
  Stop-ScheduledTask -TaskName 'AgentOrchestrator-Runner'
  New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null
  python scripts/release/installed-card-journey.py `
    --server-url http://127.0.0.1:5071 `
    --studio-token-file (Join-Path $env:ProgramData 'AgentOrchestrator\studio.token') `
    --owner-code-file (Join-Path $env:ProgramData 'AgentOrchestrator\owner-bootstrap.code') `
    --runner-binary $runnerBinary `
    --fake-cli (Join-Path $fakeRoot 'installation-fake-cli.exe') `
    --work-dir (Join-Path $installDir 'card-work') `
    --report (Join-Path $ReportDir 'windows-native-card.json')
  if ($LASTEXITCODE -ne 0) { throw 'Installed Windows card did not reach integration.' }
  Start-ScheduledTask -TaskName 'AgentOrchestrator-Runner'
  $afterBootstrap = Invoke-RestMethod -Uri 'http://127.0.0.1:4011/api/v1/studio/auth/status'
  if ($afterBootstrap.bootstrapRequired) { throw 'Studio owner bootstrap remained open.' }
  $card = Get-Content -LiteralPath (Join-Path $ReportDir 'windows-native-card.json') -Raw | ConvertFrom-Json
  $updatedManifest = Get-Content -LiteralPath (Join-Path $installDir 'installation.json') -Raw | ConvertFrom-Json
  $before = $updatedManifest.InstallationId
  if (-not $before) { throw 'Installation ID is missing.' }
  if ($before -ne $initialId) { throw 'Candidate update changed the installation ID.' }
  if ($updatedManifest.ReleaseVersion -ne $version) { throw 'Candidate update did not pin the requested version.' }
  Invoke-Setup -SetupArgs @('uninstall', '--install-dir', $installDir)
  Assert-Tasks $false
  if (-not (Test-Path -LiteralPath (Join-Path $installDir 'data'))) { throw 'Uninstall removed the data directory.' }
  Invoke-Setup -SetupArgs @('--unattended', '--answer-file', $answers)
  Assert-Tasks $true
  Assert-Healthy
  $after = (Get-Content -LiteralPath (Join-Path $installDir 'installation.json') -Raw | ConvertFrom-Json).InstallationId
  if ($before -ne $after) { throw 'Reinstall changed the installation ID.' }
  $token = (Get-Content -LiteralPath (Join-Path $env:ProgramData 'AgentOrchestrator\studio.token') -Raw).Trim()
  $retained = Invoke-RestMethod -Uri "http://127.0.0.1:5071/api/v1/projects/$($card.projectId)/tasks/$($card.taskKey)" `
    -Headers @{ Authorization = "Bearer $token"; 'X-Task-Protocol-Version' = '2' }
  if ($retained.state -ne '5-human-review') { throw 'Reinstall did not retain the integrated card.' }
  $previousVersion = (& $previousSetup --version).Split()[-1]
  @{ status = 'passed'; installationId = $before; previousVersion = $previousVersion;
     candidateVersion = $version; updatedFromPrevious = $true;
     uninstalledAndReinstalled = $true; retainedCard = $true } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDir 'windows-native-lifecycle.json') -Encoding utf8
  Write-Host 'Windows native candidate install, card integration, and retained-data reinstall passed.'
} finally {
  if (Test-Path -LiteralPath (Join-Path $installDir 'installation.json')) {
    & $setup uninstall --install-dir $installDir --purge | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Windows scenario cleanup could not purge the installation.' }
  }
  Remove-Item -LiteralPath $answers -Force -ErrorAction SilentlyContinue
  if ($previousAnswers) { Remove-Item -LiteralPath $previousAnswers -Force -ErrorAction SilentlyContinue }
  if ($fakeRoot) { Remove-Item -LiteralPath $fakeRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
