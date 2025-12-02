#!/bin/bash

#######################################################################
# Tracing Demo Runner Script
# Demonstrates W3C Trace Context propagation via:
# 1. Azure Service Bus (automatic)
# 2. PostgreSQL database (manual - storing traceparent)
#######################################################################

set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

# Detect docker compose command
if command -v docker-compose &> /dev/null; then
    DC="docker-compose"
else
    DC="docker compose"
fi

echo -e "${BLUE}"
echo "╔══════════════════════════════════════════════════════════════╗"
echo "║           OpenTelemetry Tracing Demo                         ║"
echo "║    PostgreSQL + Azure Service Bus Emulator + Grafana Cloud   ║"
echo "║                                                              ║"
echo "║  Demonstrates trace context propagation via:                 ║"
echo "║  • Service Bus (automatic via Azure SDK)                     ║"
echo "║  • PostgreSQL (manual W3C traceparent storage)               ║"
echo "╚══════════════════════════════════════════════════════════════╝"
echo -e "${NC}"

# Change to project root
cd "$(dirname "${BASH_SOURCE[0]}")/.."
echo -e "${YELLOW}Working directory: $(pwd)${NC}"

# Cleanup function
cleanup() {
    echo ""
    echo -e "${YELLOW}Stopping containers...${NC}"
    $DC --profile loadtest down
}
trap cleanup EXIT

# Step 1: Stop existing
echo -e "${YELLOW}Stopping any existing containers...${NC}"
$DC --profile loadtest down 2>/dev/null || true

# Step 2: Build
echo -e "${YELLOW}Building services...${NC}"
$DC build

# Step 3: Start infrastructure
echo -e "${YELLOW}Starting infrastructure...${NC}"
$DC up -d otel-collector postgres-db sql-server

echo "Waiting for PostgreSQL..."
until docker exec postgres-db pg_isready -U demo -d trades &>/dev/null; do
    sleep 2
    echo -n "."
done
echo -e " ${GREEN}PostgreSQL ready!${NC}"

echo "Waiting for SQL Server (30s)..."
sleep 30

echo -e "${YELLOW}Starting Service Bus Emulator...${NC}"
$DC up -d servicebus-emulator
echo "Waiting for Service Bus Emulator (25s)..."
sleep 25

# Step 4: Start services
echo -e "${YELLOW}Starting Trade Service and Settlement Service...${NC}"
$DC up -d trade-service settlement-service

echo "Waiting 15 seconds for services to start..."
sleep 15

# Step 5: Check health
echo -e "${YELLOW}Checking services...${NC}"
curl -s http://localhost:5001/health && echo -e " ${GREEN}Trade Service OK${NC}" || echo -e " ${RED}Trade Service FAILED${NC}"
curl -s http://localhost:5002/health && echo -e " ${GREEN}Settlement Service OK${NC}" || echo -e " ${RED}Settlement Service FAILED${NC}"

echo ""
echo -e "${GREEN}════════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}Services running:${NC}"
echo "  Trade Service:      http://localhost:5001"
echo "  Settlement Service: http://localhost:5002"
echo "  Swagger UI:         http://localhost:5001/swagger"
echo ""
echo -e "${GREEN}Try creating a trade:${NC}"
echo '  curl -X POST http://localhost:5001/api/trades \'
echo '    -H "Content-Type: application/json" \'
echo '    -d '"'"'{"instrument":"CRUDE-OIL","quantity":1000,"price":75.50,"counterparty":"ACME"}'"'"
echo -e "${GREEN}════════════════════════════════════════════════════════════════${NC}"
echo ""

# Step 6: Run load test
echo -e "${YELLOW}Running k6 load test (3 minutes)...${NC}"
$DC --profile loadtest up k6-load-test

echo ""
echo -e "${GREEN}Load test complete!${NC}"
echo ""
echo -e "${YELLOW}Press Ctrl+C to stop all services.${NC}"

# Keep running
while true; do sleep 1; done
