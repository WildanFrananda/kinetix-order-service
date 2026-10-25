using Grpc.Core;
using Grpc.Core.Interceptors;
using Kinetix.OrderService.Infrastructure.Persistence;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public sealed class DatabaseOutageInterceptor(ILogger<DatabaseOutageInterceptor> logger) : Interceptor {
    public const string UnavailableDetail = "order could not reach its database; try again shortly";

    private readonly ILogger<DatabaseOutageInterceptor> _logger = logger;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation
    ) {
        try {
            return await continuation(request, context);
        } catch (Exception e) when (e is not RpcException && DatabaseOutage.Explains(e)) {
            _logger.LogError(e, "{Method} could not reach the database; answering Unavailable", context.Method);
            throw new RpcException(new Status(StatusCode.Unavailable, UnavailableDetail));
        }
    }
}
