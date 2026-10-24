param(
    [Parameter(Mandatory)] [string] $InstallRoot,
    [Parameter(Mandatory)] [string] $ExecutableName,
    [Parameter(Mandatory)] [string] $EnvFile,
    [Parameter(Mandatory)] [string] $StateDirectory
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
while ($true) {
    if (-not (Test-Path -LiteralPath $EnvFile)) { throw "Environment file not found: $EnvFile" }
    foreach ($line in Get-Content -LiteralPath $EnvFile) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        $separator = $trimmed.IndexOf('=')
        if ($separator -lt 1) { continue }
        [Environment]::SetEnvironmentVariable($trimmed.Substring(0, $separator), $trimmed.Substring($separator + 1), 'Process')
    }
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $process = Start-Process -FilePath (Join-Path $InstallRoot $ExecutableName) `
        -WorkingDirectory $InstallRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $StateDirectory "$stamp.stdout.log") `
        -RedirectStandardError (Join-Path $StateDirectory "$stamp.stderr.log")
    $process.WaitForExit()
    Start-Sleep -Seconds 5
}
