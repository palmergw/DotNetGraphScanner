#!/usr/bin/env pwsh
# scan-samples.ps1
# Scans all three sample APIs, pushes each to the local Neo4j/Memgraph database,
# then runs the built-in diagnostic to confirm what was captured.
#
# Usage:
#   .\scan-samples.ps1                      # use default bolt://127.0.0.1:7687 (no auth)
#   .\scan-samples.ps1 -BoltUrl bolt://...  # custom URL
#   .\scan-samples.ps1 -User neo4j -Pass secret
param(
    [string]$BoltUrl = "bolt://127.0.0.1:7687",
    [string]$User    = "",
    [string]$Pass    = "",
    [string]$Output  = (Join-Path $PSScriptRoot ".." "out-db")
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

function Invoke-Scan([string]$csproj) {
    $args = @(
        "run", "--",
        "scan", $csproj,
        "--output", $Output,
        "--push",
        "--neo4j-url", $BoltUrl,
        "--neo4j-user", $User
    )
    if ($Pass) { $args += @("--neo4j-pass", $Pass) }

    Write-Host ""
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host "  Scanning: $csproj" -ForegroundColor Cyan
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "Scan failed for $csproj (exit $LASTEXITCODE)" }
}

Push-Location $here
try {
    Invoke-Scan (Join-Path "." "samples" "ApiContracts" "ApiContracts.csproj")
    Invoke-Scan (Join-Path "." "samples" "FooApi" "FooApi.csproj")
    Invoke-Scan (Join-Path "." "samples" "BarApi" "BarApi.csproj")

    Write-Host ""
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Magenta
    Write-Host "  Generating cross-API live HTML" -ForegroundColor Magenta
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Magenta

    $crossArgs = @(
        "run", "--",
        "cross-view",
        "--output", $Output,
        "--neo4j-url", $BoltUrl,
        "--neo4j-user", $User
    )
    if ($Pass) { $crossArgs += @("--neo4j-pass", $Pass) }
    & dotnet @crossArgs
    if ($LASTEXITCODE -ne 0) { throw "cross-view failed (exit $LASTEXITCODE)" }

    Write-Host ""
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
    Write-Host "  Running diagnostic" -ForegroundColor Green
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green

    $diagArgs = @(
        "run", "--",
        "diagnostic",
        "--neo4j-url", $BoltUrl,
        "--neo4j-user", $User
    )
    if ($Pass) { $diagArgs += @("--neo4j-pass", $Pass) }
    & dotnet @diagArgs
}
finally {
    Pop-Location
}
