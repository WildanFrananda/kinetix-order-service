using System.Net.Sockets;
using Grpc.Core;
using Kinetix.OrderService.Infrastructure.Grpc;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Kinetix.OrderService.Tests;

public class DatabaseOutageTests {
    private static NpgsqlException Refused() =>
        new("Failed to connect to 127.0.0.1:5432", new SocketException((int)SocketError.ConnectionRefused));

    private static PostgresException ServerSaid(string sqlState) =>
        new("the server said so", "FATAL", "FATAL", sqlState);

    public static TheoryData<Exception> Outages() => [
        Refused(),
        new NpgsqlException("Exception while reading from stream", new TimeoutException("timed out")),
        new DbUpdateException("could not save", Refused()),
        new InvalidOperationException("wrapped", new DbUpdateException("could not save", Refused())),
        ServerSaid(PostgresErrorCodes.CannotConnectNow),
        ServerSaid(PostgresErrorCodes.AdminShutdown),
        ServerSaid(PostgresErrorCodes.TooManyConnections),
        ServerSaid(PostgresErrorCodes.ConnectionFailure),
    ];

    public static TheoryData<Exception> Faults() => [
        new InvalidOperationException("a defect"),
        new DbUpdateException("duplicate", ServerSaid(PostgresErrorCodes.UniqueViolation)),
        ServerSaid(PostgresErrorCodes.SerializationFailure),
        ServerSaid(PostgresErrorCodes.DeadlockDetected),
        ServerSaid(PostgresErrorCodes.UndefinedColumn),
        ServerSaid(PostgresErrorCodes.InvalidPassword),
        new NpgsqlException("Received unexpected backend message"),
    ];

    [Theory]
    [MemberData(nameof(Outages))]
    public void ADatabaseThatCouldNotBeAskedIsAnOutage(Exception failure) {
        Assert.True(DatabaseOutage.Explains(failure));
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public void ADatabaseThatAnsweredOrACodeDefectIsNotAnOutage(Exception failure) {
        Assert.False(DatabaseOutage.Explains(failure));
    }

    [Fact]
    public async Task AGrpcCallWhoseDatabaseCouldNotBeAskedIsUnavailableAndNamesNothingInternal() {
        var interceptor = new DatabaseOutageInterceptor(NullLogger<DatabaseOutageInterceptor>.Instance);

        var refusal = await Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<string, string>(
            "request",
            new TestCallContext("/order.v1.OrderService/GetOrderDetails"),
            (_, _) => throw Refused()
        ));

        Assert.Equal(StatusCode.Unavailable, refusal.StatusCode);
        Assert.Equal(DatabaseOutageInterceptor.UnavailableDetail, refusal.Status.Detail);
    }

    [Fact]
    public async Task AGrpcCallThatFailedForAnotherReasonIsLeftAsItFailed() {
        var interceptor = new DatabaseOutageInterceptor(NullLogger<DatabaseOutageInterceptor>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => interceptor.UnaryServerHandler<string, string>(
            "request",
            new TestCallContext("/order.v1.OrderService/GetOrderDetails"),
            (_, _) => throw new InvalidOperationException("a defect")
        ));
    }
}
