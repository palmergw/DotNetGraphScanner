using System.Net.Http.Json;
using FooApi.Models;

namespace FooApi.Clients;

/// <summary>
/// Concrete typed HTTP client implementation for BarApi.
/// Methods here carry [ApiCall] on the interface, which the scanner picks up.
/// </summary>
public sealed class BarApiClient : IBarApiClient
{
    private readonly HttpClient _http;

    public BarApiClient(HttpClient http) => _http = http;

    public async Task<InventoryResponse> GetInventoryAsync(string productId)
    {
        return await _http.GetFromJsonAsync<InventoryResponse>($"/inventory?productId={productId}")
            ?? throw new InvalidOperationException("Empty inventory response");
    }

    public async Task<InventoryResponse> ReserveInventoryAsync(string productId, int quantity)
    {
        var response = await _http.PostAsJsonAsync("/inventory/reserve",
            new { productId, quantity });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InventoryResponse>()
            ?? throw new InvalidOperationException("Empty reserve response");
    }

    public async Task<NotifyResponse> SendNotificationAsync(string customerEmail, string message)
    {
        var response = await _http.PostAsJsonAsync("/notifications",
            new { customerEmail, message });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<NotifyResponse>()
            ?? throw new InvalidOperationException("Empty notification response");
    }

    public async Task<string> GetStatusAsync()
    {
        return await _http.GetStringAsync("/status");
    }
}
