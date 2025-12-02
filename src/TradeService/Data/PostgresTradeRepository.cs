using System.Diagnostics;
using Npgsql;
using OpenTelemetry.Trace;
using Shared.Models;
using TradeService.Telemetry;

namespace TradeService.Data;

/// <summary>
/// Repository for PostgreSQL operations with automatic Npgsql OpenTelemetry instrumentation.
/// Stores W3C Trace Context (traceparent) in the database to enable trace reconstruction
/// when trades are read by other services.
/// </summary>
public interface ITradeRepository
{
    Task<Trade?> GetByIdAsync(string tradeId);
    Task<IEnumerable<Trade>> GetAllAsync();
    Task<Trade> CreateAsync(Trade trade);
    Task<Trade> UpdateAsync(Trade trade);
}

public class PostgresTradeRepository : ITradeRepository
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresTradeRepository> _logger;

    public PostgresTradeRepository(IConfiguration configuration, ILogger<PostgresTradeRepository> logger)
    {
        _connectionString = configuration["POSTGRES_CONNECTION_STRING"] 
            ?? "Host=localhost;Database=trades;Username=demo;Password=demo123";
        _logger = logger;
    }

    public async Task<Trade?> GetByIdAsync(string tradeId)
    {
        // Npgsql automatically creates spans via its built-in Activity support
        // We add a wrapper span for additional context
        using var activity = TelemetryExtensions.ActivitySource.StartActivity(
            name: "TradeRepository.GetById",
            kind: ActivityKind.Internal);
        
        activity?.SetTag("trade.id", tradeId);

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                SELECT trade_id, instrument, quantity, price, counterparty, 
                       trade_date, status, created_at, settled_at,
                       trace_parent, trace_state
                FROM trades 
                WHERE trade_id = @tradeId";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("tradeId", tradeId);

            await using var reader = await command.ExecuteReaderAsync();
            
            if (await reader.ReadAsync())
            {
                var trade = MapToTrade(reader);
                activity?.SetTag("db.rows_affected", 1);
                _logger.LogInformation("Retrieved trade {TradeId} from PostgreSQL", tradeId);
                return trade;
            }

            activity?.SetTag("db.rows_affected", 0);
            return null;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            _logger.LogError(ex, "Error retrieving trade {TradeId} from PostgreSQL", tradeId);
            throw;
        }
    }

    public async Task<IEnumerable<Trade>> GetAllAsync()
    {
        using var activity = TelemetryExtensions.ActivitySource.StartActivity(
            name: "TradeRepository.GetAll",
            kind: ActivityKind.Internal);

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                SELECT trade_id, instrument, quantity, price, counterparty, 
                       trade_date, status, created_at, settled_at,
                       trace_parent, trace_state
                FROM trades 
                ORDER BY created_at DESC";

            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            var trades = new List<Trade>();
            while (await reader.ReadAsync())
            {
                trades.Add(MapToTrade(reader));
            }

            activity?.SetTag("db.rows_affected", trades.Count);
            _logger.LogInformation("Retrieved {Count} trades from PostgreSQL", trades.Count);
            return trades;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            _logger.LogError(ex, "Error retrieving trades from PostgreSQL");
            throw;
        }
    }

    public async Task<Trade> CreateAsync(Trade trade)
    {
        using var activity = TelemetryExtensions.ActivitySource.StartActivity(
            name: "TradeRepository.Create",
            kind: ActivityKind.Internal);

        activity?.SetTag("trade.id", trade.TradeId);
        activity?.SetTag("trade.instrument", trade.Instrument);

        try
        {
            // Capture current trace context to persist with the trade
            // This enables trace reconstruction when the trade is read by another service
            var currentActivity = Activity.Current;
            if (currentActivity != null)
            {
                // Format: "00-{trace-id}-{span-id}-{trace-flags}"
                trade.TraceParent = $"00-{currentActivity.TraceId}-{currentActivity.SpanId}-{(currentActivity.Recorded ? "01" : "00")}";
                trade.TraceState = currentActivity.TraceStateString;
                
                activity?.SetTag("trace.propagation.traceparent", trade.TraceParent);
                _logger.LogInformation(
                    "Storing trace context with trade {TradeId}: traceparent={TraceParent}", 
                    trade.TradeId, trade.TraceParent);
            }

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                INSERT INTO trades (trade_id, instrument, quantity, price, counterparty, 
                                   trade_date, status, created_at, settled_at,
                                   trace_parent, trace_state)
                VALUES (@tradeId, @instrument, @quantity, @price, @counterparty, 
                        @tradeDate, @status, @createdAt, @settledAt,
                        @traceParent, @traceState)";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("tradeId", trade.TradeId);
            command.Parameters.AddWithValue("instrument", trade.Instrument);
            command.Parameters.AddWithValue("quantity", trade.Quantity);
            command.Parameters.AddWithValue("price", trade.Price);
            command.Parameters.AddWithValue("counterparty", trade.Counterparty);
            command.Parameters.AddWithValue("tradeDate", trade.TradeDate);
            command.Parameters.AddWithValue("status", trade.Status.ToString());
            command.Parameters.AddWithValue("createdAt", trade.CreatedAt);
            command.Parameters.AddWithValue("settledAt", trade.SettledAt ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("traceParent", trade.TraceParent ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("traceState", trade.TraceState ?? (object)DBNull.Value);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            activity?.SetTag("db.rows_affected", rowsAffected);

            _logger.LogInformation("Created trade {TradeId} in PostgreSQL", trade.TradeId);
            return trade;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            _logger.LogError(ex, "Error creating trade {TradeId} in PostgreSQL", trade.TradeId);
            throw;
        }
    }

    public async Task<Trade> UpdateAsync(Trade trade)
    {
        using var activity = TelemetryExtensions.ActivitySource.StartActivity(
            name: "TradeRepository.Update",
            kind: ActivityKind.Internal);

        activity?.SetTag("trade.id", trade.TradeId);
        activity?.SetTag("trade.status", trade.Status.ToString());

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                UPDATE trades 
                SET status = @status, settled_at = @settledAt
                WHERE trade_id = @tradeId";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("status", trade.Status.ToString());
            command.Parameters.AddWithValue("settledAt", trade.SettledAt ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("tradeId", trade.TradeId);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            activity?.SetTag("db.rows_affected", rowsAffected);

            _logger.LogInformation("Updated trade {TradeId} status to {Status}", trade.TradeId, trade.Status);
            return trade;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            _logger.LogError(ex, "Error updating trade {TradeId} in PostgreSQL", trade.TradeId);
            throw;
        }
    }

    private static Trade MapToTrade(NpgsqlDataReader reader)
    {
        return new Trade
        {
            TradeId = reader.GetString(0),
            Instrument = reader.GetString(1),
            Quantity = reader.GetDecimal(2),
            Price = reader.GetDecimal(3),
            Counterparty = reader.GetString(4),
            TradeDate = reader.GetDateTime(5),
            Status = Enum.Parse<TradeStatus>(reader.GetString(6)),
            CreatedAt = reader.GetDateTime(7),
            SettledAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            TraceParent = reader.IsDBNull(9) ? null : reader.GetString(9),
            TraceState = reader.IsDBNull(10) ? null : reader.GetString(10)
        };
    }
}

