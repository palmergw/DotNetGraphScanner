using ApiContracts;
using FooApi.Models;

namespace FooApi.Clients;

/// <summary>Typed HTTP client for calling BarApi endpoints.</summary>
public interface IBarApiClient
{
    [ApiCall("BarApi", "GET /inventory")]
    Task<InventoryResponse> GetInventoryAsync(string productId);

    [ApiCall("BarApi", "POST /inventory/reserve")]
    Task<InventoryResponse> ReserveInventoryAsync(string productId, int quantity);

    [ApiCall("BarApi", "POST /notifications")]
    Task<NotifyResponse> SendNotificationAsync(string customerEmail, string message);

    [ApiCall("BarApi", "GET /status")]
    Task<string> GetStatusAsync();
}
