#!/usr/bin/env pwsh
# Run E2E tests in Docker environment
# Usage: ./run-e2e-tests.ps1 [-Duration <seconds>] [-Bitrate <mbps>] [-Verbose]

param(
    [int]$Duration = 10,
    [int]$Bitrate = 5,
    [switch]$Verbose,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

Write-Host "=== Jellyfin.Xtream E2E Pipeline Tests ===" -ForegroundColor Cyan
Write-Host "Duration: ${Duration}s | Target Bitrate: ${Bitrate} Mbps"
Write-Host ""

# Set environment variables
$env:E2E_STREAM_DURATION_SEC = $Duration
$env:E2E_TARGET_BITRATE_MBPS = $Bitrate

# Create results directory
$resultsDir = Join-Path $PSScriptRoot "e2e-results"
if (-not (Test-Path $resultsDir)) {
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
}

# Build and run
$composeArgs = @("compose", "-f", "docker-compose.e2e.yaml", "up")
if (-not $NoBuild) {
    $composeArgs += "--build"
}
$composeArgs += @("--abort-on-container-exit", "--exit-code-from", "e2e-tests")

Write-Host "Starting Docker E2E tests..." -ForegroundColor Yellow
Write-Host "Running: docker $($composeArgs -join ' ')" -ForegroundColor Gray
$startTime = Get-Date

& docker @composeArgs 2>&1 | ForEach-Object {
    if ($Verbose -or $_ -match "(PASS|FAIL|Error|Warning|assert|Test Run)" -or $_ -notmatch "^\s*$") {
        Write-Host $_
    }
}

$exitCode = $LASTEXITCODE
$elapsed = (Get-Date) - $startTime

Write-Host ""
Write-Host "=== Results ===" -ForegroundColor Cyan
Write-Host "Duration: $($elapsed.ToString('mm\:ss'))"

if ($exitCode -eq 0) {
    Write-Host "Status: ALL TESTS PASSED" -ForegroundColor Green
} else {
    Write-Host "Status: TESTS FAILED (exit code: $exitCode)" -ForegroundColor Red
}

# Check for TRX results file
$trxFile = Get-ChildItem -Path $resultsDir -Filter "*.trx" -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($trxFile) {
    Write-Host ""
    Write-Host "Results file: $($trxFile.FullName)" -ForegroundColor Gray

    # Parse TRX for summary
    try {
        [xml]$trx = Get-Content $trxFile.FullName
        $counters = $trx.TestRun.ResultSummary.Counters
        Write-Host "  Total:    $($counters.total)" -ForegroundColor Gray
        Write-Host "  Passed:   $($counters.passed)" -ForegroundColor Green
        Write-Host "  Failed:   $($counters.failed)" -ForegroundColor $(if ([int]$counters.failed -gt 0) { "Red" } else { "Gray" })
        Write-Host "  Skipped:  $($counters.notExecuted)" -ForegroundColor Yellow
    } catch {
        Write-Host "  (Could not parse TRX file)" -ForegroundColor Yellow
    }
}

# Cleanup
docker compose -f docker-compose.e2e.yaml down 2>$null

exit $exitCode
