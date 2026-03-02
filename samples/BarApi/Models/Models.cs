namespace BarApi.Models;

public record InventoryItem(string ProductId, int Stock, int Reserved);

public record ReserveRequest(string ProductId, int Quantity);

public record ReserveResponse(string ProductId, int Reserved, bool Success);

public record Notification(string Id, string CustomerEmail, string Message, string Status);

public record SendNotificationRequest(string CustomerEmail, string Message);
