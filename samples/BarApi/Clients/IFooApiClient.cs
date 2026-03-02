using ApiContracts;

namespace BarApi.Clients;

/// <summary>Typed HTTP client for calling FooApi endpoints.</summary>
public interface IFooApiClient
{
    [ApiCall("FooApi", "GET /orders")]
    Task<IReadOnlyList<object>> GetOrdersAsync();

    [ApiCall("FooApi", "GET /orders/{id}")]
    Task<object?> GetOrderAsync(int id);

    [ApiCall("FooApi", "GET /health")]
    Task<string> GetHealthAsync();
}
