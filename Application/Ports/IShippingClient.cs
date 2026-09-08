using Kinetix.OrderService.Application.Results;

namespace Kinetix.OrderService.Application.Ports;


public interface IShippingClient {
    Task<EstimateShippingResult> EstimateShippingOptionsAsync(
        double originLat,
        double originLng,
        double destLat,
        double destLng,
        long totalWeightGrams,
        string merchantPrincipalId
    );
}
