using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.IdentityModel.Tokens;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Infrastructure.Grpc;
using Kinetix.OrderService.Security;
using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Application.Services;
using Pricing.V1;
using Kinetix.OrderService.Infrastructure.Background;
using Kinetix.OrderService.Infrastructure.Configuration;
using Kinetix.OrderService.Infrastructure.Http;
using Kinetix.OrderService.Infrastructure.Lifecycle;
using Kinetix.OrderService.Infrastructure.Observability;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Prometheus;

EnvLoader.Load();

var builder = WebApplication.CreateBuilder(args);

var configuredLogLevel = LogLevelSetting.FromEnvironment(builder.Configuration["LOG_LEVEL"]);
if (configuredLogLevel is not null) {
    builder.Logging.SetMinimumLevel(configuredLogLevel.Value);
}

builder.Logging.AddConsoleFormatter<KinetixJsonConsoleFormatter, ConsoleFormatterOptions>(options => {
    options.IncludeScopes = true;
});

builder.Logging.AddConsole(options => options.FormatterName = KinetixJsonConsoleFormatter.FormatterName);

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

    await Kinetix.OrderService.Infrastructure.Migration.PrincipalBackfill.RunAsync(connectionString);

    await migrateContext.Database.MigrateAsync();
    Console.WriteLine($"applied {pending.Count} migration(s)");
    return 0;
}

Metrics.SuppressDefaultMetrics(new SuppressDefaultMetricOptions {
    SuppressEventCounters = true,
    SuppressMeters = true,
});

builder.Services.AddSingleton(new KinetixMetrics(
    Metrics.DefaultFactory,
    BuildVersion.Of(builder.Configuration["KINETIX_BUILD_VERSION"])
));

builder.Services.AddSingleton<DrainState>();
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = ShutdownTimeout.FromEnvironment(
        builder.Configuration["KINETIX_SHUTDOWN_TIMEOUT_SECONDS"]
    )
);

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString)
);

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
        : url
    );

var grpcDeadline = TimeSpan.FromSeconds(
    double.TryParse(builder.Configuration["KINETIX_GRPC_DEADLINE_SECONDS"], out var seconds)
        ? seconds
        : 5
    );
builder.Services.AddSingleton(new GrpcDeadlineInterceptor(grpcDeadline));

HttpMessageHandler MeshHandler() => new SocketsHttpHandler {
    SslOptions = new SslClientAuthenticationOptions {
        ClientCertificates = new X509Certificate2Collection(serviceIdentity.Leaf),
        RemoteCertificateValidationCallback = (_, cert, _, _) =>
            cert is X509Certificate2 c && serviceIdentity.IsIssuedByOurCa(c),
    },
};

var pricingAddress = AsMesh(pricingGrpcUrl);

builder.Services.AddGrpcClient<PricingService.PricingServiceClient>(options => {
    options.Address = pricingAddress;
}).ConfigurePrimaryHttpMessageHandler(MeshHandler)
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      pricingAddress.Host, services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

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
var matchingAddress = AsMesh(matchingGrpcUrl);

builder.Services.AddGrpcClient<Shipping.V1.ShippingService.ShippingServiceClient>(options => {
    options.Address = matchingAddress;
}).ConfigurePrimaryHttpMessageHandler(MeshHandler)
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      matchingAddress.Host, services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

var warehouseGrpcUrl = builder.Configuration["WAREHOUSE_GRPC_URL"] ?? "http://kinetix-warehouse-grpc:50051";
var paymentGrpcUrl = builder.Configuration["PAYMENT_GRPC_URL"] ?? "http://kinetix-payment-service:50056";
var warehouseAddress = AsMesh(warehouseGrpcUrl);
var paymentAddress = AsMesh(paymentGrpcUrl);

builder.Services.AddGrpcClient<Fulfillment.V1.BinStockService.BinStockServiceClient>(options => {
    options.Address = warehouseAddress;
}).ConfigurePrimaryHttpMessageHandler(MeshHandler)
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      warehouseAddress.Host, services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

builder.Services.AddGrpcClient<Payment.V1.PaymentService.PaymentServiceClient>(options => {
    options.Address = paymentAddress;
}).ConfigurePrimaryHttpMessageHandler(MeshHandler)
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      paymentAddress.Host, services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

