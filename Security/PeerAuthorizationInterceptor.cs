using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Http.Features;

namespace Kinetix.OrderService.Security;

public sealed class PeerAuthorizationInterceptor : Interceptor {
    private readonly HashSet<string> _allowed;
    private readonly ILogger<PeerAuthorizationInterceptor> _log;

    public PeerAuthorizationInterceptor(IConfiguration config, ILogger<PeerAuthorizationInterceptor> log) {
        _log = log;

        var raw = config["KINETIX_GRPC_ALLOWED_PEERS"]
            ?? throw new InvalidOperationException(
                "KINETIX_GRPC_ALLOWED_PEERS is required and has no default.");

        _allowed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .ToHashSet(StringComparer.Ordinal);

        if (_allowed.Count == 0) {
            throw new InvalidOperationException("KINETIX_GRPC_ALLOWED_PEERS is set but names no services.");
        }
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation) {
        Authorize(context);
        return await continuation(request, context);
    }

    private void Authorize(ServerCallContext context) {
        var http = context.GetHttpContext();
        X509Certificate2? peer = http.Connection.ClientCertificate ?? throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "a client certificate carrying a SPIFFE identity is required"));
        var service = SpiffePeer.ServiceOf(peer) ?? throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "the client certificate carries no SPIFFE identity in this trust domain"));
        if (!_allowed.Contains(service)) {
            _log.LogWarning("refused a gRPC call from {Peer}, which is not on the allow list", service);
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                "this service is not permitted to call order"));
        }
    }
}
