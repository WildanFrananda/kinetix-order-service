using Common.V1;
using Grpc.Core;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Results;
using Shipping.V1;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class ShippingGrpcClient(
    ShippingService.ShippingServiceClient client,
    ILogger<ShippingGrpcClient> logger
) : IShippingClient {
    private readonly ShippingService.ShippingServiceClient _client = client;
    private readonly ILogger<ShippingGrpcClient> _logger = logger;

    public async Task<EstimateShippingResult> EstimateShippingOptionsAsync(
        double originLat,
        double originLng,
        double destLat,
        double destLng,
        long totalWeightGrams,
        string merchantPrincipalId
    ) {
        var request = new EstimateShippingOptionsRequest {
            Origin = new GeoPoint { Latitude = originLat, Longitude = originLng },
            Destination = new GeoPoint { Latitude = destLat, Longitude = destLng },
            TotalWeightGrams = totalWeightGrams,
            MerchantPrincipalId = merchantPrincipalId
        };

        EstimateShippingOptionsResponse response;
        try {
            response = await _client.EstimateShippingOptionsAsync(request);
        } catch (RpcException ex) {
            var deployFault = ex.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated;

            _logger.Log(
                deployFault ? LogLevel.Critical : LogLevel.Error, ex,
                "matching answered EstimateShippingOptions with {StatusCode}, so this checkout is "
              + "refused rather than shipped free; the shipping fee cannot be established from "
              + "here. {Interpretation}",
                ex.StatusCode,
                deployFault
                    ? "This is a misconfiguration, not an outage: order is off matching's caller "
                    + "allow list or the mesh certificates do not chain."
                    : "matching is unreachable, over its deadline, or faulting."
            );

            throw new ShippingUnavailableException(ex);
        } catch (Exception ex) {
            _logger.LogError(
                ex, "matching did not answer EstimateShippingOptions, so this checkout is refused "
                  + "rather than shipped free; the shipping fee cannot be established from here"
            );

            throw new ShippingUnavailableException(ex);
        }

        var options = response.Options.Select(opt => new ShippingOptionResult(
            opt.ServiceTier,
            opt.ServiceName,
            opt.DistanceKm,
            opt.EstimatedDeliveryTime,
            opt.IsAvailable,
            string.IsNullOrWhiteSpace(opt.UnavailableReason) ? null : opt.UnavailableReason
        )).ToList();

        return new EstimateShippingResult(response.DistanceKm, options);
    }
}
