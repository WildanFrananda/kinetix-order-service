using Kinetix.OrderService.Infrastructure.Migration;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class PrincipalBackfillPostgresTests {
    private static async Task ExecuteAsync(string connectionString, string sql) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static IConfiguration With(string? identityTarget) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["IDENTITY_GRPC_TARGET"] = identityTarget })
            .Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kinetix-identity-service:50052")]
    public async Task AccountsToResolveWithNoUsableIdentityTargetStopTheMigrationNamingIt(string? identityTarget) {
        if (PostgresDatabase.ConnectionString is not { } server) {
            return;
        }

        var scratch = $"kinetix_order_backfill_{Guid.NewGuid():N}";
        var legacy = new NpgsqlConnectionStringBuilder(server) { Database = scratch }.ConnectionString;

        await ExecuteAsync(server, $"create database {scratch}");

        try {
            await ExecuteAsync(
                legacy,
                """
                create table orders (customer_id bigint not null, "CustomerPrincipalId" text);
                insert into orders (customer_id, "CustomerPrincipalId") values (42, '');
                """
            );

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PrincipalBackfill.RunAsync(legacy, With(identityTarget))
            );

            Assert.Contains("IDENTITY_GRPC_TARGET", failure.Message, StringComparison.Ordinal);
        } finally {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(server, $"drop database {scratch} with (force)");
        }
    }

    [Fact]
    public async Task ASchemaWithNothingToBackfillNeedsNoIdentityTarget() {
        if (PostgresDatabase.ConnectionString is not { } server) {
            return;
        }

        var updated = await PrincipalBackfill.RunAsync(server, With(null));

        Assert.Equal(0, updated);
    }
}
