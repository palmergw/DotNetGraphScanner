using FooApi.Clients;
using FooApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpClient<IBarApiClient, BarApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["BarApi:BaseUrl"] ?? "http://localhost:5001");
});
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

var app = builder.Build();

app.MapControllers();
app.Run();
