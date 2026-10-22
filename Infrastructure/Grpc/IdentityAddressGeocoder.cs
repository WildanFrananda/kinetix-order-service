using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Results;
using CommonProto = global::Common.V1;
using GeoProto = global::Geo.V1;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class IdentityAddressGeocoder(
    GeoProto.GeocodingService.GeocodingServiceClient client,
    GeocodeDeadline deadline,
    ILogger<IdentityAddressGeocoder> logger
) : IAddressGeocoder {
    private readonly GeoProto.GeocodingService.GeocodingServiceClient _client = client;
    private readonly GeocodeDeadline _deadline = deadline;
    private readonly ILogger<IdentityAddressGeocoder> _logger = logger;

    public async Task<AddressPlacement> PlaceAsync(string address, CancellationToken cancellationToken = default) {
        GeoProto.GeocodeAddressResponse response;

        try {
            response = await _client.GeocodeAddressAsync(
                new GeoProto.GeocodeAddressRequest {
                    Address = new CommonProto.Address { StreetAddress = address }
                },
                deadline: DateTime.UtcNow.Add(_deadline.Length),
                cancellationToken: cancellationToken
            );
        } catch (Exception e) {
            throw new AddressPlacementUnavailableException("identity could not be asked to place it", e);
        }

        if (!response.Geocoded) {
            return Refusal(response);
        }

        if (response.Location is null) {
            throw new AddressPlacementUnavailableException("identity said it placed the address and sent no point");
        }

        var point = new MapPoint(response.Location.Latitude, response.Location.Longitude);

        return response.Precision switch {
            GeoProto.GeocodePrecision.Rooftop
                or GeoProto.GeocodePrecision.Street
                or GeoProto.GeocodePrecision.Suburb => AddressPlacement.Placed(point),
            _ => TooVague(response.Precision)
        };
    }

    private AddressPlacement Refusal(GeoProto.GeocodeAddressResponse response) {
        var failure = response.Failure switch {
            GeoProto.GeocodeFailure.NotFound => AddressPlacementFailure.NotFound,
            GeoProto.GeocodeFailure.Ambiguous => AddressPlacementFailure.Ambiguous,
            GeoProto.GeocodeFailure.OutOfServiceArea => AddressPlacementFailure.OutOfServiceArea,
            GeoProto.GeocodeFailure.InsufficientAddress => AddressPlacementFailure.Insufficient,
            GeoProto.GeocodeFailure.ProviderUnavailable => throw new AddressPlacementUnavailableException(
                $"identity's geocoder is unavailable: {response.FailureDetail}"
            ),
            _ => throw new AddressPlacementUnavailableException(
                $"identity declined to place the address without saying why ({response.Failure}): "
                    + response.FailureDetail
            )
        };

        _logger.LogInformation(
            "identity could not place a shipping address ({Failure}): {Detail}",
            failure,
            response.FailureDetail
        );

        return AddressPlacement.Refused(failure, response.FailureDetail);
    }

    private AddressPlacement TooVague(GeoProto.GeocodePrecision precision) {
        _logger.LogInformation(
            "identity placed a shipping address only to {Precision} precision, which is no point a courier can find",
            precision
        );

        return AddressPlacement.Refused(
            AddressPlacementFailure.TooVague,
            $"the address was placed only to {precision} precision; give a street"
        );
    }
}
