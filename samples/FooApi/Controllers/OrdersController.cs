using FooApi.Models;
using FooApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FooApi.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class OrdersController : ControllerBase
{
    private readonly IInventoryService _inventory;
    private readonly INotificationService _notifications;

    // Simulated in-memory store for the sample
    private static readonly List<Order> _orders = new();
    private static int _nextId = 1;

    public OrdersController(IInventoryService inventory, INotificationService notifications)
    {
        _inventory     = inventory;
        _notifications = notifications;
    }

    /// <summary>Returns all current orders. Checks live inventory availability via BarApi.</summary>
    [HttpGet]
    public async Task<IActionResult> GetOrders()
    {
        // Check inventory status from BarApi for each distinct product
        var productIds = _orders.Select(o => o.ProductId).Distinct().ToList();
        foreach (var productId in productIds)
        {
            await _inventory.GetAvailableInventoryAsync(productId);
        }
        return Ok(_orders);
    }

    /// <summary>Retrieves a single order by ID.</summary>
    [HttpGet("{id:int}")]
    public IActionResult GetOrder(int id)
    {
        var order = _orders.FirstOrDefault(o => o.Id == id);
        return order is null ? NotFound() : Ok(order);
    }

    /// <summary>Creates a new order after reserving inventory and sending a confirmation notification.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateOrder([FromBody] CreateOrderRequest request)
    {
        // Reserve inventory from BarApi
        var reservation = await _inventory.ReserveInventoryAsync(request.ProductId, request.Quantity);
        if (!reservation.Reserved)
            return Conflict(new { error = "Insufficient inventory" });

        var order = new Order(_nextId++, request.ProductId, request.Quantity, "Confirmed");
        _orders.Add(order);

        // Send confirmation notification through BarApi
        await _notifications.SendOrderCreatedAsync(request.CustomerEmail, order.Id);

        return Created($"/orders/{order.Id}", order);
    }

    /// <summary>Cancels an existing order and notifies the customer.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> CancelOrder(int id)
    {
        var order = _orders.FirstOrDefault(o => o.Id == id);
        if (order is null) return NotFound();

        _orders.Remove(order);
        _orders.Add(order with { Status = "Cancelled" });

        await _notifications.SendOrderCancelledAsync("customer@example.com", id);

        return NoContent();
    }
}
