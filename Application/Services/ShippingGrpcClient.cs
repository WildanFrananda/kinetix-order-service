using Kinetix.OrderService.Grpc.Shipping;

namespace Kinetix.OrderService.Application.Services;

public class ShippingGrpcClient(ShippingService.ShippingServiceClient client) : IShippingClient {
    private readonly ShippingService.ShippingServiceClient _client = client;

    public async Task<EstimateShippingResult> EstimateShippingOptionsAsync(double originLat, double originLng, double destLat, double destLng, double totalWeightKg, long? merchantId = null) {
        try {
            var request = new EstimateShippingOptionsRequest {
                Origin = new LocationCoordinates { Latitude = originLat, Longitude = originLng },
                Destination = new LocationCoordinates { Latitude = destLat, Longitude = destLng },
                TotalWeightKg = totalWeightKg,
                MerchantId = merchantId ?? 0
            };

            var response = await _client.EstimateShippingOptionsAsync(request);

            var options = response.Options.Select(opt => new ShippingOptionResult(
                opt.ServiceTier,
                opt.ServiceName,
                opt.DistanceKm,
                (decimal)opt.BaseShippingFee,
                opt.EstimatedDeliveryTime,
                opt.IsAvailable,
                opt.UnavailableReason
            )).ToList();

            return new EstimateShippingResult(response.DistanceKm, options);
        } catch {
            return new EstimateShippingResult(0, []);
        }
    }
}
