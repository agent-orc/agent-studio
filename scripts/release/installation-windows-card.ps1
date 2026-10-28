param(
  [Parameter(Mandatory = $true)][string] $CandidateDir,
  [Parameter(Mandatory = $true)][string] $InstallDir
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$work = Join-Path $InstallDir 'scenario'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$config = Join-Path $env:ProgramData 'AgentOrchestrator'
$baseUrl = 'http://127.0.0.1:5071'
$studioToken = Join-Path $config 'studio.token'
$ownerCode = Join-Path $config 'owner-bootstrap.code'
$ownerSession = Join-Path $work 'owner.session'
$remote = $null
$workers = @()
$gitDaemon = $null

function Invoke-Git([string[]] $GitArgs) {
  & git @GitArgs | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Git fixture command failed: $($GitArgs[0])." }
}

try {
  $gitRoot = Join-Path $work 'git'
  $bare = Join-Path $gitRoot 'scenario.git'
  $seed = Join-Path $work 'seed'
  New-Item -ItemType Directory -Path $gitRoot -Force | Out-Null
  New-Item -ItemType Directory -Path (Join-Path $seed 'tests') -Force | Out-Null
  Invoke-Git -GitArgs @('init', '--bare', '--initial-branch=main', $bare)
  Invoke-Git -GitArgs @('-C', $bare, 'config', 'daemon.receivepack', 'true')
  Invoke-Git -GitArgs @('init', '--initial-branch=main', $seed)
  Set-Content -LiteralPath (Join-Path $seed 'README.md') -Value 'Installed Windows fixture' -Encoding ascii
  [IO.File]::WriteAllText((Join-Path $seed 'tests/known-passing.sh'), "#!/bin/sh`nexit 0`n")
  [IO.File]::WriteAllText((Join-Path $seed 'tests/known-failing.sh'), "#!/bin/sh`nexit 1`n")
  Invoke-Git -GitArgs @('-C', $seed, 'add', '.')
  Invoke-Git -GitArgs @('-C', $seed, '-c', 'user.name=Scenario', '-c', 'user.email=scenario@localhost',
    'commit', '-m', 'seed fixture', '--quiet')
  Invoke-Git -GitArgs @('-C', $seed, 'remote', 'add', 'origin', $bare)
  Invoke-Git -GitArgs @('-C', $seed, 'push', '-q', 'origin', 'main')
  $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
  $listener.Start()
  $port = $listener.LocalEndpoint.Port
  $listener.Stop()
  $remote = "git://127.0.0.1:$port/scenario.git"
  $gitStart = [Diagnostics.ProcessStartInfo]::new((Get-Command git.exe).Source)
  $gitStart.UseShellExecute = $false
  $gitStart.CreateNoWindow = $true
  foreach ($argument in @('daemon', '--reuseaddr', '--listen=127.0.0.1', "--port=$port",
      "--base-path=$gitRoot", '--export-all', '--enable=receive-pack', $gitRoot)) {
    $gitStart.ArgumentList.Add($argument)
  }
  $gitDaemon = [Diagnostics.Process]::Start($gitStart)
  if (-not $gitDaemon) { throw 'Git daemon could not start.' }
  $ready = $false
  for ($attempt = 0; $attempt -lt 30; $attempt++) {
    & git ls-remote $remote HEAD 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 1
  }
  if (-not $ready) { throw 'Loopback Git daemon did not start.' }

  $installedHost = Join-Path ([IO.Path]::GetPathRoot($env:ProgramData)) 'AgentOrchestrator/current/agent-host.exe'
  if (-not (Test-Path -LiteralPath $installedHost)) { throw 'Installed agent-host.exe is missing.' }
  Stop-ScheduledTask -TaskName 'AgentOrchestrator-Runner'
  $installBase = Join-Path ([IO.Path]::GetPathRoot($env:ProgramData)) 'AgentOrchestrator'
  Get-Process -Name 'agent-host' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($installBase, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force

  & python (Join-Path $repoRoot 'scripts/release/installation-first-use.py') `
    --server-url $baseUrl --studio-token-file $studioToken --owner-code-file $ownerCode `
    --owner-session-file $ownerSession --repository-url $remote | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Owner bootstrap and project registration failed.' }
  $shell = (Get-Command sh.exe -ErrorAction SilentlyContinue).Source
  if (-not $shell) { $shell = Join-Path $env:ProgramFiles 'Git/bin/bash.exe' }
  if (-not (Test-Path -LiteralPath $shell)) { throw 'Git Bash is required for fixture checks.' }
  $reviewToken = Join-Path $work 'review.token'
  $integrationToken = Join-Path $work 'integration.token'
  $cardFile = Join-Path $work 'card.json'
  & python (Join-Path $repoRoot 'scripts/release/installation-card.py') prepare `
    --server-url $baseUrl --studio-token-file $studioToken --owner-session-file $ownerSession `
    --repository-url $remote --card-file $cardFile `
    --review-token-file $reviewToken --integration-token-file $integrationToken `
    --review-shell $shell | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Installed card preparation failed.' }
  Remove-Item -LiteralPath $ownerSession -Force

  $fakeProject = Join-Path $repoRoot 'scripts/release/InstalledFakeCodex/InstalledFakeCodex.csproj'
  $fakeOutput = Join-Path $work 'fake-cli'
  & dotnet publish $fakeProject --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true --output $fakeOutput --nologo | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Windows fake CLI build failed.' }
  $fakeCodex = Join-Path $fakeOutput 'InstalledFakeCodex.exe'
  if (-not (Test-Path -LiteralPath $fakeCodex)) { throw 'Windows fake CLI is missing.' }
  New-Item -ItemType Directory -Path (Join-Path $work 'results') -Force | Out-Null
  foreach ($role in @('coding', 'review', 'integration')) {
    $runnerId = switch ($role) {
      'coding' { 'local-coding' }
      'review' { 'scenario-install-review' }
      'integration' { 'scenario-install-integration' }
    }
    $tokenFile = switch ($role) {
      'coding' { Join-Path $config 'runner.token' }
      'review' { $reviewToken }
      'integration' { $integrationToken }
    }
    $env:RUNNER_AUTH_TOKEN_FILE = $tokenFile
    $env:RUNNER_ALLOW_INSECURE_HTTP = '1'
    $env:RUNNER_SERVER_URL = $baseUrl
    $env:RUNNER_ID = $runnerId
    $env:RUNNER_NAME = $runnerId
    $env:RUNNER_ROLE = $role
    $env:RUNNER_GIT_REMOTE = $remote
    $env:RUNNER_CLI_TYPE = 'codex'
    $env:RUNNER_CODEX_CLI_BIN = $fakeCodex
    $env:SCENARIO_EXPECTED_WORK_ROOT = $work
    $env:SCENARIO_SHELL = $shell
    $env:RUNNER_WORKDIR = Join-Path $work "$role-work"
    $env:RUNNER_STATE_DIR = Join-Path $work "$role-state"
    $env:JOB_RESULTS_DIR = Join-Path $work 'results'
    $workers += Start-Process -FilePath $installedHost -ArgumentList @('--poll') `
      -WorkingDirectory $work -WindowStyle Hidden -PassThru
  }
  & python (Join-Path $repoRoot 'scripts/release/installation-card.py') verify `
    --server-url $baseUrl --studio-token-file $studioToken --repository-url $remote `
    --card-file $cardFile | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Windows installed card did not reach integration.' }
  $card = Get-Content -LiteralPath $cardFile -Raw | ConvertFrom-Json
  return $card.taskKey
} finally {
  foreach ($worker in $workers) {
    if (-not $worker.HasExited) { $worker.Kill(); $worker.WaitForExit() }
    $worker.Dispose()
  }
  if ($gitDaemon) {
    if (-not $gitDaemon.HasExited) { $gitDaemon.Kill(); $gitDaemon.WaitForExit() }
    $gitDaemon.Dispose()
  }
}
