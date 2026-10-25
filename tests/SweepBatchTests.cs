using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Infrastructure.Background;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Kinetix.OrderService.Tests;

public class SweepBatchTests {
    private static readonly string[] Settings = [
        "KINETIX_SAGA_SWEEP_BATCH",
        "KINETIX_SETTLEMENT_SWEEP_BATCH",
        "KINETIX_DISPATCH_SWEEP_BATCH",
        "KINETIX_COMPLETION_SWEEP_BATCH",
    ];

    private static readonly string?[] Unusable = [null, "", "  ", "0", "-20", "2.5", "twenty", " 20"];

    public static TheoryData<string, string?> UnusableBatches() {
        var data = new TheoryData<string, string?>();
        foreach (var setting in Settings) {
            foreach (var value in Unusable) {
                data.Add(setting, value);
            }
        }

        return data;
    }

    public static TheoryData<string> EveryBatch() => new(Settings);

    private static IConfiguration With(string setting, string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [setting] = value })
            .Build();

    private static BackgroundService SweeperReading(string setting, IConfiguration configuration) {
        var scopes = new Mock<IServiceScopeFactory>().Object;

        return setting switch {
            "KINETIX_SAGA_SWEEP_BATCH" => new StuckSagaSweeper(
                scopes,
                configuration,
                NullLogger<StuckSagaSweeper>.Instance
            ),
            "KINETIX_SETTLEMENT_SWEEP_BATCH" => new ShippingSettlementSweeper(
                scopes,
                configuration,
                NullLogger<ShippingSettlementSweeper>.Instance
            ),
            "KINETIX_DISPATCH_SWEEP_BATCH" => new CourierDispatchSweeper(
                scopes,
                configuration,
                NullLogger<CourierDispatchSweeper>.Instance
            ),
            "KINETIX_COMPLETION_SWEEP_BATCH" => new OrderCompletionSweeper(
                scopes,
                new ReturnWindow(7),
                configuration,
                NullLogger<OrderCompletionSweeper>.Instance
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "no sweeper reads this setting"),
        };
    }

    [Theory]
    [MemberData(nameof(EveryBatch))]
    public void ASweeperStartsWithTheBatchItIsGiven(string setting) {
        Assert.NotNull(SweeperReading(setting, With(setting, "20")));
    }

    [Theory]
    [MemberData(nameof(UnusableBatches))]
    public void AMissingOrUnusableBatchStopsTheServiceStarting(string setting, string? value) {
        var failure = Assert.Throws<InvalidOperationException>(() => SweeperReading(setting, With(setting, value)));

        Assert.Contains(setting, failure.Message, StringComparison.Ordinal);
    }
}
