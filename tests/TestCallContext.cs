using Grpc.Core;

namespace Kinetix.OrderService.Tests;

public sealed class TestCallContext(string method) : ServerCallContext {
    private readonly Metadata _responseTrailers = [];

    protected override string MethodCore => method;
    protected override string HostCore => "localhost";
    protected override string PeerCore => "ipv4:127.0.0.1:50000";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => [];
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore => _responseTrailers;
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new(null, []);

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotSupportedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
