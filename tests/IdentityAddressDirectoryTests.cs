using Grpc.Core;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Infrastructure.Grpc;
using Microsoft.Extensions.Logging.Abstractions;
using IdentityProto = global::Identity.V1;

namespace Kinetix.OrderService.Tests;

public class IdentityAddressDirectoryTests {
    private const string Principal = "11111111-1111-1111-1111-111111111111";

    private sealed class Identity(Func<object> answer) : IdentityProto.IdentityService.IdentityServiceClient {
        public override AsyncUnaryCall<IdentityProto.GetMerchantInfoResponse> GetMerchantInfoAsync(
            IdentityProto.GetMerchantInfoRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default
        ) => Call((IdentityProto.GetMerchantInfoResponse)answer());

        public override AsyncUnaryCall<IdentityProto.GetUserProfileResponse> GetUserProfileAsync(
            IdentityProto.GetUserProfileRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default
        ) => Call((IdentityProto.GetUserProfileResponse)answer());

        private static AsyncUnaryCall<T> Call<T>(T response) => new(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { }
        );
    }

    private static IdentityAddressDirectory Directory(Func<object> answer) =>
        new(new Identity(answer), NullLogger<IdentityAddressDirectory>.Instance);

    private static object Unavailable() => throw new RpcException(new Status(StatusCode.Unavailable, "down"));

    [Fact]
    public async Task AStoreIdentityCannotBeAskedAboutIsUnknownNotAbsent() {
        await Assert.ThrowsAsync<AddressUnknownException>(
            () => Directory(Unavailable).PickupPointAsync(Principal)
        );
    }

    [Fact]
    public async Task ACustomerIdentityCannotBeAskedAboutIsUnknownNotAbsent() {
        await Assert.ThrowsAsync<AddressUnknownException>(
            () => Directory(Unavailable).DeliveryPointAsync(Principal)
        );
    }

    [Fact]
    public async Task AStoreIdentityHasNeverPlacedIsAbsent() {
        var point = await Directory(() => new IdentityProto.GetMerchantInfoResponse { Found = true })
            .PickupPointAsync(Principal);

        Assert.Null(point);
    }

    [Fact]
    public async Task AFaultBeforeIdentityIsReachedIsUnknownNotAbsent() {
        static object MeshRefusing() =>
            throw new InvalidOperationException("the mesh handler has no client certificate");

        await Assert.ThrowsAsync<AddressUnknownException>(() => Directory(MeshRefusing).PickupPointAsync(Principal));
        await Assert.ThrowsAsync<AddressUnknownException>(() => Directory(MeshRefusing).DeliveryPointAsync(Principal));
    }
}
