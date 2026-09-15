# Windows entry point for the repository preparation script.
# The Task Server runs prepare.ps1 on Windows hosts. The POSIX script stays the
# single source of truth and runs under Git Bash with the gate's environment.
# The gate starts PowerShell with a reduced environment, in which the call
# operator (&) can skip a native executable without an error. The process is
# therefore started directly and its exit code is returned unchanged.
$ErrorActionPreference = 'Stop'

$candidates = @()
if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles 'Git\bin\bash.exe' }
$candidates += 'C:\Program Files\Git\bin\bash.exe'
$gitCommand = Get-Command git.exe -ErrorAction SilentlyContinue
if ($gitCommand) {
    $candidates += Join-Path (Split-Path (Split-Path $gitCommand.Source)) 'bin\bash.exe'
}
$bash = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $bash) {
    [Console]::Error.WriteLine('prepare.ps1: Git Bash (bash.exe) is required to run .agent-studio/prepare on Windows.')
    exit 69
}

$script = (Join-Path $PSScriptRoot 'prepare').Replace([char]92, [char]47)
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $bash
$start.Arguments = '-e "' + $script + '"'
$start.UseShellExecute = $false
$start.WorkingDirectory = (Get-Location).Path
$process = [System.Diagnostics.Process]::Start($start)
$process.WaitForExit()
exit $process.ExitCode
