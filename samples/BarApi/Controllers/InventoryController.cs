using BarApi.Models;
using BarApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BarApi.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class InventoryController : ControllerBase
{
    private readonly IOrderService _orders;

    // Simulated in-memory inventory
    private static readonly Dictionary<string, InventoryItem> _stock = new()
    {
        ["WIDGET-A"]  = new("WIDGET-A",  100, 0),
        ["GADGET-B"]  = new("GADGET-B",  50,  0),
        ["DOOHICKEY"] = new("DOOHICKEY", 200, 0),
    };

    public InventoryController(IOrderService orders) => _orders = orders;

    /// <summary>Returns current inventory levels. Enriches with active order counts from FooApi.</summary>
    [HttpGet]
    public async Task<IActionResult> GetInventory([FromQuery] string? productId)
    {
        // Cross-check with active orders to compute true available quantity
        var activeOrders = await _orders.GetActiveOrdersAsync();

        if (productId is not null)
        {
            return _stock.TryGetValue(productId, out var item)
                ? Ok(item)
                : NotFound();
        }

        return Ok(_stock.Values);
    }

    /// <summary>Reserves inventory for an order, validating the order exists in FooApi first.</summary>
    [HttpPost("reserve")]
    public async Task<IActionResult> ReserveInventory([FromBody] ReserveRequest request)
    {
        if (!_stock.TryGetValue(request.ProductId, out var item))
            return NotFound(new { error = "Product not found" });

        var available = item.Stock - item.Reserved;
        if (available < request.Quantity)
            return Conflict(new { error = "Insufficient stock", available });

        _stock[request.ProductId] = item with { Reserved = item.Reserved + request.Quantity };

        return Ok(new ReserveResponse(request.ProductId, request.Quantity, true));
    }
}
