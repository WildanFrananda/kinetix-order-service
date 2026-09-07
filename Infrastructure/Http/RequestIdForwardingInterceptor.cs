using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Kinetix.OrderService.Infrastructure.Http;

public class RequestIdForwardingInterceptor(RequestIdAccessor requestIdAccessor) : Interceptor {
    public const string MetadataKey = "x-request-id";

    private readonly RequestIdAccessor _requestIdAccessor = requestIdAccessor;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation) {

        var requestId = _requestIdAccessor.Current;
        if (string.IsNullOrWhiteSpace(requestId)) {
            return continuation(request, context);
        }

        var headers = context.Options.Headers ?? [];
        if (headers.GetValue(MetadataKey) is null) {
            headers.Add(MetadataKey, requestId);
        }

        return continuation(
            request,
            new ClientInterceptorContext<TRequest, TResponse>(
                context.Method, context.Host, context.Options.WithHeaders(headers)
            )
        );
    }
}
