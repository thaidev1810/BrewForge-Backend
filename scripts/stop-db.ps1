# Stops the local PostgreSQL started by start-db.ps1. The data is kept.

$pgBin  = Join-Path $env:LOCALAPPDATA 'BrewForge\pg16\pgsql\bin'
$pgData = Join-Path $env:LOCALAPPDATA 'BrewForge\pgdata'

& (Join-Path $pgBin 'pg_ctl.exe') -D $pgData -m fast -w stop
