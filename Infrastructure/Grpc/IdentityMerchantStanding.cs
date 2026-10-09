using Grpc.Core;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using IdentityProto = global::Identity.V1;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class IdentityMerchantStanding(
    IdentityProto.IdentityService.IdentityServiceClient client
) : IMerchantStanding {
    private readonly IdentityProto.IdentityService.IdentityServiceClient _client = client;

    public async Task<bool> MaySellAsync(string merchantPrincipalId, CancellationToken cancellationToken = default) {
        try {
            var response = await _client.GetMerchantInfoAsync(
                new IdentityProto.GetMerchantInfoRequest { PrincipalId = merchantPrincipalId },
                cancellationToken: cancellationToken
            );

            return response.Found && response.MaySell;
        } catch (RpcException e) {
            throw new MerchantStandingUnknownException(merchantPrincipalId, e);
        }
    }
}
