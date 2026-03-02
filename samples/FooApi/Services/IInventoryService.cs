using FooApi.Models;

namespace FooApi.Services;

public interface IInventoryService
{
    Task<InventoryResponse> GetAvailableInventoryAsync(string productId);
    Task<InventoryResponse> ReserveInventoryAsync(string productId, int quantity);
}
