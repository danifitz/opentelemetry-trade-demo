#######################################################################
# Tracing Demo Runner Script (Windows PowerShell)
# Demonstrates W3C Trace Context propagation via:
# 1. Azure Service Bus (automatic)
# 2. PostgreSQL database (manual - storing traceparent)
#######################################################################

$ErrorActionPreference = "Stop"

# Colors for output
function Write-Info { param($msg) Write-Host $msg -ForegroundColor Blue }
function Write-Success { param($msg) Write-Host $msg -ForegroundColor Green }
function Write-Warn { param($msg) Write-Host $msg -ForegroundColor Yellow }
function Write-Err { param($msg) Write-Host $msg -ForegroundColor Red }

# Detect docker compose command
function Get-DockerCompose {
    try {
        docker-compose version | Out-Null
        return "docker-compose"
    } catch {
        return "docker compose"
    }
}

$DC = Get-DockerCompose

Write-Info @"

╔══════════════════════════════════════════════════════════════╗
║           OpenTelemetry Tracing Demo                         ║
║    PostgreSQL + Azure Service Bus Emulator + Grafana Cloud   ║
║                                                              ║
║  Demonstrates trace context propagation via:                 ║
║  • Service Bus (automatic via Azure SDK)                     ║
║  • PostgreSQL (manual W3C traceparent storage)               ║
╚══════════════════════════════════════════════════════════════╝

"@

# Change to project root
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir
Set-Location $ProjectRoot
Write-Warn "Working directory: $ProjectRoot"

# Cleanup function
function Stop-Demo {
    Write-Host ""
    Write-Warn "Stopping containers..."
    & $DC.Split()[0] $DC.Split()[1..($DC.Split().Length-1)] --profile loadtest down 2>$null
}

# Register cleanup on Ctrl+C
$null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action { Stop-Demo }

try {
    # Step 1: Stop existing
    Write-Warn "Stopping any existing containers..."
    if ($DC -eq "docker-compose") {
        docker-compose --profile loadtest down 2>$null
    } else {
        docker compose --profile loadtest down 2>$null
    }

    # Step 2: Build
    Write-Warn "Building services..."
    if ($DC -eq "docker-compose") {
        docker-compose build
    } else {
        docker compose build
    }

    # Step 3: Start infrastructure
    Write-Warn "Starting infrastructure..."
    if ($DC -eq "docker-compose") {
        docker-compose up -d otel-collector postgres-db sql-server
    } else {
        docker compose up -d otel-collector postgres-db sql-server
    }

    Write-Host "Waiting for PostgreSQL..." -NoNewline
    $retries = 0
    $maxRetries = 30
    while ($retries -lt $maxRetries) {
        $result = docker exec postgres-db pg_isready -U demo -d trades 2>$null
        if ($LASTEXITCODE -eq 0) {
            break
        }
        Start-Sleep -Seconds 2
        Write-Host "." -NoNewline
        $retries++
    }
    if ($retries -ge $maxRetries) {
        Write-Err " PostgreSQL failed to start!"
        exit 1
    }
    Write-Success " PostgreSQL ready!"

    Write-Host "Waiting for SQL Server (30s)..."
    Start-Sleep -Seconds 30

    Write-Warn "Starting Service Bus Emulator..."
    if ($DC -eq "docker-compose") {
        docker-compose up -d servicebus-emulator
    } else {
        docker compose up -d servicebus-emulator
    }
    Write-Host "Waiting for Service Bus Emulator (25s)..."
    Start-Sleep -Seconds 25

    # Step 4: Start services
    Write-Warn "Starting Trade Service and Settlement Service..."
    if ($DC -eq "docker-compose") {
        docker-compose up -d trade-service settlement-service
    } else {
        docker compose up -d trade-service settlement-service
    }

    Write-Host "Waiting 15 seconds for services to start..."
    Start-Sleep -Seconds 15

    # Step 5: Check health
    Write-Warn "Checking services..."
    try {
        $response = Invoke-RestMethod -Uri "http://localhost:5001/health" -Method Get -TimeoutSec 5
        Write-Success "Trade Service OK"
    } catch {
        Write-Err "Trade Service FAILED"
    }

    try {
        $response = Invoke-RestMethod -Uri "http://localhost:5002/health" -Method Get -TimeoutSec 5
        Write-Success "Settlement Service OK"
    } catch {
        Write-Err "Settlement Service FAILED"
    }

    Write-Host ""
    Write-Success "════════════════════════════════════════════════════════════════"
    Write-Success "Services running:"
    Write-Host "  Trade Service:      http://localhost:5001"
    Write-Host "  Settlement Service: http://localhost:5002"
    Write-Host "  Swagger UI:         http://localhost:5001/swagger"
    Write-Host ""
    Write-Success "Try creating a trade (PowerShell):"
    Write-Host @"
  Invoke-RestMethod -Uri "http://localhost:5001/api/trades" ``
    -Method Post -ContentType "application/json" ``
    -Body '{"instrument":"CRUDE-OIL","quantity":1000,"price":75.50,"counterparty":"ACME"}'
"@
    Write-Host ""
    Write-Success "Or with curl:"
    Write-Host @"
  curl -X POST http://localhost:5001/api/trades -H "Content-Type: application/json" -d "{\"instrument\":\"CRUDE-OIL\",\"quantity\":1000,\"price\":75.50,\"counterparty\":\"ACME\"}"
"@
    Write-Success "════════════════════════════════════════════════════════════════"
    Write-Host ""

    # Step 6: Run load test
    Write-Warn "Running k6 load test (3 minutes)..."
    if ($DC -eq "docker-compose") {
        docker-compose --profile loadtest up k6-load-test
    } else {
        docker compose --profile loadtest up k6-load-test
    }

    Write-Host ""
    Write-Success "Load test complete!"
    Write-Host ""
    Write-Warn "Press Ctrl+C to stop all services."

    # Keep running
    while ($true) {
        Start-Sleep -Seconds 1
    }
}
finally {
    Stop-Demo
}

