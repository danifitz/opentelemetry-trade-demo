namespace Shared.Models;

/// <summary>
/// Message sent via Azure Service Bus for trade processing
/// </summary>
public class TradeMessage
{
    public string TradeId { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Properties { get; set; } = new();
}

public static class MessageTypes
{
    public const string TradeSubmitted = "TradeSubmitted";
    public const string TradeSettled = "TradeSettled";
    public const string TradeFailed = "TradeFailed";
}

