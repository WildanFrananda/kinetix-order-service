namespace Kinetix.OrderService.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresDatabase> {
    public const string Name = "postgres";
}