builder.Services.AddGrpc(options => {
    options.Interceptors.Add<GrpcServerCallMetricsInterceptor>();
    options.Interceptors.Add<PeerAuthorizationInterceptor>();
});
builder.Services.AddSingleton<GrpcServerCallMetricsInterceptor>();
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
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RoleClaimType = "role",
        };
    });

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwksKeyProvider>((options, jwks) => {
        options.TokenValidationParameters.IssuerSigningKeyResolver = jwks.Resolve;
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Kinetix.OrderService.Controllers.SagaAdminController.SagaOperatorPolicy, policy =>
        policy.RequireAuthenticatedUser().RequireRole("operator", "admin")
    );

builder.Services.AddScoped<IPricingClient, PricingGrpcClient>();
builder.Services.AddScoped<IShippingClient, ShippingGrpcClient>();
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IVoucherQuotaClient, VoucherQuotaGrpcClient>();
builder.Services.AddScoped<IFlashSaleClient, FlashSaleGrpcClient>();
builder.Services.AddScoped<IStockClient, StockGrpcClient>();
builder.Services.AddScoped<IEscrowClient, EscrowGrpcClient>();

builder.Services.AddSingleton<SagaWorkerIdentity>();
builder.Services.AddSingleton(CompensationPolicy.FromConfiguration(builder.Configuration));
builder.Services.AddScoped<ISagaLeaseStore, SagaLeaseStore>();

builder.Services.AddScoped<CheckoutSagaRunner>();
builder.Services.AddScoped<IOrderService, OrderService>();

builder.Services.AddHostedService<StuckSagaSweeper>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<RequestIdAccessor>();
builder.Services.AddScoped<RequestIdForwardingInterceptor>();

builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddGrpcReflection();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

var metrics = app.Services.GetRequiredService<KinetixMetrics>();
metrics.RegisterGrpcServerSurface(Order.V1.OrderService.Descriptor);
metrics.RegisterGrpcClientSurface(pricingAddress.Host, Pricing.V1.PricingService.Descriptor);
metrics.RegisterGrpcClientSurface(matchingAddress.Host, Shipping.V1.ShippingService.Descriptor);
metrics.RegisterGrpcClientSurface(warehouseAddress.Host, Fulfillment.V1.BinStockService.Descriptor);
metrics.RegisterGrpcClientSurface(paymentAddress.Host, Payment.V1.PaymentService.Descriptor);

var drain = app.Services.GetRequiredService<DrainState>();
var shutdownBudget = app.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

lifetime.ApplicationStopping.Register(() => {
    drain.Begin();
    app.Logger.LogInformation(
        "SIGTERM: the listeners are closed and readiness now answers draining. In-flight HTTP "
            + "requests, gRPC calls and any compensation round the sweeper has already begun have "
            + "{Seconds}s to finish. Whatever is still running then is aborted; a saga left "
            + "mid-unwind waits for its lease to expire and for the next sweep to claim it, and "
            + "nothing is retried from here",
        shutdownBudget.TotalSeconds
    );
});

lifetime.ApplicationStopped.Register(() =>
    app.Logger.LogInformation("drain finished; the process is exiting")
);

await WarmJwksAsync(app);
app.Logger.LogInformation("gRPC listening on {Port} (mTLS)", grpcPort);

static async Task WarmJwksAsync(WebApplication app) {
    var jwks = app.Services.GetRequiredService<JwksKeyProvider>();
    const int attempts = 5;

    for (var attempt = 1; attempt <= attempts; attempt++) {
        try {
            var loaded = await jwks.RefreshAsync();
            app.Logger.LogInformation("loaded {Count} signing key(s) from identity's JWKS", loaded);
            return;
        } catch (Exception ex) when (attempt < attempts) {
            var delay = TimeSpan.FromSeconds(attempt * 2);
            app.Logger.LogWarning(
                "identity's JWKS is not answering yet ({Message}); retrying in {Delay}s ({Attempt}/{Attempts})",
                ex.Message, delay.TotalSeconds, attempt, attempts
            );
            await Task.Delay(delay);
        } catch (Exception ex) {
            app.Logger.LogError(
                ex, "identity's JWKS did not answer in {Attempts} attempts; starting without keys, "
                  + "which means the first authenticated request will fetch them", attempts
            );
        }
    }
}

app.UseMiddleware<HttpMetricsMiddleware>();

app.UseMiddleware<RequestIdMiddleware>();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment()) {
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

MetricsEndpoint.Map(app, Metrics.DefaultRegistry).AllowAnonymous();

app.MapGrpcService<OrderGrpcServerService>().RequireHost($"*:{grpcPort}");

app.MapGrpcReflectionService();

await app.RunAsync();
return 0;
