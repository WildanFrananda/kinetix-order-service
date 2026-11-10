using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Kinetix.OrderService.Tests;

public sealed class ScratchTransaction : IAsyncDisposable {
    private ScratchTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction) {
        Connection = connection;
        Transaction = transaction;
    }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction { get; }

    public static async Task<ScratchTransaction> BeginAsync(string connectionString) {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        return new ScratchTransaction(connection, await connection.BeginTransactionAsync());
    }

    public async Task<OrderDbContext> ContextAsync(params IInterceptor[] interceptors) {
        var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(Connection)
            .AddInterceptors(interceptors)
            .Options
        );
        await db.Database.UseTransactionAsync(Transaction);

        return db;
    }

    public NpgsqlCommand Command(string sql) => new(sql, Connection, Transaction);

    public async Task ExecuteAsync(string sql) {
        await using var command = Command(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() {
        await Transaction.RollbackAsync();
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
