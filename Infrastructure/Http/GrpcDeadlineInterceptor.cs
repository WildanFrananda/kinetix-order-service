using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Kinetix.OrderService.Infrastructure.Http;

public class GrpcDeadlineInterceptor(TimeSpan deadline) : Interceptor {
    private readonly TimeSpan _deadline = deadline;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation) {

        if (context.Options.Deadline is not null) {
            return continuation(request, context);
        }

        return continuation(
            request,
            new ClientInterceptorContext<TRequest, TResponse>(
                context.Method,
                context.Host,
                context.Options.WithDeadline(DateTime.UtcNow.Add(_deadline))
            )
        );
    }
}
