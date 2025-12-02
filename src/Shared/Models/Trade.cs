namespace Shared.Models;

/// <summary>
/// Represents a trade entity stored in PostgreSQL
/// Includes W3C Trace Context fields for cross-service trace propagation via database
/// </summary>
public class Trade
{
    public string TradeId { get; set; } = string.Empty;
    public string Instrument { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public string Counterparty { get; set; } = string.Empty;
    public DateTime TradeDate { get; set; }
    public TradeStatus Status { get; set; } = TradeStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SettledAt { get; set; }
    
    // W3C Trace Context - persisted to enable trace reconstruction across services
    // Format: "00-{trace-id}-{span-id}-{trace-flags}"
    public string? TraceParent { get; set; }
    
    // Optional W3C Trace State for vendor-specific trace information
    public string? TraceState { get; set; }
}

public enum TradeStatus
{
    Pending,
    Submitted,
    Settled,
    Failed
}
