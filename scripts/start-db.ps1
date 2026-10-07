# Starts a local PostgreSQL 16 without Docker, using the portable binaries in
# %LOCALAPPDATA%\BrewForge\pg16. The first run creates the cluster and the
# `brewforge` database; later runs only start the server.
#
#   .\scripts\start-db.ps1
#
# Same credentials as docker-compose.yml (local development only):
#   Host=localhost;Port=5432;Database=brewforge;Username=brewforge;Password=brewforge
#
# Use either this script or `docker compose up -d`, not both: they share port 5432.

$ErrorActionPreference = 'Stop'

$pgBin  = Join-Path $env:LOCALAPPDATA 'BrewForge\pg16\pgsql\bin'
$pgData = Join-Path $env:LOCALAPPDATA 'BrewForge\pgdata'
$pgLog  = Join-Path $env:LOCALAPPDATA 'BrewForge\postgres.log'

if (-not (Test-Path (Join-Path $pgBin 'pg_ctl.exe'))) {
    throw "PostgreSQL binaries not found in $pgBin. Use 'docker compose up -d' instead."
}

if (-not (Test-Path (Join-Path $pgData 'PG_VERSION'))) {
    $pwFile = Join-Path $env:TEMP 'brewforge-pw.txt'
    Set-Content -Path $pwFile -Value 'brewforge' -Encoding ascii -NoNewline
    try {
        & (Join-Path $pgBin 'initdb.exe') -D $pgData -U brewforge -A scram-sha-256 `
            --pwfile=$pwFile -E UTF8 --locale=C | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "initdb failed (exit code $LASTEXITCODE)." }
    } finally {
        Remove-Item $pwFile -ErrorAction SilentlyContinue
    }
}

& (Join-Path $pgBin 'pg_isready.exe') -h localhost -p 5432 -q
if ($LASTEXITCODE -ne 0) {
    # Start-Process without -Wait, then poll. The call operator would let the
    # server inherit this shell's output pipe, and -Wait would wait for the
    # server itself; either way a script that captures output never returns.
    Start-Process -FilePath (Join-Path $pgBin 'pg_ctl.exe') -WindowStyle Hidden `
        -ArgumentList @('-D', "`"$pgData`"", '-l', "`"$pgLog`"", '-o', '"-p 5432"', 'start')

    $ready = $false
    foreach ($attempt in 1..60) {
        & (Join-Path $pgBin 'pg_isready.exe') -h localhost -p 5432 -q
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw "PostgreSQL did not start. See $pgLog" }
}

$env:PGPASSWORD = 'brewforge'
try {
    $exists = & (Join-Path $pgBin 'psql.exe') -h localhost -p 5432 -U brewforge -d postgres -Atc `
        "select 1 from pg_database where datname = 'brewforge'"
    if ($exists -ne '1') {
        & (Join-Path $pgBin 'createdb.exe') -h localhost -p 5432 -U brewforge brewforge
        if ($LASTEXITCODE -ne 0) { throw 'createdb failed.' }
    }
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host 'PostgreSQL 16 is running on localhost:5432 (database: brewforge).'
