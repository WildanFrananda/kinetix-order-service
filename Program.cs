using Kinetix.OrderService.Application.Fulfillment;
using Kinetix.OrderService.Application.Delivery;
using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.Application.Completion;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
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
using Kinetix.OrderService.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Prometheus;
using StackExchange.Redis;

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

var connectionString = RequiredSetting.Text(builder.Configuration, "DATABASE_URL");

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

    await Kinetix.OrderService.Infrastructure.Migration.PrincipalBackfill.RunAsync(
        connectionString,
        builder.Configuration
    );

    await migrateContext.Database.MigrateAsync();
    Console.WriteLine($"applied {pending.Count} migration(s)");
    return 0;
}

var settings = OrderSettings.FromConfiguration(builder.Configuration);

Metrics.SuppressDefaultMetrics(new SuppressDefaultMetricOptions {
    SuppressEventCounters = true,
    SuppressMeters = true,
});

builder.Services.AddSingleton(new KinetixMetrics(
    Metrics.DefaultFactory,
    BuildVersion.Of(builder.Configuration["KINETIX_BUILD_VERSION"])
));

builder.Services.AddSingleton<AdminAudit>();
builder.Services.AddSingleton<DrainState>();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = settings.ShutdownTimeout);

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString)
);

var redisOptions = ConfigurationOptions.Parse(settings.RedisConnectionString);
redisOptions.AbortOnConnectFail = false;
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
builder.Services.AddSingleton<ICartStore, RedisCartStore>();

var serviceIdentity = ServiceIdentity.Load();

static Uri AsMesh(Uri url) =>
    url.Scheme == Uri.UriSchemeHttp
        ? new UriBuilder(url) { Scheme = Uri.UriSchemeHttps, Port = url.IsDefaultPort ? -1 : url.Port }.Uri
        : url;

builder.Services.AddSingleton(new GrpcDeadlineInterceptor(settings.GrpcDeadline));

HttpMessageHandler MeshHandler(IServiceProvider services, string expectedService) =>
    MeshClientHandler.For(
        serviceIdentity,
        expectedService,
        services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MeshClientHandler))
    );

var pricingAddress = AsMesh(settings.PricingGrpcUrl);

builder.Services.AddGrpcClient<PricingService.PricingServiceClient>(options => {
    options.Address = pricingAddress;
}).ConfigurePrimaryHttpMessageHandler(services => MeshHandler(services, "pricing"))
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      pricingAddress.Host,
      services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

builder.WebHost.ConfigureKestrel(options => {
    options.ListenAnyIP(settings.RestPort, listen => {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
    });

    options.ListenAnyIP(settings.GrpcPort, listen => {
        listen.Protocols = HttpProtocols.Http2;

        listen.UseHttps(serviceIdentity.Leaf, https => {
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (cert, _, _) => serviceIdentity.IsIssuedByOurCa(cert);
        });
    });
});

var matchingAddress = AsMesh(settings.MatchingGrpcUrl);

builder.Services.AddGrpcClient<Shipping.V1.ShippingService.ShippingServiceClient>(options => {
    options.Address = matchingAddress;
}).ConfigurePrimaryHttpMessageHandler(services => MeshHandler(services, "matching"))
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      matchingAddress.Host,
      services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

var catalogAddress = AsMesh(settings.CatalogGrpcUrl);

builder.Services.AddGrpcClient<Catalog.V1.CatalogService.CatalogServiceClient>(options => {
    options.Address = catalogAddress;
}).ConfigurePrimaryHttpMessageHandler(services => MeshHandler(services, "catalog"))
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      catalogAddress.Host,
      services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

var warehouseAddress = AsMesh(settings.WarehouseGrpcUrl);
var paymentAddress = AsMesh(settings.PaymentGrpcUrl);
var identityAddress = AsMesh(settings.IdentityGrpcUrl);

builder.Services.AddGrpcClient<Fulfillment.V1.BinStockService.BinStockServiceClient>(options => {
    options.Address = warehouseAddress;
}).ConfigurePrimaryHttpMessageHandler(services => MeshHandler(services, "warehouse"))
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      warehouseAddress.Host,
      services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

builder.Services.AddGrpcClient<Fulfillment.V1.FulfillmentTaskService.FulfillmentTaskServiceClient>(options => {
    options.Address = warehouseAddress;
}).ConfigurePrimaryHttpMessageHandler(services => MeshHandler(services, "warehouse"))
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      warehouseAddress.Host,
      services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

builder.Services.AddGrpcClient<Fleet.V1.CourierTelemetryService.CourierTelemetryServiceClient>(options => {
    options.Address = matchingAddress;
}).ConfigurePrimaryHttpMessageHandler(services => MeshHandler(services, "matching"))
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      matchingAddress.Host,
      services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

builder.Services.AddGrpcClient<Payment.V1.PaymentService.PaymentServiceClient>(options => {
    options.Address = paymentAddress;
}).ConfigurePrimaryHttpMessageHandler(services => MeshHandler(services, "payment"))
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      paymentAddress.Host,
      services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

builder.Services.AddGrpcClient<Identity.V1.IdentityService.IdentityServiceClient>(options => {
    options.Address = identityAddress;
}).ConfigurePrimaryHttpMessageHandler(services => MeshHandler(services, "identity"))
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      identityAddress.Host,
      services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

