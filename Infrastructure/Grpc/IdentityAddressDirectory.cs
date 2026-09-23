using Grpc.Core;
using Kinetix.OrderService.Application.Ports;
using IdentityProto = global::Identity.V1;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class IdentityAddressDirectory(
    IdentityProto.IdentityService.IdentityServiceClient client,
    ILogger<IdentityAddressDirectory> logger
) : IAddressDirectory {
    private readonly IdentityProto.IdentityService.IdentityServiceClient _client = client;
    private readonly ILogger<IdentityAddressDirectory> _logger = logger;

    public async Task<MapPoint?> PickupPointAsync(string merchantPrincipalId) {
        if (string.IsNullOrWhiteSpace(merchantPrincipalId)) {
            return null;
        }

        try {
            var response = await _client.GetMerchantInfoAsync(
                new IdentityProto.GetMerchantInfoRequest { PrincipalId = merchantPrincipalId }
            );

            if (!response.Found) {
                _logger.LogWarning(
                    "identity knows no merchant {Principal}, so there is nowhere to collect from",
                    merchantPrincipalId
                );
                return null;
            }

            if (response.Location is null) {
                _logger.LogWarning(
                    "identity has not placed merchant {Principal}'s store address on a map, so no "
                  + "courier can be sent to it",
                    merchantPrincipalId
                );
                return null;
            }

            return new MapPoint(response.Location.Latitude, response.Location.Longitude);
        } catch (RpcException e) {
            _logger.LogError(
                e, "identity could not be asked where merchant {Principal} collects from",
                merchantPrincipalId
            );
            return null;
        }
    }

    public async Task<MapPoint?> DeliveryPointAsync(string customerPrincipalId) {
        if (string.IsNullOrWhiteSpace(customerPrincipalId)) {
            return null;
        }

        try {
            var response = await _client.GetUserProfileAsync(
                new IdentityProto.GetUserProfileRequest { PrincipalId = customerPrincipalId }
            );

            if (!response.Found || response.Location is null) {
                _logger.LogWarning(
                    "identity has not placed customer {Principal}'s address on a map, so no courier "
                  + "can be sent to it",
                    customerPrincipalId
                );
                return null;
            }

            return new MapPoint(response.Location.Latitude, response.Location.Longitude);
        } catch (RpcException e) {
            _logger.LogError(
                e, "identity could not be asked where customer {Principal} lives", customerPrincipalId
            );
            return null;
        }
    }
}
