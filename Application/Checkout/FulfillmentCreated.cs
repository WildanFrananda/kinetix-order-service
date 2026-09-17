namespace Kinetix.OrderService.Application.Checkout;

public record FulfillmentCreated(StepResult Result, string WarehouseOrderId);
