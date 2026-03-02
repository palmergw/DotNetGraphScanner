using BarApi.Clients;

namespace BarApi.Services;

public interface IOrderService
{
    Task<IReadOnlyList<object>> GetActiveOrdersAsync();
    Task<object?> ValidateOrderAsync(int orderId);
    Task<string> CheckSystemHealthAsync();
}

public sealed class OrderService : IOrderService
{
    private readonly IFooApiClient _foo;

    public OrderService(IFooApiClient foo) => _foo = foo;

    public async Task<IReadOnlyList<object>> GetActiveOrdersAsync()
    {
        return await _foo.GetOrdersAsync();
    }

    public async Task<object?> ValidateOrderAsync(int orderId)
    {
        return await _foo.GetOrderAsync(orderId);
    }

    public async Task<string> CheckSystemHealthAsync()
    {
        return await _foo.GetHealthAsync();
    }
}
