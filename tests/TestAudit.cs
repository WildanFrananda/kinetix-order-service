using Kinetix.OrderService.Infrastructure.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Prometheus;

namespace Kinetix.OrderService.Tests;

public static class TestAudit {
    public static AdminAudit Silent() => new(
        new KinetixMetrics(Metrics.WithCustomRegistry(Metrics.NewCustomRegistry()), BuildVersion.Of(null)),
        NullLogger<AdminAudit>.Instance
    );
}
