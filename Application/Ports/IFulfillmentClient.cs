using Kinetix.OrderService.Application.Checkout;

namespace Kinetix.OrderService.Application.Ports;

public interface IFulfillmentClient {
    Task<FulfillmentCreated> CreateTaskAsync(
        string merchantPrincipalId,
        string orderNumber,
        IReadOnlyList<FulfillmentLine> lines
    );

    Task<StepResult> CancelTaskAsync(string merchantPrincipalId, string fulfillmentTaskId);

    Task<StepResult> RecordCourierAwbAsync(
        string merchantPrincipalId,
        string fulfillmentTaskId,
        string orderNumber,
        string awbNumber
    );
}
