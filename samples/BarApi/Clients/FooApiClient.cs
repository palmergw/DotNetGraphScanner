using System.Net.Http.Json;

namespace BarApi.Clients;

/// <summary>
/// Concrete typed HTTP client for FooApi.
/// The [ApiCall] annotations live on the interface; the scanner reads those.
/// </summary>
public sealed class FooApiClient : IFooApiClient
{
    private readonly HttpClient _http;

    public FooApiClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<object>> GetOrdersAsync()
    {
        return await _http.GetFromJsonAsync<List<object>>("/orders")
            ?? new List<object>();
    }

    public async Task<object?> GetOrderAsync(int id)
    {
        return await _http.GetFromJsonAsync<object>($"/orders/{id}");
    }

    public async Task<string> GetHealthAsync()
    {
        return await _http.GetStringAsync("/health");
    }
}
