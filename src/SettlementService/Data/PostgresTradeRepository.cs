using System.Diagnostics;
using Npgsql;
using OpenTelemetry.Trace;
using Shared.Models;
using SettlementService.Telemetry;

namespace SettlementService.Data;

/// <summary>
/// Repository for PostgreSQL operations with trace context reconstruction.
/// When reading trades, extracts the stored W3C Trace Context (traceparent)
/// and creates linked spans to maintain trace continuity across services.
/// </summary>
public interface ITradeRepository
{
    Task<Trade?> GetByIdAsync(string tradeId);
    
    /// <summary>
    /// Gets a trade by ID and reconstructs the trace context from stored traceparent.
    /// Returns both the trade and an ActivityContext that can be used as a parent.
    /// </summary>
    Task<(Trade? Trade, ActivityContext? OriginalContext)> GetByIdWithTraceContextAsync(string tradeId);
    
    Task<Trade> UpdateStatusAsync(string tradeId, TradeStatus status, DateTime? settledAt = null);
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
        var (trade, _) = await GetByIdWithTraceContextAsync(tradeId);
        return trade;
    }

    public async Task<(Trade? Trade, ActivityContext? OriginalContext)> GetByIdWithTraceContextAsync(string tradeId)
    {
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
                
                // Extract and parse the stored trace context
                ActivityContext? originalContext = null;
                if (!string.IsNullOrEmpty(trade.TraceParent))
                {
                    originalContext = ParseTraceParent(trade.TraceParent, trade.TraceState);
                    if (originalContext.HasValue)
                    {
                        activity?.SetTag("trace.propagation.reconstructed", true);
                        activity?.SetTag("trace.propagation.original_trace_id", originalContext.Value.TraceId.ToString());
                        activity?.SetTag("trace.propagation.original_span_id", originalContext.Value.SpanId.ToString());
                        
                        _logger.LogInformation(
                            "Reconstructed trace context for trade {TradeId}: TraceId={TraceId}, SpanId={SpanId}", 
                            tradeId, 
                            originalContext.Value.TraceId, 
                            originalContext.Value.SpanId);
                    }
                }
                
                _logger.LogInformation("Retrieved trade {TradeId} from PostgreSQL", tradeId);
                return (trade, originalContext);
            }

            activity?.SetTag("db.rows_affected", 0);
            return (null, null);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            _logger.LogError(ex, "Error retrieving trade {TradeId} from PostgreSQL", tradeId);
            throw;
        }
    }

    public async Task<Trade> UpdateStatusAsync(string tradeId, TradeStatus status, DateTime? settledAt = null)
    {
        using var activity = TelemetryExtensions.ActivitySource.StartActivity(
            name: "TradeRepository.UpdateStatus",
            kind: ActivityKind.Internal);

        activity?.SetTag("trade.id", tradeId);
        activity?.SetTag("trade.new_status", status.ToString());

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                UPDATE trades 
                SET status = @status, settled_at = @settledAt
                WHERE trade_id = @tradeId";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("status", status.ToString());
            command.Parameters.AddWithValue("settledAt", settledAt ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("tradeId", tradeId);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            activity?.SetTag("db.rows_affected", rowsAffected);

            _logger.LogInformation("Updated trade {TradeId} status to {Status}", tradeId, status);

            // Retrieve and return the updated trade
            var trade = await GetByIdAsync(tradeId);
            return trade ?? throw new InvalidOperationException($"Trade {tradeId} not found after update");
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            _logger.LogError(ex, "Error updating trade {TradeId} in PostgreSQL", tradeId);
            throw;
        }
    }

    /// <summary>
    /// Parses a W3C traceparent header into an ActivityContext using the built-in TryParse.
    /// Format: "{version}-{trace-id}-{parent-id}-{trace-flags}"
    /// Example: "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
    /// </summary>
    private ActivityContext? ParseTraceParent(string traceParent, string? traceState)
    {
        if (ActivityContext.TryParse(traceParent, traceState, out var context))
        {
            return context;
        }
        
        _logger.LogWarning("Failed to parse traceparent: {TraceParent}", traceParent);
        return null;
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

