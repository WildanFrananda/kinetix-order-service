namespace Kinetix.OrderService.Tests;

public class RedisIsConfiguredTests {
    [Fact]
    public void CiRunsTheRedisTestsAgainstARealRedis() {
        if (Environment.GetEnvironmentVariable("CI") is null) {
            return;
        }

        Assert.False(
            CartRedisTests.RedisUrl is null,
            "CI is set but KINETIX_TEST_REDIS is not, "
          + "so the cart's compare-and-set would pass without asking Redis anything"
        );
    }
}
