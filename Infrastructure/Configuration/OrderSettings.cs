namespace Kinetix.OrderService.Infrastructure.Configuration;

public sealed record OrderSettings(
    string RedisConnectionString,
    string JwtIssuer,
    string JwtAudience,
    int RestPort,
    int GrpcPort,
    Uri PricingGrpcUrl,
    Uri MatchingGrpcUrl,
    Uri CatalogGrpcUrl,
    Uri WarehouseGrpcUrl,
    Uri IdentityGrpcUrl,
    Uri PaymentGrpcUrl,
    TimeSpan GrpcDeadline,
    TimeSpan ShutdownTimeout
) {
    public static OrderSettings FromConfiguration(IConfiguration configuration) => new(
        RequiredSetting.Text(configuration, "REDIS_CONNECTION_STRING"),
        RequiredSetting.Text(configuration, "JWT_ISSUER"),
        RequiredSetting.Text(configuration, "JWT_AUDIENCE"),
        RequiredSetting.Port(configuration, "PORT"),
        RequiredSetting.Port(configuration, "GRPC_PORT"),
        RequiredSetting.GrpcUrl(configuration, "PRICING_GRPC_URL"),
        RequiredSetting.GrpcUrl(configuration, "MATCHING_GRPC_URL"),
        RequiredSetting.GrpcUrl(configuration, "CATALOG_GRPC_URL"),
        RequiredSetting.GrpcUrl(configuration, "WAREHOUSE_GRPC_URL"),
        RequiredSetting.GrpcUrl(configuration, "IDENTITY_GRPC_URL"),
        RequiredSetting.GrpcUrl(configuration, "PAYMENT_GRPC_URL"),
        RequiredSetting.Seconds(configuration, "KINETIX_GRPC_DEADLINE_SECONDS"),
        Lifecycle.ShutdownTimeout.FromEnvironment(configuration[Lifecycle.ShutdownTimeout.Setting])
    );
}
