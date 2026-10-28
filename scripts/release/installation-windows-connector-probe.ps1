param(
  [Parameter(Mandatory = $true)][string] $CandidateDir,
  [Parameter(Mandatory = $true)][string] $ServerUrl,
  [Parameter(Mandatory = $true)][string] $TokenFile
)
$ErrorActionPreference = 'Stop'
$candidate = (Resolve-Path -LiteralPath $CandidateDir).Path
$token = (Resolve-Path -LiteralPath $TokenFile).Path
$setup = Join-Path $candidate 'agent-studio-setup.exe'
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$installDir = Join-Path $temporaryRoot 'agent-studio-remote-connector-scenario'
if (Test-Path -LiteralPath $installDir) { throw 'Scenario install directory already exists. Use a clean host.' }
foreach ($task in @('AgentOrchestrator-TaskServer', 'AgentOrchestrator-Engine', 'AgentOrchestrator-StudioConnector')) {
  if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
    throw 'Agent Studio scheduled tasks already exist. Use a disposable clean Windows host for this scenario.'
  }
}
try {
  & $setup --unattended --mode connector --server-url $ServerUrl --token-file $token --release-dir $candidate --install-dir $installDir | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Windows connector setup failed.' }
  $live = Invoke-RestMethod -Uri 'http://127.0.0.1:5031/healthz'
  if ($live.status -ne 'live') { throw 'Windows connector did not become live.' }
  if (-not (Get-ScheduledTask -TaskName 'AgentOrchestrator-StudioConnector' -ErrorAction SilentlyContinue)) {
    throw 'Windows connector task is not registered.'
  }
  if (Get-ScheduledTask -TaskName 'AgentOrchestrator-TaskServer' -ErrorAction SilentlyContinue) {
    throw 'Connector profile installed a local authority.'
  }
  $bearer = (Get-Content -LiteralPath $token -Raw).Trim()
  if (-not $bearer) { throw 'Remote authority token file is empty.' }
  $headers = @{ Authorization = "Bearer $bearer"; 'X-Task-Protocol-Version' = '2' }
  $hosts = Invoke-RestMethod -Uri "$($ServerUrl.TrimEnd('/'))/api/v1/management/remote-hosts" -Headers $headers
  if (@($hosts | Where-Object { $_.runnerId -eq 'agent-runner-01' }).Count -ne 1) {
    throw 'The real Linux agent-runner-01 host is not registered on the remote authority.'
  }
  Write-Host 'Windows Studio connector reached the remote Linux authority.'
} finally {
  if (Test-Path -LiteralPath (Join-Path $installDir 'installation.json')) {
    & $setup uninstall --install-dir $installDir --purge | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Connector scenario cleanup could not purge the installation.' }
  }
}
