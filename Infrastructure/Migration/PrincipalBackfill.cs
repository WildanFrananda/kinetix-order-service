using Grpc.Core;
using Grpc.Net.Client;
using Identity.V1;
using Npgsql;
using Kinetix.OrderService.Security;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Kinetix.OrderService.Infrastructure.Migration;

public static class PrincipalBackfill {

    public static async Task<int> RunAsync(string connectionString) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        if (!await ColumnExistsAsync(connection, "orders", "customer_id")) {
            Console.WriteLine("customer_id is already gone; no principals to backfill");
            return 0;
        }

        var pending = new List<long>();
        await using (var read = new NpgsqlCommand(
            """
            select distinct customer_id from orders
            where customer_id > 0 and coalesce("CustomerPrincipalId", '') = ''
            """, connection)) {
            await using var rows = await read.ExecuteReaderAsync();
            while (await rows.ReadAsync()) {
                pending.Add(rows.GetInt64(0));
            }
        }

        if (pending.Count == 0) {
            Console.WriteLine("every order already carries a principal");
            return 0;
        }

        Console.WriteLine($"{pending.Count} account(s) to resolve through identity");

        using var channel = OpenChannel();
        var identity = new IdentityService.IdentityServiceClient(channel);

        var updated = 0;
        foreach (var accountId in pending) {
            var response = await identity.ResolvePrincipalAsync(new ResolvePrincipalRequest {
                ServiceLocalId = new ServiceLocalId {
                    Service = "identity",
                    LocalId = accountId.ToString(),
                },
            }, deadline: DateTime.UtcNow.AddSeconds(15));

            if (!response.Found || string.IsNullOrWhiteSpace(response.PrincipalId)) {
                throw new InvalidOperationException(
                    $"identity does not know account {accountId}, so the orders placed under it "
                    + "cannot be attributed to anyone. Backfill stopped before the migration ran.");
            }

            await using var write = new NpgsqlCommand(
                """
                update orders set "CustomerPrincipalId" = @principal
                where customer_id = @account and coalesce("CustomerPrincipalId", '') = ''
                """, connection);
            write.Parameters.AddWithValue("principal", response.PrincipalId);
            write.Parameters.AddWithValue("account", accountId);
            updated += await write.ExecuteNonQueryAsync();
        }

        Console.WriteLine($"gave {updated} order(s) the principal of {pending.Count} account(s)");
        return updated;
    }

    private static async Task<bool> ColumnExistsAsync(NpgsqlConnection connection, string table, string column) {
        await using var command = new NpgsqlCommand(
            """
            select 1 from information_schema.columns
            where table_schema = 'public' and table_name = @table and column_name = @column
            """, connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static GrpcChannel OpenChannel() {
        var target = Environment.GetEnvironmentVariable("IDENTITY_GRPC_TARGET")
            ?? "https://kinetix-identity-service:50052";

        var identity = ServiceIdentity.Load();

        var handler = new SocketsHttpHandler {
            SslOptions = new SslClientAuthenticationOptions {
                ClientCertificates = new X509Certificate2Collection(identity.Leaf),
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is X509Certificate2 peer && identity.IsIssuedByOurCa(peer),
            },
        };

        return GrpcChannel.ForAddress(target, new GrpcChannelOptions {
            HttpHandler = handler,
            Credentials = ChannelCredentials.SecureSsl,
        });
    }
}
