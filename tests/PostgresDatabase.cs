using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Tests;

public sealed class PostgresDatabase : IAsyncLifetime {
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable("KINETIX_TEST_POSTGRES");

    public static OrderDbContext NewDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(connectionString)
            .Options
        );

    public async Task InitializeAsync() {
        if (ConnectionString is null) {
            return;
        }

        await using var db = NewDbContext(ConnectionString);
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
