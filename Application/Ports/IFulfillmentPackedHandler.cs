namespace Kinetix.OrderService.Application.Ports;

public interface IFulfillmentPackedHandler {
    Task<FulfillmentPackedOutcome> HandleAsync(
        string merchantPrincipalId,
        string orderNumber,
        string fulfillmentTaskId
    );
}

public record FulfillmentPackedOutcome(
    bool Found,
    bool AlreadyPacked,
    string DispatchRef,
    string? Detail
);
