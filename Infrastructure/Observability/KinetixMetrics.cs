using System.Globalization;
using Prometheus;
using ServiceDescriptor = Google.Protobuf.Reflection.ServiceDescriptor;

namespace Kinetix.OrderService.Infrastructure.Observability;

public sealed class KinetixMetrics {
    public const string ServiceName = "kinetix-order-service";
    public const string MetricsPath = "/metrics";

    private readonly Counter _httpRequests;
    private readonly Histogram _httpRequestDuration;
    private readonly Counter _grpcServerCalls;
    private readonly Counter _grpcClientCalls;

    public KinetixMetrics(IMetricFactory factory, string version) {
        _httpRequests = factory.CreateCounter(
            "kinetix_http_requests_total",
            "HTTP requests served, by method, matched route template and response status.",
            new CounterConfiguration { LabelNames = ["method", "route", "status"] }
        );

        _httpRequestDuration = factory.CreateHistogram(
            "kinetix_http_request_duration_seconds",
            "Wall time spent serving an HTTP request, in seconds.",
            new HistogramConfiguration { LabelNames = ["method", "route"] }
        );

        _grpcServerCalls = factory.CreateCounter(
            "kinetix_grpc_server_calls_total",
            "gRPC calls served, by full method name and the status they ended with.",
            new CounterConfiguration { LabelNames = ["grpc_method", "grpc_code"] }
        );

        _grpcClientCalls = factory.CreateCounter(
            "kinetix_grpc_client_calls_total",
            "gRPC calls made to another service, by peer, full method name and status.",
            new CounterConfiguration { LabelNames = ["peer", "grpc_method", "grpc_code"] }
        );

        factory.CreateGauge(
            "kinetix_build_info",
            "Always 1. The labels are the payload: which service this is and which build.",
            new GaugeConfiguration { LabelNames = ["service", "version"] }
        ).WithLabels(ServiceName, version).Set(1);

        _httpRequests.WithLabels(HttpMethodLabel.Of(HttpMethods.Get), MetricsPath, "200");
        _httpRequestDuration.WithLabels(HttpMethodLabel.Of(HttpMethods.Get), MetricsPath);
    }

    public void ObserveHttp(string method, string route, int status, double seconds) {
        _httpRequests.WithLabels(method, route, status.ToString(CultureInfo.InvariantCulture)).Inc();
        _httpRequestDuration.WithLabels(method, route).Observe(seconds);
    }

    public void ObserveGrpcServer(string grpcMethod, string grpcCode) {
        _grpcServerCalls.WithLabels(grpcMethod, grpcCode).Inc();
    }

    public void ObserveGrpcClient(string peer, string grpcMethod, string grpcCode) {
        _grpcClientCalls.WithLabels(peer, grpcMethod, grpcCode).Inc();
    }

    public void RegisterGrpcServerSurface(ServiceDescriptor surface) {
        foreach (var method in FullMethodNames(surface)) {
            _grpcServerCalls.WithLabels(method, GrpcCodeLabel.Ok);
        }
    }

    public void RegisterGrpcClientSurface(string peer, ServiceDescriptor surface) {
        foreach (var method in FullMethodNames(surface)) {
            _grpcClientCalls.WithLabels(peer, method, GrpcCodeLabel.Ok);
        }
    }

    private static IEnumerable<string> FullMethodNames(ServiceDescriptor surface) {
        return surface.Methods.Select(method => $"/{surface.FullName}/{method.Name}");
    }
}
