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

    private const long MinorPerMajor = 100L;

    private const string Currency = "IDR";

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

        var foreignCurrency = response.Options
            .Where(o => o.IsAvailable
                     && o.BaseShippingFee is not null
                     && o.BaseShippingFee.Currency != Currency)
            .Select(o => $"{o.ServiceTier} priced in '{o.BaseShippingFee?.Currency}'")
            .ToList();

        if (foreignCurrency.Count > 0) {
            _logger.LogError(
                "matching quoted an available tier in a currency order cannot debit ({Quotes}); "
              + "order prices in {Currency} only and converts nothing, so this quote is refused "
              + "rather than read as a number of rupiah",
                string.Join(", ", foreignCurrency), Currency
            );

            throw new ShippingQuoteMalformedException(
                $"available options quoted in a currency order cannot debit: {string.Join(", ", foreignCurrency)}"
            );
        }

        var options = response.Options.Select(opt => new ShippingOptionResult(
            opt.ServiceTier,
            opt.ServiceName,
            opt.DistanceKm,
            opt.BaseShippingFee is null ? null : (decimal)opt.BaseShippingFee.AmountMinor / MinorPerMajor,
            opt.EstimatedDeliveryTime,
            opt.IsAvailable,
            string.IsNullOrWhiteSpace(opt.UnavailableReason) ? null : opt.UnavailableReason
        )).ToList();

        return new EstimateShippingResult(response.DistanceKm, options);
    }
}
