using Kinetix.OrderService.Infrastructure.Lifecycle;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class ShutdownTimeoutTests {

    [Fact]
    public void TheDefaultLeavesRoomInsideTheContainersGracePeriod() {
        Assert.Equal(TimeSpan.FromSeconds(25), ShutdownTimeout.FromEnvironment(null));
        Assert.Equal(TimeSpan.FromSeconds(25), ShutdownTimeout.FromEnvironment("  "));
    }

    [Fact]
    public void AnOperatorCanChangeItWithTheGracePeriod() {
        Assert.Equal(TimeSpan.FromSeconds(45), ShutdownTimeout.FromEnvironment("45"));
    }

    [Theory]
    [InlineData("30s")]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("-5")]
    public void ABudgetThatIsNotANumberOfSecondsStopsTheService(string value) {
        Assert.Throws<InvalidOperationException>(() => ShutdownTimeout.FromEnvironment(value));
    }
}
