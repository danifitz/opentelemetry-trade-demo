using Microsoft.AspNetCore.Mvc;
using Shared.Models;
using SettlementService.Data;

namespace SettlementService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettlementController : ControllerBase
{
    private readonly ITradeRepository _repository;
    private readonly ILogger<SettlementController> _logger;

    public SettlementController(
        ITradeRepository repository,
        ILogger<SettlementController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Get settlement status for a specific trade
    /// </summary>
    [HttpGet("{tradeId}/status")]
    public async Task<ActionResult<SettlementStatus>> GetSettlementStatus(string tradeId)
    {
        _logger.LogInformation("Checking settlement status for trade {TradeId}", tradeId);
        
        var trade = await _repository.GetByIdAsync(tradeId);
        
        if (trade == null)
        {
            _logger.LogWarning("Trade {TradeId} not found", tradeId);
            return NotFound(new { message = $"Trade {tradeId} not found" });
        }

        var status = new SettlementStatus
        {
            TradeId = trade.TradeId,
            Status = trade.Status.ToString(),
            IsSettled = trade.Status == TradeStatus.Settled,
            SettledAt = trade.SettledAt,
            Instrument = trade.Instrument,
            Quantity = trade.Quantity,
            Price = trade.Price
        };

        return Ok(status);
    }
}

public class SettlementStatus
{
    public string TradeId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsSettled { get; set; }
    public DateTime? SettledAt { get; set; }
    public string Instrument { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
}

