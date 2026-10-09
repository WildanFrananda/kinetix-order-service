using Kinetix.OrderService.Application.Completion;
using Microsoft.Extensions.Configuration;

namespace Kinetix.OrderService.Tests;

public class ReturnWindowTests {
    private static IConfiguration With(string? days) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ReturnWindow.Setting] = days })
            .Build();

    [Fact]
    public void TheWindowIsReadInDays() {
        var window = ReturnWindow.FromConfiguration(With("7"));

        Assert.Equal(TimeSpan.FromDays(7), window.Length);
        Assert.Equal(
            new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc),
            window.ClosesAt(new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc))
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("7d")]
    [InlineData(" 7")]
    [InlineData("1.5")]
    public void AMissingOrUnusableWindowStopsTheServiceStarting(string? days) {
        var failure = Assert.Throws<InvalidOperationException>(() => ReturnWindow.FromConfiguration(With(days)));

        Assert.Contains(ReturnWindow.Setting, failure.Message, StringComparison.Ordinal);
    }
}
