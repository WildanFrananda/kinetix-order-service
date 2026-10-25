using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Observability;
using Kinetix.OrderService.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Kinetix.OrderService.Application.Lifecycle;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Authorize(Policy = OrderPolicies.MerchantPayout)]
[Route("api/v1/orders")]
public class OrderCompletionController(IOrderCompletion completion, AdminAudit audit) : ControllerBase {
    private readonly IOrderCompletion _completion = completion;
    private readonly AdminAudit _audit = audit;

    [HttpPost("{orderNumber}/complete")]
    public async Task<IActionResult> Complete(string orderNumber, CancellationToken cancellationToken) {
        var admin = CallerPrincipal.Of(User);
        if (admin is null) {
            _audit.Record(HttpContext, AdminActions.Complete, orderNumber, AdminActionOutcome.Refused);
            return Unauthorized(new {
                error = "PRINCIPAL_UNKNOWN",
                message = "the token names no principal, so the completion could not be attributed",
            });
        }

        var outcome = await _audit.AuditAsync(
            HttpContext,
            AdminActions.Complete,
            orderNumber,
            () => _completion.CompleteAsync(
                orderNumber,
                waitForReturnWindow: false,
                LifecycleActor.Admin(admin),
                cancellationToken
            ),
            done => done.Status is CompletionStatus.Completed ? AdminActionOutcome.Done : AdminActionOutcome.Refused
        );

        return outcome.Status switch {
            CompletionStatus.Completed =>
                Ok(new OrderCompletionResponse(orderNumber, outcome.EscrowReleased, outcome.Detail)),
            CompletionStatus.AlreadyCompleted => Conflict(new {
                error = "ORDER_ALREADY_COMPLETED",
                message = "the order is already complete, so its escrow is already owed to the merchant: released, "
                        + "retried while payment cannot be reached, or waiting for staff if payment refused it",
            }),
            CompletionStatus.NoSuchOrder => NotFound(new { error = "ORDER_NOT_FOUND", message = outcome.Detail }),
            CompletionStatus.ReturnUnresolved => Conflict(new {
                error = "RETURN_UNRESOLVED",
                message = outcome.Detail,
            }),
            _ => Conflict(new { error = "ORDER_NOT_DELIVERED", message = outcome.Detail }),
        };
    }
}
