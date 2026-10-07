# Puts the per-user .NET SDK and the portable PostgreSQL tools on PATH for the
# current PowerShell session only. Dot-source it:
#
#   . .\scripts\dev-env.ps1
#
# Nothing is written to the machine or user environment.

$dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$pgBin     = Join-Path $env:LOCALAPPDATA 'BrewForge\pg16\pgsql\bin'

if (Test-Path (Join-Path $dotnetDir 'dotnet.exe')) {
    $env:DOTNET_ROOT = $dotnetDir
    if ($env:PATH -notlike "*$dotnetDir*") { $env:PATH = "$dotnetDir;$env:PATH" }
}
if (Test-Path (Join-Path $pgBin 'psql.exe')) {
    if ($env:PATH -notlike "*$pgBin*") { $env:PATH = "$pgBin;$env:PATH" }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
