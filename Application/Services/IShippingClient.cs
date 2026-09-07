namespace Kinetix.OrderService.Application.Services;


public interface IShippingClient {
    Task<EstimateShippingResult> EstimateShippingOptionsAsync(double originLat, double originLng, double destLat, double destLng, double totalWeightKg, long? merchantId = null);
}
