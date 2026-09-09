using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Kinetix.OrderService.Infrastructure.Observability;

public sealed class GrpcServerCallMetricsInterceptor(KinetixMetrics metrics) : Interceptor {
    private readonly KinetixMetrics _metrics = metrics;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation
    ) {
        try {
            var response = await continuation(request, context);
            Count(context);
            return response;
        } catch (Exception e) {
            Count(context, e);
            throw;
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation
    ) {
        try {
            var response = await continuation(requestStream, context);
            Count(context);
            return response;
        } catch (Exception e) {
            Count(context, e);
            throw;
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation
    ) {
        try {
            await continuation(request, responseStream, context);
            Count(context);
        } catch (Exception e) {
            Count(context, e);
            throw;
        }
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation
    ) {
        try {
            await continuation(requestStream, responseStream, context);
            Count(context);
        } catch (Exception e) {
            Count(context, e);
            throw;
        }
    }

    private void Count(ServerCallContext context) {
        _metrics.ObserveGrpcServer(context.Method, GrpcCodeLabel.Of(context.Status.StatusCode));
    }

    private void Count(ServerCallContext context, Exception failure) {
        var code = failure is RpcException rpc
            ? GrpcCodeLabel.Of(rpc.StatusCode)
            : GrpcCodeLabel.Unknown;

        _metrics.ObserveGrpcServer(context.Method, code);
    }
}
