# OpenTelemetry Tracing Demo

A .NET 8 demo demonstrating distributed tracing with OpenTelemetry across:
- **PostgreSQL database** - with manual W3C Trace Context propagation
- **Azure Service Bus** - with automatic trace context propagation

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           Grafana Cloud                                  │
│                    (Traces, Metrics, Logs)                              │
└─────────────────────────────────────────────────────────────────────────┘
                                    ▲
                                    │ OTLP
                                    │
┌─────────────────────────────────────────────────────────────────────────┐
│                      OpenTelemetry Collector                            │
│                         (otel-collector)                                │
└─────────────────────────────────────────────────────────────────────────┘
                                    ▲
                    ┌───────────────┴───────────────┐
                    │                               │
           ┌────────┴────────┐             ┌───────┴────────┐
           │  TradeService   │             │SettlementService│
           │   (Port 5001)   │             │   (Port 5002)   │
           └───┬─────────┬───┘             └───┬─────────┬───┘
               │         │                     │         │
               │         │                     │         │
     1. Store  │         │ 2. Send message     │         │ 4. Read trade
     trade w/  │         │    (auto trace      │         │    w/ traceparent
     traceparent         │     propagation)    │         │
               │         │                     │         │
               ▼         └─────────┐   ┌───────┘         ▼
           ┌──────────────┐        │   │        ┌──────────────┐
           │  PostgreSQL  │        │   │        │  PostgreSQL  │
           │  (write)     │        │   │        │  (read)      │
           └──────────────┘        │   │        └──────────────┘
                                   ▼   │
                    ┌──────────────────┴───────────────────┐
                    │          Azure Service Bus           │
                    │       (or Service Bus Emulator)      │
                    │                                      │
                    │  3. Receive message (auto trace)     │
                    └──────────────────────────────────────┘
```

## Trace Context Propagation

This demo shows two methods of trace context propagation:

### 1. Service Bus (Automatic)
The Azure.Messaging.ServiceBus SDK automatically propagates trace context through message headers. When a message is sent, the SDK includes a `Diagnostic-Id` header with the W3C traceparent.

> **Note:** The Service Bus Emulator may not fully support trace context propagation. In production with real Azure Service Bus, traces are automatically connected.

### 2. PostgreSQL Database (Manual)
We manually store the W3C trace context in the database:

```sql
CREATE TABLE trades (
    trade_id VARCHAR(50) PRIMARY KEY,
    -- ... other columns ...
    trace_parent VARCHAR(100),  -- "00-{trace-id}-{span-id}-{flags}"
    trace_state VARCHAR(500)
);
```

**When creating a trade (TradeService):**
```csharp
var currentActivity = Activity.Current;
if (currentActivity != null)
{
    trade.TraceParent = $"00-{currentActivity.TraceId}-{currentActivity.SpanId}-01";
}
```

**When processing a trade (SettlementService):**
```csharp
// Read the stored trace context
var (trade, originalContext) = await repository.GetByIdWithTraceContextAsync(tradeId);

// Create a span linked to the original trace
ActivityLink[] links = originalContext.HasValue 
    ? new[] { new ActivityLink(originalContext.Value) }
    : null;

using var activity = ActivitySource.StartActivity(
    "settlement.settle_trade",
    ActivityKind.Internal,
    default,
    tags,
    links);  // Creates a LINK to the original trace
```

## Project Structure

```
tracing-demo/
├── src/
│   ├── Shared/                    # Shared models
│   │   └── Models/
│   │       ├── Trade.cs           # Trade entity with TraceParent/TraceState
│   │       └── TradeMessage.cs    # Service Bus message
│   ├── TradeService/              # Trade creation API
│   │   ├── Controllers/
│   │   ├── Data/
│   │   │   └── PostgresTradeRepository.cs
│   │   ├── Messaging/
│   │   │   └── ServiceBusPublisher.cs
│   │   └── Telemetry/
│   │       └── TelemetryExtensions.cs
│   └── SettlementService/         # Trade settlement processor
│       ├── Controllers/
│       ├── Data/
│       │   └── PostgresTradeRepository.cs
│       ├── Messaging/
│       │   └── TradeMessageProcessor.cs
│       └── Telemetry/
│           └── TelemetryExtensions.cs
├── config/
│   └── servicebus-config.json     # Service Bus Emulator config
├── scripts/
│   ├── postgres-init.sql          # Database initialization
│   ├── run-demo.sh                # Full demo runner
│   └── provision-azure.sh         # Azure resource provisioning
├── tests/
│   └── load-test.js               # k6 load test
├── docker-compose.yml
├── otel-collector-config.yaml
└── .env                           # Environment configuration
```

## Quick Start

### Prerequisites
- Docker and Docker Compose
- .NET 8 SDK (for local development)
- Grafana Cloud account (free tier works)

### 1. Configure Grafana Cloud

Get your OTLP credentials from Grafana Cloud:
1. Go to **Connections** → **OpenTelemetry (OTLP)**
2. Copy the endpoint, instance ID, and API key

Create a `.env` file:

```bash
# Grafana Cloud
GRAFANA_CLOUD_OTLP_ENDPOINT=https://otlp-gateway-prod-xx-xxxx-x.grafana.net/otlp
GRAFANA_CLOUD_INSTANCE_ID=your-instance-id
GRAFANA_CLOUD_API_KEY=your-api-key

