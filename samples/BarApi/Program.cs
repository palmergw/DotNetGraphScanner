using BarApi.Clients;
using BarApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpClient<IFooApiClient, FooApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["FooApi:BaseUrl"] ?? "http://localhost:5000");
});
builder.Services.AddScoped<IOrderService, OrderService>();

var app = builder.Build();

app.MapControllers();
app.Run();
