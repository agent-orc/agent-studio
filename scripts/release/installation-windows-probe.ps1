param([Parameter(Mandatory = $true)][string] $CandidateDir)
$ErrorActionPreference = 'Stop'
$candidate = (Resolve-Path -LiteralPath $CandidateDir).Path
$setup = Join-Path $candidate 'agent-studio-setup.exe'
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$installDir = Join-Path $temporaryRoot 'agent-studio-installation-scenario'
if (Test-Path -LiteralPath $installDir) { throw 'Scenario install directory already exists. Use a clean host.' }
$tasks = @('AgentOrchestrator-TaskServer', 'AgentOrchestrator-Engine', 'AgentOrchestrator-StudioConnector')
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
  if ($live.status -ne 'live') { throw 'Studio connector is not live.' }
}
function Assert-Tasks([bool] $Present) {
  foreach ($task in $tasks) {
    $found = $null -ne (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)
    if ($found -ne $Present) { throw "Scheduled task $task has unexpected state." }
  }
}

try {
  Invoke-Setup -SetupArgs @('--unattended', '--target', 'native', '--release-dir', $candidate, '--install-dir', $installDir)
  Assert-Tasks $true
  Assert-Healthy
  $before = (Get-Content -LiteralPath (Join-Path $installDir 'installation.json') -Raw | ConvertFrom-Json).installationId
  if (-not $before) { throw 'Installation ID is missing.' }
  Invoke-Setup -SetupArgs @('uninstall', '--install-dir', $installDir)
  Assert-Tasks $false
  if (-not (Test-Path -LiteralPath (Join-Path $installDir 'data'))) { throw 'Uninstall removed the data directory.' }
  Invoke-Setup -SetupArgs @('--unattended', '--target', 'native', '--release-dir', $candidate, '--install-dir', $installDir)
  Assert-Tasks $true
  Assert-Healthy
  $after = (Get-Content -LiteralPath (Join-Path $installDir 'installation.json') -Raw | ConvertFrom-Json).installationId
  if ($before -ne $after) { throw 'Reinstall changed the installation ID.' }
  Write-Host 'Windows native candidate install and retained-data reinstall passed.'
} finally {
  & $setup uninstall --install-dir $installDir --purge | Out-Null
}
