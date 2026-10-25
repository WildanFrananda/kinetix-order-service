using Kinetix.OrderService.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace Kinetix.OrderService.Tests;

public class OrderSettingsTests {
    private static readonly string[] Urls = [
        "PRICING_GRPC_URL",
        "MATCHING_GRPC_URL",
        "CATALOG_GRPC_URL",
        "WAREHOUSE_GRPC_URL",
        "IDENTITY_GRPC_URL",
        "PAYMENT_GRPC_URL",
    ];

    private static readonly string[] Ports = ["PORT", "GRPC_PORT"];

    private static readonly string[] Durations = ["KINETIX_GRPC_DEADLINE_SECONDS", "KINETIX_SHUTDOWN_TIMEOUT_SECONDS"];

    private static readonly string[] Texts = ["REDIS_CONNECTION_STRING", "JWT_ISSUER", "JWT_AUDIENCE"];

    private static Dictionary<string, string?> Lab() => new() {
        ["REDIS_CONNECTION_STRING"] = "kinetix-redis:6379,password=test-only",
        ["JWT_ISSUER"] = "https://identity.kinetix.local",
        ["JWT_AUDIENCE"] = "kinetix",
        ["PORT"] = "8001",
        ["GRPC_PORT"] = "50055",
        ["PRICING_GRPC_URL"] = "https://kinetix-pricing-service:50054",
        ["MATCHING_GRPC_URL"] = "http://kinetix-matching-service:50053",
        ["CATALOG_GRPC_URL"] = "http://kinetix-catalog-grpc:50058",
        ["WAREHOUSE_GRPC_URL"] = "http://kinetix-warehouse-grpc:50051",
        ["IDENTITY_GRPC_URL"] = "http://kinetix-identity-service:50052",
        ["PAYMENT_GRPC_URL"] = "http://kinetix-payment-service:50056",
        ["KINETIX_GRPC_DEADLINE_SECONDS"] = "5",
        ["KINETIX_SHUTDOWN_TIMEOUT_SECONDS"] = "25",
    };

    private static IConfiguration From(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration LabWith(string setting, string? value) {
        var values = Lab();
        values[setting] = value;

        return From(values);
    }

    private static IConfiguration LabWithout(string setting) {
        var values = Lab();
        values.Remove(setting);

        return From(values);
    }

    private static void RefusedNaming(string setting, IConfiguration configuration) {
        var failure = Assert.Throws<InvalidOperationException>(() => OrderSettings.FromConfiguration(configuration));

        Assert.Contains(setting, failure.Message, StringComparison.Ordinal);
    }

    private static TheoryData<string, string> Pairs(string[] settings, string[] values) {
        var data = new TheoryData<string, string>();
        foreach (var setting in settings) {
            foreach (var value in values) {
                data.Add(setting, value);
            }
        }

        return data;
    }

    public static TheoryData<string> EverySetting() {
        string[] every = [.. Texts, .. Ports, .. Urls, .. Durations];

        return new TheoryData<string>(every);
    }

    public static TheoryData<string, string> UnusableUrls() => Pairs(
        Urls,
        [
            "kinetix-payment-service:50056",
            "grpc://kinetix-payment-service:50056",
            "/payment",
            "not a url",
        ]
    );

    public static TheoryData<string, string> UnusablePorts() => Pairs(
        Ports,
        ["0", "65536", "-1", "8001a", " 8001", "80.5"]
    );

    public static TheoryData<string, string> UnusableDurations() => Pairs(
        Durations,
        ["0", "-5", "5s", "soon", "1,5"]
    );

    [Fact]
    public void EverySettingIsReadAsConfigured() {
        var settings = OrderSettings.FromConfiguration(From(Lab()));

        Assert.Equal("kinetix-redis:6379,password=test-only", settings.RedisConnectionString);
        Assert.Equal("https://identity.kinetix.local", settings.JwtIssuer);
        Assert.Equal("kinetix", settings.JwtAudience);
        Assert.Equal(8001, settings.RestPort);
        Assert.Equal(50055, settings.GrpcPort);
        Assert.Equal(new Uri("https://kinetix-pricing-service:50054"), settings.PricingGrpcUrl);
        Assert.Equal(new Uri("http://kinetix-matching-service:50053"), settings.MatchingGrpcUrl);
        Assert.Equal(new Uri("http://kinetix-catalog-grpc:50058"), settings.CatalogGrpcUrl);
        Assert.Equal(new Uri("http://kinetix-warehouse-grpc:50051"), settings.WarehouseGrpcUrl);
        Assert.Equal(new Uri("http://kinetix-identity-service:50052"), settings.IdentityGrpcUrl);
        Assert.Equal(new Uri("http://kinetix-payment-service:50056"), settings.PaymentGrpcUrl);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.GrpcDeadline);
        Assert.Equal(TimeSpan.FromSeconds(25), settings.ShutdownTimeout);
    }

    [Fact]
    public void ADeadlineMayBeAFractionOfASecondAboveZero() {
        var settings = OrderSettings.FromConfiguration(LabWith("KINETIX_GRPC_DEADLINE_SECONDS", "2.5"));

        Assert.Equal(TimeSpan.FromSeconds(2.5), settings.GrpcDeadline);
    }

    [Theory]
    [MemberData(nameof(EverySetting))]
    public void AnUnsetSettingStopsTheServiceStartingAndNamesIt(string setting) {
        RefusedNaming(setting, LabWithout(setting));
    }

    [Theory]
    [MemberData(nameof(EverySetting))]
    public void AnEmptySettingStopsTheServiceStartingAndNamesIt(string setting) {
        RefusedNaming(setting, LabWith(setting, ""));
        RefusedNaming(setting, LabWith(setting, "   "));
    }

    [Theory]
    [MemberData(nameof(UnusableUrls))]
    public void AUrlThatIsNotHttpOrHttpsStopsTheServiceStarting(string setting, string value) {
        RefusedNaming(setting, LabWith(setting, value));
    }

    [Theory]
    [MemberData(nameof(UnusablePorts))]
    public void APortThatIsNotOneTo65535StopsTheServiceStarting(string setting, string value) {
        RefusedNaming(setting, LabWith(setting, value));
    }

    [Theory]
    [MemberData(nameof(UnusableDurations))]
    public void ADurationThatIsNotPositiveSecondsStopsTheServiceStarting(string setting, string value) {
        RefusedNaming(setting, LabWith(setting, value));
    }
}
