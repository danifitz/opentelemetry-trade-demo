using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Shared.Models;

namespace TradeService.Messaging;

/// <summary>
/// Publisher for Azure Service Bus messages.
/// Azure.Messaging.ServiceBus has built-in Activity/DiagnosticSource support,
/// so OpenTelemetry automatically picks up the traces without manual instrumentation.
/// Supports both Azure Service Bus and the local Service Bus Emulator.
/// </summary>
public interface IMessagePublisher
{
    Task PublishTradeSubmittedAsync(Trade trade);
    Task PublishTradeSettledAsync(Trade trade);
    Task PublishTradeFailedAsync(Trade trade, string reason);
}

public class ServiceBusPublisher : IMessagePublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusPublisher> _logger;
    private readonly string _queueName;

    public ServiceBusPublisher(IConfiguration configuration, ILogger<ServiceBusPublisher> logger)
    {
        _logger = logger;
        
        var useEmulator = configuration.GetValue<bool>("USE_SERVICE_BUS_EMULATOR", false);
        _queueName = configuration["AZURE_SERVICE_BUS_QUEUE_NAME"] ?? "trade-messages";

        string connectionString;
        
        if (useEmulator)
        {
            // Use the Service Bus Emulator connection string
            // Services share network namespace with emulator, so localhost works
            connectionString = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
            _logger.LogInformation("Using Service Bus Emulator");
        }
        else
        {
            // Use the real Azure Service Bus connection string
            connectionString = configuration["AZURE_SERVICE_BUS_CONNECTION_STRING"]
                ?? throw new InvalidOperationException("AZURE_SERVICE_BUS_CONNECTION_STRING is required when not using emulator");
            _logger.LogInformation("Using Azure Service Bus");
        }
        
        // ServiceBusClient automatically creates Activity spans for tracing
        _client = new ServiceBusClient(connectionString);
        _sender = _client.CreateSender(_queueName);
    }

    public async Task PublishTradeSubmittedAsync(Trade trade)
    {
        var message = new TradeMessage
        {
            TradeId = trade.TradeId,
            MessageType = MessageTypes.TradeSubmitted,
            Timestamp = DateTime.UtcNow,
            Properties = new Dictionary<string, string>
            {
                ["instrument"] = trade.Instrument,
                ["counterparty"] = trade.Counterparty,
                ["quantity"] = trade.Quantity.ToString(),
                ["price"] = trade.Price.ToString()
            }
        };

        await SendMessageAsync(message);
        _logger.LogInformation("Published TradeSubmitted message for trade {TradeId}", trade.TradeId);
    }

    public async Task PublishTradeSettledAsync(Trade trade)
    {
        var message = new TradeMessage
        {
            TradeId = trade.TradeId,
            MessageType = MessageTypes.TradeSettled,
            Timestamp = DateTime.UtcNow,
            Properties = new Dictionary<string, string>
            {
                ["settledAt"] = trade.SettledAt?.ToString("O") ?? DateTime.UtcNow.ToString("O")
            }
        };

        await SendMessageAsync(message);
        _logger.LogInformation("Published TradeSettled message for trade {TradeId}", trade.TradeId);
    }

    public async Task PublishTradeFailedAsync(Trade trade, string reason)
    {
        var message = new TradeMessage
        {
            TradeId = trade.TradeId,
            MessageType = MessageTypes.TradeFailed,
            Timestamp = DateTime.UtcNow,
            Properties = new Dictionary<string, string>
            {
                ["reason"] = reason
            }
        };

        await SendMessageAsync(message);
        _logger.LogWarning("Published TradeFailed message for trade {TradeId}: {Reason}", trade.TradeId, reason);
    }

    private async Task SendMessageAsync(TradeMessage tradeMessage)
    {
        // Serialize the message
        var messageBody = JsonSerializer.Serialize(tradeMessage);
        
        // Create Service Bus message
        // Azure SDK automatically propagates trace context through message properties
        var serviceBusMessage = new ServiceBusMessage(messageBody)
        {
            ContentType = "application/json",
            Subject = tradeMessage.MessageType,
            MessageId = $"{tradeMessage.TradeId}-{tradeMessage.MessageType}-{Guid.NewGuid():N}",
            ApplicationProperties =
            {
                ["TradeId"] = tradeMessage.TradeId,
                ["MessageType"] = tradeMessage.MessageType
            }
        };

        // Send the message - Activity span is automatically created
        await _sender.SendMessageAsync(serviceBusMessage);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
