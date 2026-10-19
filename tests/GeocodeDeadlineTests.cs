using Kinetix.OrderService.Infrastructure.Grpc;
using Microsoft.Extensions.Configuration;

namespace Kinetix.OrderService.Tests;

public class GeocodeDeadlineTests {
    private static IConfiguration With(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [GeocodeDeadline.Setting] = value })
            .Build();

    [Fact]
    public void ADeadlineIsReadInWholeSeconds() {
        Assert.Equal(TimeSpan.FromSeconds(10), GeocodeDeadline.FromConfiguration(With("10")).Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("2.5")]
    [InlineData("ten")]
    public void AMissingOrUnusableDeadlineStopsTheServiceStarting(string? value) {
        Assert.Throws<InvalidOperationException>(() => GeocodeDeadline.FromConfiguration(With(value)));
    }
}
