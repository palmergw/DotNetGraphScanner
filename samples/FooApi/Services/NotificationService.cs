using FooApi.Clients;

namespace FooApi.Services;

public sealed class NotificationService : INotificationService
{
    private readonly IBarApiClient _bar;

    public NotificationService(IBarApiClient bar) => _bar = bar;

    public async Task SendOrderCreatedAsync(string customerEmail, int orderId)
    {
        await _bar.SendNotificationAsync(customerEmail,
            $"Your order #{orderId} has been placed successfully.");
    }

    public async Task SendOrderCancelledAsync(string customerEmail, int orderId)
    {
        await _bar.SendNotificationAsync(customerEmail,
            $"Your order #{orderId} has been cancelled.");
    }
}
