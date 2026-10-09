# Drops and recreates the local development database `brewforge`, so the next
# start of the API applies the schema and the seed from scratch.
#
#   .\scripts\reset-db.ps1
#
# Everything in the local `brewforge` database is lost. It touches nothing else.

$ErrorActionPreference = 'Stop'

$pgBin = Join-Path $env:LOCALAPPDATA 'BrewForge\pg16\pgsql\bin'
& (Join-Path $PSScriptRoot 'start-db.ps1') | Out-Null

$env:PGPASSWORD = 'brewforge'
try {
    & (Join-Path $pgBin 'psql.exe') -h localhost -p 5432 -U brewforge -d postgres -q `
        -c 'DROP DATABASE IF EXISTS brewforge WITH (FORCE)' -c 'CREATE DATABASE brewforge'
    if ($LASTEXITCODE -ne 0) { throw 'Could not recreate the database.' }
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host 'Database brewforge is empty. Start the API to apply the schema and the seed.'
