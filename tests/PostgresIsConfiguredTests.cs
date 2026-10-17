namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class PostgresIsConfiguredTests {
    [Fact]
    public void CiRunsThePostgresTestsAgainstARealDatabase() {
        if (Environment.GetEnvironmentVariable("CI") is null) {
            return;
        }

        Assert.False(
            PostgresDatabase.ConnectionString is null,
            "CI is set but KINETIX_TEST_POSTGRES is not, "
          + "so every Postgres test would pass without asking Postgres anything"
        );
    }
}
