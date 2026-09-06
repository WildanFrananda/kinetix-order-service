using Common.V1;
using Shipping.V1;

namespace Kinetix.OrderService.Application.Services;

public class ShippingGrpcClient(ShippingService.ShippingServiceClient client) : IShippingClient {
    private readonly ShippingService.ShippingServiceClient _client = client;

    public async Task<EstimateShippingResult> EstimateShippingOptionsAsync(double originLat, double originLng, double destLat, double destLng, double totalWeightKg, long? merchantId = null) {
        try {
            var request = new EstimateShippingOptionsRequest {
                Origin = new GeoPoint { Latitude = originLat, Longitude = originLng },
                Destination = new GeoPoint { Latitude = destLat, Longitude = destLng },
                TotalWeightGrams = (long)Math.Round(totalWeightKg * 1000.0),
                MerchantPrincipalId = string.Empty
            };

            var response = await _client.EstimateShippingOptionsAsync(request);

            var options = response.Options.Select(opt => new ShippingOptionResult(
                opt.ServiceTier,
                opt.ServiceName,
                opt.DistanceKm,
                opt.BaseShippingFee == null ? 0m : (decimal)opt.BaseShippingFee.AmountMinor / 100m,
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
