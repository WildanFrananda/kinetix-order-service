using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.IdentityModel.Tokens;
using Kinetix.OrderService.Security;
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

var serviceIdentity = ServiceIdentity.Load();

static Uri AsMesh(string url) =>
    new(url.StartsWith("http://", StringComparison.Ordinal)
        ? string.Concat("https://", url.AsSpan("http://".Length))
        : url);

HttpMessageHandler MeshHandler() => new SocketsHttpHandler {
    SslOptions = new SslClientAuthenticationOptions {
        ClientCertificates = new X509Certificate2Collection(serviceIdentity.Leaf),
        RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is X509Certificate2 c && serviceIdentity.IsIssuedByOurCa(c),
    },
};

builder.Services.AddGrpcClient<PricingService.PricingServiceClient>(options => {
    options.Address = AsMesh(pricingGrpcUrl);
}).ConfigurePrimaryHttpMessageHandler(MeshHandler);

var restPort = int.Parse(builder.Configuration["PORT"] ?? "8001");
var grpcPort = int.Parse(builder.Configuration["GRPC_PORT"] ?? "50055");

builder.WebHost.ConfigureKestrel(options => {
    options.ListenAnyIP(restPort, listen => {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
    });

    options.ListenAnyIP(grpcPort, listen => {
        listen.Protocols = HttpProtocols.Http2;

        listen.UseHttps(serviceIdentity.Leaf, https => {
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (cert, _, _) => serviceIdentity.IsIssuedByOurCa(cert);
        });
    });
});

var matchingGrpcUrl = builder.Configuration["MATCHING_GRPC_URL"] ?? "http://kinetix-matching-service:50053";

builder.Services.AddGrpcClient<Kinetix.OrderService.Grpc.Shipping.ShippingService.ShippingServiceClient>(options => {
    options.Address = AsMesh(matchingGrpcUrl);
}).ConfigurePrimaryHttpMessageHandler(MeshHandler);

builder.Services.AddGrpc(options => {
    options.Interceptors.Add<PeerAuthorizationInterceptor>();
});
builder.Services.AddSingleton<PeerAuthorizationInterceptor>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<JwksKeyProvider>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => {
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["JWT_ISSUER"]
                ?? throw new InvalidOperationException("JWT_ISSUER is required and has no default."),
            ValidateAudience = true,
            ValidAudience = builder.Configuration["JWT_AUDIENCE"]
                ?? throw new InvalidOperationException("JWT_AUDIENCE is required and has no default."),
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
        };
    });

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwksKeyProvider>((options, jwks) => {
        options.TokenValidationParameters.IssuerSigningKeyResolver = jwks.Resolve;
    });
builder.Services.AddAuthorization();

builder.Services.AddScoped<IPricingClient, PricingGrpcClient>();
builder.Services.AddScoped<IShippingClient, ShippingGrpcClient>();
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IOrderService, OrderService>();

builder.Services.AddGrpcReflection();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

{
    var jwks = app.Services.GetRequiredService<JwksKeyProvider>();
    var loaded = await jwks.RefreshAsync();
    app.Logger.LogInformation("loaded {Count} signing key(s) from identity's JWKS", loaded);
    app.Logger.LogInformation("gRPC listening on {Port} (mTLS)", grpcPort);
}

if (app.Environment.IsDevelopment()) {
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGrpcService<OrderGrpcServerService>().RequireHost($"*:{grpcPort}");

if (app.Environment.IsDevelopment()) {
    app.MapGrpcReflectionService();
}

await app.RunAsync();
return 0;
