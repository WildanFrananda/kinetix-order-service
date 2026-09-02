using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Grpc.Pricing;
using Kinetix.OrderService.Infrastructure.Persistence;

EnvLoader.Load();

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["DATABASE_URL"]
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("DATABASE_URL environment variable is required and missing.");

if (args.Contains("--migrate")) {
    var migrateOptions = new DbContextOptionsBuilder<OrderDbContext>()
        .UseNpgsql(connectionString)
        .Options;

    await using var migrateContext = new OrderDbContext(migrateOptions);

    var pending = (await migrateContext.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count == 0) {
        Console.WriteLine("schema is up to date; no migrations to apply");
        return 0;
    }

    foreach (var name in pending) {
        Console.WriteLine($"applying {name}");
    }

    await migrateContext.Database.MigrateAsync();
    Console.WriteLine($"applied {pending.Count} migration(s)");
    return 0;
}

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

var restPort = int.Parse(builder.Configuration["PORT"] ?? "8001");
var grpcPort = int.Parse(builder.Configuration["GRPC_PORT"] ?? "50055");

builder.WebHost.ConfigureKestrel(options => {
    options.ListenAnyIP(restPort, listen => {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
    });

    options.ListenAnyIP(grpcPort, listen => {
        listen.Protocols = HttpProtocols.Http2;
    });
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

builder.Services.AddGrpcReflection();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment()) {
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();

app.MapGrpcService<OrderGrpcServerService>().RequireHost($"*:{grpcPort}");

if (app.Environment.IsDevelopment()) {
    app.MapGrpcReflectionService();
}

await app.RunAsync();
return 0;
