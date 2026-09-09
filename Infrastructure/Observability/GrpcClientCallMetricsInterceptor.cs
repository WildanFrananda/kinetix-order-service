using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Kinetix.OrderService.Infrastructure.Observability;

public class GrpcClientCallMetricsInterceptor(string peer, KinetixMetrics metrics) : Interceptor {
    private readonly string _peer = peer;
    private readonly KinetixMetrics _metrics = metrics;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation
    ) {
        var call = continuation(request, context);

        return new AsyncUnaryCall<TResponse>(
            CountAsync(call.ResponseAsync, context.Method.FullName),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose
        );
    }

    private async Task<TResponse> CountAsync<TResponse>(Task<TResponse> call, string method) {
        try {
            var response = await call;
            _metrics.ObserveGrpcClient(_peer, method, GrpcCodeLabel.Ok);
            return response;
        } catch (RpcException e) {
            _metrics.ObserveGrpcClient(_peer, method, GrpcCodeLabel.Of(e.StatusCode));
            throw;
        } catch {
            _metrics.ObserveGrpcClient(_peer, method, GrpcCodeLabel.Unknown);
            throw;
        }
    }
}
