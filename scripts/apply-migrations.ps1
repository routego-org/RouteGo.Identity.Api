<#
.SYNOPSIS
    Applies migrations/ to the LOCAL Postgres started by docker-compose.yml.

.DESCRIPTION
    Local stand-in for the pipeline's DbUp step until the Phase 10 pipeline exists. It follows the
    same contract (migrations/README.md): each V#### script once, in numeric order, journaled in
    jobs.schema_versions; every R__ script on every run, after the V#### scripts, unjournaled; no
    wrapping transaction (each script carries its own BEGIN/COMMIT); stop on the first error.
    The journal uses DbUp's PostgreSQL column layout so the pipeline can adopt it later.

    Never point this at a shared or AWS database: there the pipeline applies migrations.

.EXAMPLE
    docker compose up -d
    ./scripts/apply-migrations.ps1
#>
param(
    [string]$Service = "postgres",
    [string]$Database = "routego_identity",
    [string]$User = "identity_user"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$migrations = Join-Path $root "migrations"

function Invoke-Psql([string[]]$Arguments) {
    $output = & docker compose --project-directory $root exec -T $Service psql -U $User -d $Database -v ON_ERROR_STOP=1 -X -q @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "psql failed (exit $LASTEXITCODE): $($Arguments -join ' ')"
    }
    return $output
}

# The journal's schema is ensured here, as the pipeline's step must do: DbUp creates the journal
# table but not its schema.
Invoke-Psql @("-c", "CREATE SCHEMA IF NOT EXISTS jobs; CREATE TABLE IF NOT EXISTS jobs.schema_versions (schemaversionsid serial PRIMARY KEY, scriptname varchar(255) NOT NULL, applied timestamp NOT NULL);") | Out-Null

$applied = @(Invoke-Psql @("-t", "-A", "-c", "SELECT scriptname FROM jobs.schema_versions"))

$versioned = Get-ChildItem $migrations -Filter "V*__*.sql" |
    Where-Object { $_.Name -match '^V(\d+)__' } |
    Sort-Object { [int]($_.Name -replace '^V(\d+)__.*$', '$1') }
$repeatable = Get-ChildItem $migrations -Filter "R__*.sql" | Sort-Object Name

if ($versioned.Count -eq 0) {
    throw "No V#### scripts in $migrations - zero migrations applied must never look like success."
}

foreach ($script in $versioned) {
    if ($applied -contains $script.Name) {
        continue
    }
    Write-Host "applying $($script.Name)"
    Invoke-Psql @("-f", "/migrations/$($script.Name)") | Out-Null
    Invoke-Psql @("-c", "INSERT INTO jobs.schema_versions (scriptname, applied) VALUES ('$($script.Name)', now() AT TIME ZONE 'utc')") | Out-Null
}

foreach ($script in $repeatable) {
    Write-Host "running   $($script.Name) (run-always)"
    Invoke-Psql @("-f", "/migrations/$($script.Name)") | Out-Null
}

Write-Host "migrations up to date"
