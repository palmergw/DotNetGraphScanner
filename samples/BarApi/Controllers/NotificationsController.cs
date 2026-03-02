using BarApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace BarApi.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class NotificationsController : ControllerBase
{
    private static readonly List<Notification> _notifications = new();
    private static int _nextId = 1;

    /// <summary>Queues a notification to be sent to a customer.</summary>
    [HttpPost]
    public IActionResult SendNotification([FromBody] SendNotificationRequest request)
    {
        var notification = new Notification(
            $"notif-{_nextId++}",
            request.CustomerEmail,
            request.Message,
            "Queued");

        _notifications.Add(notification);
        return Created($"/notifications/{notification.Id}", notification);
    }

    /// <summary>Returns the delivery status of a specific notification.</summary>
    [HttpGet("{id}")]
    public IActionResult GetNotification(string id)
    {
        var notification = _notifications.FirstOrDefault(n => n.Id == id);
        return notification is null ? NotFound() : Ok(notification);
    }
}
