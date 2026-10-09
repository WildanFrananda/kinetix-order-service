using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Requests;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Persistence;
using Kinetix.OrderService.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Route("api/v1/orders/returns")]
public class ReturnAdminController(OrderDbContext dbContext, IReturnRejection rejection) : ControllerBase {
    private const int MaxPageSize = 200;

    private readonly OrderDbContext _dbContext = dbContext;
    private readonly IReturnRejection _rejection = rejection;

    [HttpGet("needs-attention")]
    [Authorize(Policy = SagaAdminController.SagaOperatorPolicy)]
    public async Task<ActionResult<List<ReturnNeedingAttentionResponse>>> NeedsAttention(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default
    ) {
        var take = Math.Clamp(limit, 1, MaxPageSize);

        var returns = await _dbContext.OrderReturns
            .AsNoTracking()
            .Where(r => r.Status == ReturnStatus.OPEN
                || (r.Status == ReturnStatus.GOODS_RECEIVED && r.NextRefundAttemptAt == null))
            .OrderBy(r => r.OpenedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        return Ok(returns.Select(r => new ReturnNeedingAttentionResponse(
            r.ReturnNumber,
            r.OrderNumber,
            r.MerchantPrincipalId,
            r.Status.ToString(),
            r.Reason,
            r.OpenedAt,
            r.GoodsReceivedAt,
            r.RefundAmount,
            r.LastRefundError
        )).ToList());
    }

    [HttpPost("{returnNumber}/reject")]
    [Authorize(Policy = OrderPolicies.MerchantPayout)]
    public async Task<IActionResult> Reject(
        string returnNumber,
        [FromBody] RejectReturnRequest request,
        CancellationToken cancellationToken
    ) {
        var status = await _rejection.RejectAsync(returnNumber, request.Reason ?? string.Empty, cancellationToken);

        return status switch {
            ReturnRejectionStatus.Rejected => NoContent(),
            ReturnRejectionStatus.NoSuchReturn => NotFound(new {
                error = "RETURN_NOT_FOUND",
                message = "no return carries that number",
            }),
            ReturnRejectionStatus.NoReasonGiven => BadRequest(new {
                error = "REASON_REQUIRED",
                message = "a rejection pays the merchant instead of refunding the buyer, so it must say why",
            }),
            ReturnRejectionStatus.AlreadyRejected => Conflict(new {
                error = "RETURN_ALREADY_REJECTED",
                message = "that return was already rejected",
            }),
            _ => Conflict(new {
                error = "GOODS_ALREADY_RECEIVED",
                message = "the goods came back, so the buyer is owed a refund and the return cannot be rejected",
            }),
        };
    }
}