builder.Services.AddGrpcClient<Geo.V1.GeocodingService.GeocodingServiceClient>(options => {
    options.Address = identityAddress;
}).ConfigurePrimaryHttpMessageHandler(services => MeshHandler(services, "identity"))
  .AddInterceptor(services => new GrpcClientCallMetricsInterceptor(
      identityAddress.Host,
      services.GetRequiredService<KinetixMetrics>()
  ))
  .AddInterceptor<RequestIdForwardingInterceptor>()
  .AddInterceptor<GrpcDeadlineInterceptor>();

builder.Services.AddScoped<IAddressDirectory, IdentityAddressDirectory>();
builder.Services.AddSingleton(GeocodeDeadline.FromConfiguration(builder.Configuration));
builder.Services.AddScoped<IAddressGeocoder, IdentityAddressGeocoder>();
builder.Services.AddScoped<IMerchantStanding, IdentityMerchantStanding>();

builder.Services.AddGrpc(options => {
    options.Interceptors.Add<GrpcServerCallMetricsInterceptor>();
    options.Interceptors.Add<PeerAuthorizationInterceptor>();
    options.Interceptors.Add<DatabaseOutageInterceptor>();
});
builder.Services.AddSingleton<GrpcServerCallMetricsInterceptor>();
builder.Services.AddSingleton<PeerAuthorizationInterceptor>();
builder.Services.AddSingleton<DatabaseOutageInterceptor>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAccessTokens(settings.JwtIssuer, settings.JwtAudience);
builder.Services.AddAuthorizationBuilder().AddOrderPolicies();

builder.Services.AddScoped<IPricingClient, PricingGrpcClient>();
builder.Services.AddScoped<IShippingClient, ShippingGrpcClient>();
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IVoucherQuotaClient, VoucherQuotaGrpcClient>();
builder.Services.AddScoped<IFlashSaleClient, FlashSaleGrpcClient>();
builder.Services.AddScoped<IStockClient, StockGrpcClient>();
builder.Services.AddScoped<IProductDirectory, CatalogProductDirectory>();
builder.Services.AddScoped<IEscrowClient, EscrowGrpcClient>();
builder.Services.AddScoped<IFulfillmentClient, FulfillmentGrpcClient>();
builder.Services.AddScoped<ICourierDispatcher, CourierDispatcher>();
builder.Services.AddScoped<IFulfillmentPackedHandler, FulfillmentPackedHandler>();
builder.Services.AddScoped<IOrderDeliveredHandler, OrderDeliveredHandler>();
builder.Services.AddScoped<IReturnsHandler, ReturnsHandler>();
builder.Services.AddScoped<IReturnRefunds, ReturnRefunds>();
builder.Services.AddScoped<IReturnRejection, ReturnRejection>();
builder.Services.AddScoped<IOrderRowLock, OrderRowLock>();
builder.Services.AddScoped<IOrderCompletion, OrderCompletion>();
builder.Services.AddScoped<IOrderLifecycleFeed, OrderLifecycleFeed>();
builder.Services.AddSingleton(ReturnWindow.FromConfiguration(builder.Configuration));

builder.Services.AddSingleton<SagaWorkerIdentity>();
builder.Services.AddSingleton(CompensationPolicy.FromConfiguration(builder.Configuration));
builder.Services.AddScoped<ISagaLeaseStore, SagaLeaseStore>();

builder.Services.AddScoped<CheckoutSagaRunner>();
builder.Services.AddScoped<CompletedCheckoutFinisher>();
builder.Services.AddScoped<SagalessCheckoutCanceller>();
builder.Services.AddScoped<IOrderService, OrderService>();

builder.Services.AddHostedService<StuckSagaSweeper>();
builder.Services.AddHostedService<ShippingSettlementSweeper>();
builder.Services.AddHostedService<CourierDispatchSweeper>();
builder.Services.AddHostedService<OrderCompletionSweeper>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<RequestIdAccessor>();
builder.Services.AddScoped<RequestIdForwardingInterceptor>();

builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddGrpcReflection();
builder.Services.AddOrderControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

_ = app.Services.GetRequiredService<PeerAuthorizationInterceptor>();

var metrics = app.Services.GetRequiredService<KinetixMetrics>();
metrics.RegisterGrpcServerSurface(Order.V1.OrderService.Descriptor);
metrics.RegisterGrpcClientSurface(pricingAddress.Host, Pricing.V1.PricingService.Descriptor);
metrics.RegisterGrpcClientSurface(matchingAddress.Host, Shipping.V1.ShippingService.Descriptor);
metrics.RegisterGrpcClientSurface(matchingAddress.Host, Fleet.V1.CourierTelemetryService.Descriptor);
metrics.RegisterGrpcClientSurface(warehouseAddress.Host, Fulfillment.V1.BinStockService.Descriptor);
metrics.RegisterGrpcClientSurface(warehouseAddress.Host, Fulfillment.V1.FulfillmentTaskService.Descriptor);
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
app.Logger.LogInformation("gRPC listening on {Port} (mTLS)", settings.GrpcPort);

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
                ex.Message,
                delay.TotalSeconds,
                attempt,
                attempts
            );
            await Task.Delay(delay);
        } catch (Exception ex) {
            app.Logger.LogError(
                ex,
                "identity's JWKS did not answer in {Attempts} attempts; starting without keys, "
                  + "which means the first authenticated request will fetch them",
                attempts
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

app.MapGrpcService<OrderGrpcServerService>().RequireHost($"*:{settings.GrpcPort}");
app.MapGrpcService<OrderLifecycleGrpcService>().RequireHost($"*:{settings.GrpcPort}");

app.MapGrpcReflectionService();

await app.RunAsync();
return 0;
