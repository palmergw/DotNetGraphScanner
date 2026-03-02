namespace FooApi.Models;

public record Order(int Id, string ProductId, int Quantity, string Status);

public record CreateOrderRequest(string ProductId, int Quantity, string CustomerEmail);

public record InventoryResponse(string ProductId, int Available, bool Reserved);

public record NotifyResponse(string NotificationId, bool Accepted);
