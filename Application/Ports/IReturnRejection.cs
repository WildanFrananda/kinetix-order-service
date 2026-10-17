using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.Application.Lifecycle;

namespace Kinetix.OrderService.Application.Ports;

public interface IReturnRejection {
    Task<ReturnRejectionStatus> RejectAsync(
        string returnNumber, string reason, LifecycleActor actor, CancellationToken cancellationToken
    );
}
