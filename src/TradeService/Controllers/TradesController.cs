using Microsoft.AspNetCore.Mvc;
using Shared.Models;
using TradeService.Data;
using TradeService.Messaging;

namespace TradeService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TradesController : ControllerBase
{
    private readonly ITradeRepository _repository;
    private readonly IMessagePublisher _messagePublisher;
    private readonly ILogger<TradesController> _logger;

    public TradesController(
        ITradeRepository repository,
        IMessagePublisher messagePublisher,
        ILogger<TradesController> logger)
    {
        _repository = repository;
        _messagePublisher = messagePublisher;
        _logger = logger;
    }

    /// <summary>
    /// Get all trades
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Trade>>> GetAll()
    {
        _logger.LogInformation("Retrieving all trades");
        var trades = await _repository.GetAllAsync();
        return Ok(trades);
    }

    /// <summary>
    /// Get a specific trade by ID
    /// </summary>
    [HttpGet("{tradeId}")]
    public async Task<ActionResult<Trade>> GetById(string tradeId)
    {
        _logger.LogInformation("Retrieving trade {TradeId}", tradeId);
        var trade = await _repository.GetByIdAsync(tradeId);
        
        if (trade == null)
        {
            _logger.LogWarning("Trade {TradeId} not found", tradeId);
            return NotFound(new { message = $"Trade {tradeId} not found" });
        }
        
        return Ok(trade);
    }

    /// <summary>
    /// Create a new trade and publish to Service Bus for processing
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Trade>> Create([FromBody] CreateTradeRequest request)
    {
        var trade = new Trade
        {
            TradeId = Guid.NewGuid().ToString("N"),
            Instrument = request.Instrument,
            Quantity = request.Quantity,
            Price = request.Price,
            Counterparty = request.Counterparty,
            TradeDate = request.TradeDate ?? DateTime.UtcNow,
            Status = TradeStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        _logger.LogInformation("Creating trade {TradeId} for {Instrument}", trade.TradeId, trade.Instrument);

        // Store in Oracle DB
        await _repository.CreateAsync(trade);

        // Update status and persist
        trade.Status = TradeStatus.Submitted;
        await _repository.UpdateAsync(trade);

        // Publish message to Service Bus for SettlementService to process
        await _messagePublisher.PublishTradeSubmittedAsync(trade);

        _logger.LogInformation("Trade {TradeId} created and submitted for settlement", trade.TradeId);
        
        return CreatedAtAction(nameof(GetById), new { tradeId = trade.TradeId }, trade);
    }
}

public class CreateTradeRequest
{
    public string Instrument { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public string Counterparty { get; set; } = string.Empty;
    public DateTime? TradeDate { get; set; }
}

