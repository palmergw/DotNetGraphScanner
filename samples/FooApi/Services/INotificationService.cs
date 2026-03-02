using FooApi.Models;

namespace FooApi.Services;

public interface INotificationService
{
    Task SendOrderCreatedAsync(string customerEmail, int orderId);
    Task SendOrderCancelledAsync(string customerEmail, int orderId);
}