# Service Bus Emulator
USE_SERVICE_BUS_EMULATOR=true
```

### 2. Run the Demo

```bash
./scripts/run-demo.sh
```

This will:
1. Start PostgreSQL, SQL Server, and Service Bus Emulator
2. Start the OpenTelemetry Collector
3. Start TradeService and SettlementService
4. Run a 3-minute k6 load test

### 3. Manual Testing

Create a trade:
```bash
curl -X POST http://localhost:5001/api/trades \
  -H "Content-Type: application/json" \
  -d '{
    "instrument": "CRUDE-OIL-JAN25",
    "quantity": 1000,
    "price": 75.50,
    "counterparty": "ACME Corp"
  }'
```

Check trade status:
```bash
curl http://localhost:5001/api/trades/{tradeId}
```

Check settlement status:
```bash
curl http://localhost:5002/api/settlement/{tradeId}/status
```

## Viewing Traces in Grafana

### Finding Connected Traces

1. **Go to Grafana → Explore → Tempo**

2. **Search by service:**
   - `service.name = "TradeService"` - See trade creation traces
   - `service.name = "SettlementService"` - See settlement traces

3. **Look for linked traces:**
   - Open a SettlementService trace
   - Find the `settlement.settle_trade` span
   - Look for the **Links** section - it shows the connection to the original TradeService trace

### Understanding the Trace Structure

**Flow:**
1. **TradeService** receives HTTP POST, stores trade in PostgreSQL (with traceparent), sends message to Service Bus
2. **Service Bus** propagates trace context automatically via message headers
3. **SettlementService** receives message, reads trade from PostgreSQL, links to original trace

**TradeService trace:**
```
POST /api/trades
├── TradeRepository.Create (stores traceparent in DB)
├── Npgsql: INSERT INTO trades...
└── ServiceBusSender.Send (trace context auto-propagated)
```

**SettlementService trace (same trace ID via Service Bus):**
```
ServiceBusProcessor.ProcessMessage
├── settlement.process_trade
├── TradeRepository.GetById (reads traceparent from DB)
├── settlement.settle_trade [LINKED to original span via DB traceparent]
└── TradeRepository.UpdateStatus
```

## OpenTelemetry Instrumentation

### Automatic Instrumentation
- **ASP.NET Core** - HTTP requests via `OpenTelemetry.Instrumentation.AspNetCore`
- **HttpClient** - Outbound HTTP via `OpenTelemetry.Instrumentation.Http`
- **Azure SDK** - Service Bus via built-in `Azure.*` ActivitySource
- **Npgsql** - PostgreSQL via built-in `Npgsql` ActivitySource

### Manual Instrumentation
- **Trade creation** - Stores W3C traceparent in database
- **Settlement processing** - Reconstructs trace context, creates linked spans

## Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `GRAFANA_CLOUD_OTLP_ENDPOINT` | Grafana Cloud OTLP endpoint | Required |
| `GRAFANA_CLOUD_INSTANCE_ID` | Grafana Cloud instance ID | Required |
| `GRAFANA_CLOUD_API_KEY` | Grafana Cloud API key | Required |
| `USE_SERVICE_BUS_EMULATOR` | Use local emulator | `true` |
| `AZURE_SERVICE_BUS_CONNECTION_STRING` | Azure Service Bus connection | Required if emulator=false |
| `POSTGRES_CONNECTION_STRING` | PostgreSQL connection string | Auto-configured in Docker |

### Docker Compose Profiles

```bash
# Start everything except load test
docker-compose up -d

# Run with load test
docker-compose --profile loadtest up
```

## Development

### Building Locally

```bash
dotnet restore
dotnet build
```

### Running Services Locally

You'll need PostgreSQL and Service Bus Emulator running:

```bash
# Start infrastructure only
docker-compose up -d postgres-db sql-server servicebus-emulator otel-collector

# Run services locally
cd src/TradeService && dotnet run
cd src/SettlementService && dotnet run
```

## Troubleshooting

### Traces not appearing in Grafana

1. **Check OTel Collector logs:**
   ```bash
   docker-compose logs otel-collector
   ```

2. **Verify environment variables:**
   ```bash
   docker inspect otel-collector | grep -A 10 '"Env"'
   ```

3. **Test connectivity:**
   - Traces should appear within 1-2 minutes
   - Check for authentication errors in collector logs

### Service Bus connection issues

The emulator requires `localhost` in the connection string. Services share the network namespace with the emulator container via `network_mode: "service:servicebus-emulator"`.

### Services can't connect to PostgreSQL

Services access PostgreSQL via `host.docker.internal`. Ensure your Docker setup supports this (works on Docker Desktop for Mac/Windows).

## Using Real Azure Service Bus

1. Create an Azure Service Bus namespace:
   ```bash
   ./scripts/provision-azure.sh
   ```

2. Update `.env`:
   ```bash
   USE_SERVICE_BUS_EMULATOR=false
   AZURE_SERVICE_BUS_CONNECTION_STRING=Endpoint=sb://...
   ```

3. Restart services:
   ```bash
   docker-compose up -d trade-service settlement-service
   ```

With real Azure Service Bus, trace context propagation works automatically, and you'll see fully connected traces without needing the database link.

## License

MIT
