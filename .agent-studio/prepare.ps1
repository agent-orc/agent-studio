# Windows entry point for the repository preparation script.
# The Task Server runs prepare.ps1 on Windows hosts. The POSIX script stays the
# single source of truth and runs under Git Bash with the gate's environment.
# The gate starts PowerShell with a reduced environment, in which the call
# operator (&) can skip a native executable without an error. The process is
# therefore started directly and its exit code is returned unchanged.
$ErrorActionPreference = 'Stop'

# The gate keeps only PATH, HOME, USERPROFILE and the temp variables. NuGet,
# npm and .NET need the Windows base locations as well ("Value cannot be null.
# (Parameter 'path1')" from NuGet.targets). Fill only what is missing.
function Set-DefaultEnvironment([string]$Name, [string]$Value) {
    if (-not [Environment]::GetEnvironmentVariable($Name) -and $Value) {
        [Environment]::SetEnvironmentVariable($Name, $Value)
    }
}
$systemRoot = [Environment]::GetFolderPath('Windows')
$profileRoot = if ($env:USERPROFILE) { $env:USERPROFILE } else { [Environment]::GetFolderPath('UserProfile') }
Set-DefaultEnvironment 'SystemRoot' $systemRoot
Set-DefaultEnvironment 'windir' $systemRoot
Set-DefaultEnvironment 'ComSpec' (Join-Path $systemRoot 'System32\cmd.exe')
# PowerShell appends .CPL to PATHEXT at startup, so a missing value arrives as
# '.CPL' and cmd.exe (npm lifecycle scripts) no longer resolves node.exe.
if (-not (($env:PATHEXT -split ';') -contains '.EXE')) { $env:PATHEXT = '.COM;.EXE;.BAT;.CMD;' + $env:PATHEXT }
Set-DefaultEnvironment 'ProgramData' ([Environment]::GetFolderPath('CommonApplicationData'))
Set-DefaultEnvironment 'ProgramFiles' ([Environment]::GetFolderPath('ProgramFiles'))
Set-DefaultEnvironment 'ProgramFiles(x86)' ([Environment]::GetFolderPath('ProgramFilesX86'))
Set-DefaultEnvironment 'APPDATA' (Join-Path $profileRoot 'AppData\Roaming')
Set-DefaultEnvironment 'LOCALAPPDATA' (Join-Path $profileRoot 'AppData\Local')
Set-DefaultEnvironment 'HOMEDRIVE' (Split-Path -Qualifier $profileRoot)
Set-DefaultEnvironment 'HOMEPATH' (Split-Path -NoQualifier $profileRoot)
Set-DefaultEnvironment 'USERNAME' ([Environment]::UserName)
Set-DefaultEnvironment 'COMPUTERNAME' ([Environment]::MachineName)
# A reused MSBuild node or build server keeps the environment of the process
# that started it. A node left behind by a run with the reduced environment
# fails every later restore with the same NuGet error, so preparation never
# reuses or leaves one.
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
# The gate points NUGET_PACKAGES at a per-run working folder and moves that
# folder into its cache after a miss, but the later `dotnet build --no-restore`
# is not given the new location (NETSDK1064: package not found). Restoring into
# the user's global packages folder keeps the assets file valid for the build.
Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue

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
