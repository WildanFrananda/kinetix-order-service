using Grpc.Core;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Infrastructure.Grpc;
using Microsoft.Extensions.Logging.Abstractions;
using CommonProto = global::Common.V1;
using GeoProto = global::Geo.V1;

namespace Kinetix.OrderService.Tests;

public class IdentityAddressGeocoderTests {
    private sealed class Geocoding(Func<GeoProto.GeocodeAddressResponse> answer) : GeoProto.GeocodingService.GeocodingServiceClient {
        public GeoProto.GeocodeAddressRequest? LastRequest { get; private set; }

        public DateTime? LastDeadline { get; private set; }

        public override AsyncUnaryCall<GeoProto.GeocodeAddressResponse> GeocodeAddressAsync(
            GeoProto.GeocodeAddressRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default
        ) {
            LastRequest = request;
            LastDeadline = deadline;

            return new AsyncUnaryCall<GeoProto.GeocodeAddressResponse>(
                Task.FromResult(answer()),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }
            );
        }
    }

    private static IdentityAddressGeocoder Geocoder(Geocoding client) =>
        new(client, new GeocodeDeadline(10), NullLogger<IdentityAddressGeocoder>.Instance);

    private static GeoProto.GeocodeAddressResponse PlacedAt(GeoProto.GeocodePrecision precision) => new() {
        Geocoded = true,
        Location = new CommonProto.GeoPoint { Latitude = -6.2088, Longitude = 106.8456 },
        Precision = precision,
    };

    private static GeoProto.GeocodeAddressResponse Refused(GeoProto.GeocodeFailure failure) => new() {
        Geocoded = false,
        Failure = failure,
        FailureDetail = "identity's detail",
    };

    [Theory]
    [InlineData(GeoProto.GeocodePrecision.Rooftop)]
    [InlineData(GeoProto.GeocodePrecision.Street)]
    [InlineData(GeoProto.GeocodePrecision.Suburb)]
    public async Task AnAddressPlacedCloselyEnoughIsAPoint(GeoProto.GeocodePrecision precision) {
        var placement = await Geocoder(new Geocoding(() => PlacedAt(precision))).PlaceAsync("Jl. Sudirman No. 45, Jakarta");

        Assert.NotNull(placement.Point);
        Assert.Equal(-6.2088, placement.Point.Latitude);
        Assert.Null(placement.Failure);
    }

    [Theory]
    [InlineData(GeoProto.GeocodePrecision.City)]
    [InlineData(GeoProto.GeocodePrecision.Unspecified)]
    public async Task AnAddressPlacedOnlyToATownIsTooVagueToDeliverTo(GeoProto.GeocodePrecision precision) {
        var placement = await Geocoder(new Geocoding(() => PlacedAt(precision))).PlaceAsync("Jakarta");

        Assert.Null(placement.Point);
        Assert.Equal(AddressPlacementFailure.TooVague, placement.Failure);
    }

    [Theory]
    [InlineData(GeoProto.GeocodeFailure.NotFound, AddressPlacementFailure.NotFound)]
    [InlineData(GeoProto.GeocodeFailure.Ambiguous, AddressPlacementFailure.Ambiguous)]
    [InlineData(GeoProto.GeocodeFailure.OutOfServiceArea, AddressPlacementFailure.OutOfServiceArea)]
    [InlineData(GeoProto.GeocodeFailure.InsufficientAddress, AddressPlacementFailure.Insufficient)]
    public async Task AnAddressIdentityRefusesIsRefusedForItsReason(
        GeoProto.GeocodeFailure failure,
        AddressPlacementFailure expected
    ) {
        var placement = await Geocoder(new Geocoding(() => Refused(failure))).PlaceAsync("Somewhere");

        Assert.Null(placement.Point);
        Assert.Equal(expected, placement.Failure);
        Assert.Equal("identity's detail", placement.Detail);
    }

    [Theory]
    [InlineData(GeoProto.GeocodeFailure.ProviderUnavailable)]
    [InlineData(GeoProto.GeocodeFailure.Unspecified)]
    public async Task AGeocoderThatCannotAnswerIsUnavailableNotABadAddress(GeoProto.GeocodeFailure failure) {
        await Assert.ThrowsAsync<AddressPlacementUnavailableException>(
            () => Geocoder(new Geocoding(() => Refused(failure))).PlaceAsync("Jl. Sudirman No. 45, Jakarta")
        );
    }

    [Fact]
    public async Task IdentityThatCannotBeAskedIsUnavailableNotABadAddress() {
        var down = new Geocoding(() => throw new RpcException(new Status(StatusCode.Unavailable, "down")));

        await Assert.ThrowsAsync<AddressPlacementUnavailableException>(
            () => Geocoder(down).PlaceAsync("Jl. Sudirman No. 45, Jakarta")
        );
    }

    [Fact]
    public async Task APlacedAnswerWithNoPointIsUnavailableNotAPoint() {
        var hollow = new Geocoding(() => new GeoProto.GeocodeAddressResponse { Geocoded = true });

        await Assert.ThrowsAsync<AddressPlacementUnavailableException>(
            () => Geocoder(hollow).PlaceAsync("Jl. Sudirman No. 45, Jakarta")
        );
    }

    [Fact]
    public async Task TheWholeAddressIsAskedFor_UnderItsOwnDeadline() {
        var client = new Geocoding(() => PlacedAt(GeoProto.GeocodePrecision.Street));
        var before = DateTime.UtcNow;

        await Geocoder(client).PlaceAsync("Jl. Sudirman No. 45, Jakarta");

        Assert.Equal("Jl. Sudirman No. 45, Jakarta", client.LastRequest!.Address.StreetAddress);
        Assert.NotNull(client.LastDeadline);
        Assert.InRange(client.LastDeadline!.Value, before.AddSeconds(9), DateTime.UtcNow.AddSeconds(10));
    }
}
