using FooApi.Clients;
using Microsoft.AspNetCore.Mvc;

namespace FooApi.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class HealthController : ControllerBase
{
    private readonly IBarApiClient _bar;

    public HealthController(IBarApiClient bar) => _bar = bar;

    /// <summary>Returns the health of FooApi and also checks BarApi's status.</summary>
    [HttpGet]
    public async Task<IActionResult> GetHealth()
    {
        var barStatus = await _bar.GetStatusAsync();

        return Ok(new
        {
            api    = "FooApi",
            status = "Healthy",
            bar    = barStatus
        });
    }
}
