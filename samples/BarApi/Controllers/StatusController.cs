using BarApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BarApi.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class StatusController : ControllerBase
{
    private readonly IOrderService _orders;

    public StatusController(IOrderService orders) => _orders = orders;

    /// <summary>Returns BarApi operational status, including a health check against FooApi.</summary>
    [HttpGet]
    public async Task<IActionResult> GetStatus()
    {
        var fooHealth = await _orders.CheckSystemHealthAsync();

        return Ok(new
        {
            api    = "BarApi",
            status = "Healthy",
            foo    = fooHealth,
        });
    }
}
