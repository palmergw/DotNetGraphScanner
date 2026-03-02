using FooApi.Clients;
using FooApi.Models;

namespace FooApi.Services;

public sealed class InventoryService : IInventoryService
{
    private readonly IBarApiClient _bar;

    public InventoryService(IBarApiClient bar) => _bar = bar;

    public async Task<InventoryResponse> GetAvailableInventoryAsync(string productId)
    {
        return await _bar.GetInventoryAsync(productId);
    }

    public async Task<InventoryResponse> ReserveInventoryAsync(string productId, int quantity)
    {
        return await _bar.ReserveInventoryAsync(productId, quantity);
    }
}
