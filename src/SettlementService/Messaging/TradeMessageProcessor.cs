using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using OpenTelemetry.Trace;
using Shared.Models;
using SettlementService.Data;
using SettlementService.Telemetry;

namespace SettlementService.Messaging;

/// <summary>
/// Background service that processes messages from Azure Service Bus.
/// Azure.Messaging.ServiceBus automatically propagates trace context, so spans are linked.
/// 
/// Additionally, this processor demonstrates trace context reconstruction from the database:
/// - TradeService stores the W3C traceparent when creating trades
/// - SettlementService reads the trade and reconstructs the trace context
/// - This creates a linked span that connects the settlement to the original trade creation
/// </summary>
public class TradeMessageProcessor : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusProcessor _processor;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TradeMessageProcessor> _logger;
    private readonly string _queueName;
    private readonly bool _useEmulator;

    public TradeMessageProcessor(
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        ILogger<TradeMessageProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        _useEmulator = configuration.GetValue<bool>("USE_SERVICE_BUS_EMULATOR", false);
        _queueName = configuration["AZURE_SERVICE_BUS_QUEUE_NAME"] ?? "trade-messages";

        string connectionString;

        if (_useEmulator)
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

        // ServiceBusClient automatically handles trace context propagation
        _client = new ServiceBusClient(connectionString);
        
        _processor = _client.CreateProcessor(_queueName, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1,
            AutoCompleteMessages = false
        });

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Starting Service Bus message processor for queue: {QueueName} (Emulator: {UseEmulator})", 
            _queueName, 
            _useEmulator);
        
        await _processor.StartProcessingAsync(stoppingToken);

        // Keep the service running
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        // Azure SDK automatically creates a span linked to the sender's trace context
        using var activity = TelemetryExtensions.ActivitySource.StartActivity(
            name: "settlement.process_trade",
            kind: ActivityKind.Consumer);

        var messageBody = args.Message.Body.ToString();
        
        activity?.SetTag("messaging.system", "azure_service_bus");
        activity?.SetTag("messaging.destination", _queueName);
        activity?.SetTag("messaging.message_id", args.Message.MessageId);

        try
        {
            var tradeMessage = JsonSerializer.Deserialize<TradeMessage>(messageBody);
            
            if (tradeMessage == null)
            {
                _logger.LogWarning("Received null or invalid message: {MessageId}", args.Message.MessageId);
                await args.DeadLetterMessageAsync(args.Message, "Invalid message format");
                return;
            }

            activity?.SetTag("trade.id", tradeMessage.TradeId);
            activity?.SetTag("trade.message_type", tradeMessage.MessageType);

            _logger.LogInformation(
                "Processing message {MessageType} for trade {TradeId}", 
                tradeMessage.MessageType, 
                tradeMessage.TradeId);

            await HandleMessageAsync(tradeMessage, activity);

            // Complete the message
            await args.CompleteMessageAsync(args.Message);
            
            _logger.LogInformation("Successfully processed trade {TradeId}", tradeMessage.TradeId);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            
            _logger.LogError(ex, "Error processing message {MessageId}", args.Message.MessageId);
            
            // Abandon the message so it can be reprocessed
            await args.AbandonMessageAsync(args.Message);
        }
    }

    private async Task HandleMessageAsync(TradeMessage message, Activity? parentActivity)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITradeRepository>();

        switch (message.MessageType)
        {
            case MessageTypes.TradeSubmitted:
                await HandleTradeSubmittedAsync(message, repository, parentActivity);
                break;
            
            default:
                _logger.LogWarning("Unknown message type: {MessageType}", message.MessageType);
                break;
        }
    }

    private async Task HandleTradeSubmittedAsync(
        TradeMessage message, 
        ITradeRepository repository,
        Activity? parentActivity)
    {
        try
        {
            // First, get the trade from the database along with its stored trace context
            var (trade, originalTraceContext) = await repository.GetByIdWithTraceContextAsync(message.TradeId);
            
            if (trade == null)
            {
                _logger.LogWarning("Trade {TradeId} not found in database", message.TradeId);
                return;
            }

            // Create a settlement span and link to the original trace context from the database
            // This demonstrates trace context propagation via the database!
            // The span will have:
            // 1. Parent: Current span (from Service Bus message processing)
            // 2. Link: Original span from TradeService that created the trade
            var links = originalTraceContext.HasValue
                ? new[] { new ActivityLink(originalTraceContext.Value) }
                : null;
            
            if (originalTraceContext.HasValue)
            {
                _logger.LogInformation(
                    "Creating settlement span linked to original trade creation. " +
                    "Original TraceId: {TraceId}, SpanId: {SpanId}",
                    originalTraceContext.Value.TraceId,
                    originalTraceContext.Value.SpanId);
            }

            using var activity = TelemetryExtensions.ActivitySource.StartActivity(
                "settlement.settle_trade",
                ActivityKind.Internal,
                default(ActivityContext),
                tags: new[]
                {
                    new KeyValuePair<string, object?>("trade.id", message.TradeId),
                    new KeyValuePair<string, object?>("trade.instrument", trade.Instrument),
                    new KeyValuePair<string, object?>("trade.counterparty", trade.Counterparty),
                    new KeyValuePair<string, object?>("trace.db_propagation", originalTraceContext.HasValue)
                },
                links: links);

            // Simulate settlement processing
            _logger.LogInformation("Settling trade {TradeId} for {Instrument}", message.TradeId, trade.Instrument);
            
            // Add some processing time to make traces interesting
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(100, 500)));

            // Update trade status in PostgreSQL
            var settledTrade = await repository.UpdateStatusAsync(
                message.TradeId, 
                TradeStatus.Settled, 
                DateTime.UtcNow);

            activity?.SetTag("trade.settled_at", settledTrade.SettledAt?.ToString("O"));
            activity?.SetTag("settlement.success", true);

            _logger.LogInformation(
                "Trade {TradeId} settled successfully at {SettledAt}", 
                message.TradeId, 
                settledTrade.SettledAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to settle trade {TradeId}", message.TradeId);
            throw;
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(
            args.Exception,
            "Error processing Service Bus message. Source: {Source}, Namespace: {Namespace}",
            args.ErrorSource,
            args.FullyQualifiedNamespace);
        
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Service Bus message processor");
        
        await _processor.StopProcessingAsync(cancellationToken);
        await _processor.DisposeAsync();
        await _client.DisposeAsync();
        
        await base.StopAsync(cancellationToken);
    }
}
