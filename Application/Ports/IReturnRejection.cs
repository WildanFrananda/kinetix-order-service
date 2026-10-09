using Kinetix.OrderService.Application.Returns;

namespace Kinetix.OrderService.Application.Ports;

public interface IReturnRejection {
    Task<ReturnRejectionStatus> RejectAsync(string returnNumber, string reason, CancellationToken cancellationToken);
}
