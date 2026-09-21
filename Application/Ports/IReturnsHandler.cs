using Kinetix.OrderService.Application.Returns;

namespace Kinetix.OrderService.Application.Ports;

public interface IReturnsHandler {
    Task<OpenReturnOutcome> OpenAsync(
        string orderNumber, string merchantPrincipalId, string reason
    );

    Task<ReturnGoodsReceivedOutcome> GoodsReceivedAsync(
        string returnNumber,
        string merchantPrincipalId,
        IReadOnlyList<ReturnedLine> lines,
        string binCode,
        DateTime receivedAt
    );
}
