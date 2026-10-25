param([Parameter(Mandatory = $true)][string] $CandidateDir)
$ErrorActionPreference = 'Stop'
$candidate = (Resolve-Path -LiteralPath $CandidateDir).Path
$setup = Join-Path $candidate 'agent-studio-setup.exe'
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
function Assert-OwnerBootstrap {
  $url = 'http://127.0.0.1:4011/api/v1/studio/auth'
  $before = Invoke-RestMethod -Uri "$url/status"
  if (-not $before.bootstrapRequired -or -not $before.bootstrapCodeRequired) {
    throw 'Studio did not require the installer owner code.'
  }
  $codeFile = Join-Path $env:ProgramData 'AgentOrchestrator\owner-bootstrap.code'
  $body = @{
    username = 'smoke-owner'
    displayName = 'Smoke owner'
    password = ([Guid]::NewGuid().ToString('N') + 'aA1!')
    bootstrapCode = (Get-Content -LiteralPath $codeFile -Raw).Trim()
  } | ConvertTo-Json -Compress
  $owner = Invoke-RestMethod -Method Post -Uri "$url/bootstrap" -ContentType 'application/json' `
    -Headers @{ Origin = 'http://127.0.0.1:4011' } -Body $body
  if ($owner.status.user.role -ne 'owner') { throw 'Studio did not create the owner.' }
  $after = Invoke-RestMethod -Uri "$url/status"
  if ($after.bootstrapRequired) { throw 'Studio owner bootstrap remained open.' }
}

try {
  $answers = Join-Path $temporaryRoot 'agent-studio-windows-smoke-answers.json'
  @{ target = 'native'; releaseDirectory = $candidate; installDirectory = $installDir;
     providerCli = 'skip'; gitAccess = 'skip' } | ConvertTo-Json |
    Set-Content -LiteralPath $answers -Encoding ascii
  Invoke-Setup -SetupArgs @('--unattended', '--answer-file', $answers)
  Assert-Tasks $true
  Assert-Healthy
  Assert-OwnerBootstrap
  $before = (Get-Content -LiteralPath (Join-Path $installDir 'installation.json') -Raw | ConvertFrom-Json).installationId
  if (-not $before) { throw 'Installation ID is missing.' }
  Invoke-Setup -SetupArgs @('uninstall', '--install-dir', $installDir)
  Assert-Tasks $false
  if (-not (Test-Path -LiteralPath (Join-Path $installDir 'data'))) { throw 'Uninstall removed the data directory.' }
  Invoke-Setup -SetupArgs @('--unattended', '--answer-file', $answers)
  Assert-Tasks $true
  Assert-Healthy
  $after = (Get-Content -LiteralPath (Join-Path $installDir 'installation.json') -Raw | ConvertFrom-Json).installationId
  if ($before -ne $after) { throw 'Reinstall changed the installation ID.' }
  Write-Host 'Windows native candidate install and retained-data reinstall passed.'
} finally {
  & $setup uninstall --install-dir $installDir --purge | Out-Null
  Remove-Item -LiteralPath $answers -Force -ErrorAction SilentlyContinue
}
