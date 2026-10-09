using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Authorize(Policy = OrderPolicies.MerchantPayout)]
[Route("api/v1/orders")]
public class OrderCompletionController(IOrderCompletion completion) : ControllerBase {
    private readonly IOrderCompletion _completion = completion;

    [HttpPost("{orderNumber}/complete")]
    public async Task<IActionResult> Complete(string orderNumber, CancellationToken cancellationToken) {
        var outcome = await _completion.CompleteAsync(orderNumber, waitForReturnWindow: false, cancellationToken);

        return outcome.Status switch {
            CompletionStatus.Completed =>
                Ok(new OrderCompletionResponse(orderNumber, outcome.EscrowReleased, outcome.Detail)),
            CompletionStatus.AlreadyCompleted => Conflict(new {
                error = "ORDER_ALREADY_COMPLETED",
                message = "the order is already complete; its escrow release is retried until payment accepts it",
            }),
            CompletionStatus.NoSuchOrder => NotFound(new { error = "ORDER_NOT_FOUND", message = outcome.Detail }),
            CompletionStatus.ReturnUnresolved => Conflict(new { error = "RETURN_UNRESOLVED", message = outcome.Detail }),
            _ => Conflict(new { error = "ORDER_NOT_DELIVERED", message = outcome.Detail }),
        };
    }
}
