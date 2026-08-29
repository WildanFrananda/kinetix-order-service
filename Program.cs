using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Grpc.Pricing;
using Kinetix.OrderService.Infrastructure.Persistence;

EnvLoader.Load();

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["DATABASE_URL"]
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("DATABASE_URL environment variable is required and missing.");

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

var redisConnectionString = builder.Configuration["REDIS_CONNECTION_STRING"]
    ?? builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("REDIS_CONNECTION_STRING environment variable is required and missing.");

builder.Services.AddStackExchangeRedisCache(options => {
    options.Configuration = redisConnectionString;
    options.InstanceName = "KinetixOrder:";
});

var pricingGrpcUrl = builder.Configuration["PRICING_GRPC_URL"]
    ?? throw new InvalidOperationException("PRICING_GRPC_URL environment variable is required and missing.");

builder.Services.AddGrpcClient<PricingService.PricingServiceClient>(options => {
    options.Address = new Uri(pricingGrpcUrl);
});

var matchingGrpcUrl = builder.Configuration["MATCHING_GRPC_URL"] ?? "http://kinetix-matching-service:50053";

builder.Services.AddGrpcClient<Kinetix.OrderService.Grpc.Shipping.ShippingService.ShippingServiceClient>(options => {
    options.Address = new Uri(matchingGrpcUrl);
});

builder.Services.AddGrpc();

builder.Services.AddScoped<IPricingClient, PricingGrpcClient>();
builder.Services.AddScoped<IShippingClient, ShippingGrpcClient>();
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IOrderService, OrderService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment()) {
    app.UseSwagger();
    app.UseSwaggerUI();
}

using (var scope = app.Services.CreateScope()) {
    var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    try {
        dbContext.Database.EnsureCreated();
    } catch (Exception ex) {
        Console.WriteLine($"Database initialization warning: {ex.Message}");
    }
}

app.UseAuthorization();
app.MapControllers();
app.MapGrpcService<OrderGrpcServerService>();

var port = builder.Configuration["PORT"] ?? "8001";
app.Run($"http://0.0.0.0:{port}");
