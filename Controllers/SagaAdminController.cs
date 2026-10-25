using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Observability;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Authorize(Policy = SagaAdminPolicy)]
[Route("api/v1/orders/sagas")]
public class SagaAdminController(OrderDbContext dbContext, AdminAudit audit) : ControllerBase {
    public const string SagaAdminPolicy = "SagaAdmin";
    public const string AttentionQueue = "sagas/needs-attention";

    private const int MaxPageSize = 200;

    private readonly OrderDbContext _dbContext = dbContext;
    private readonly AdminAudit _audit = audit;

    [HttpGet("needs-attention")]
    public async Task<ActionResult<List<SagaNeedingAttentionResponse>>> NeedsAttention(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default
    ) {
        var sagas = await _audit.AuditAsync(
            HttpContext,
            AdminActions.SagaAttentionList,
            AttentionQueue,
            () => ReadNeedsAttention(limit, cancellationToken),
            _ => AdminActionOutcome.Done
        );

        return Ok(sagas);
    }

    [HttpGet("needs-attention/count")]
    public async Task<ActionResult<int>> NeedsAttentionCount(CancellationToken cancellationToken) {
        var count = await _audit.AuditAsync(
            HttpContext,
            AdminActions.SagaAttentionCount,
            AttentionQueue,
            () => _dbContext.CheckoutSagas
                .AsNoTracking()
                .CountAsync(s => s.NeedsAttentionAt != null, cancellationToken),
            _ => AdminActionOutcome.Done
        );

        return Ok(count);
    }

    private async Task<List<SagaNeedingAttentionResponse>> ReadNeedsAttention(
        int limit,
        CancellationToken cancellationToken
    ) {
        var take = Math.Clamp(limit, 1, MaxPageSize);

        var sagas = await _dbContext.CheckoutSagas
            .AsNoTracking()
            .Where(s => s.NeedsAttentionAt != null)
            .OrderByDescending(s => s.NeedsAttentionAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        if (sagas.Count == 0) {
            return [];
        }

        var sagaIds = sagas.Select(s => s.Id).ToList();
        var orderNumbers = sagas.Select(s => s.OrderNumber).ToList();

        var steps = await _dbContext.CheckoutSagaSteps
            .AsNoTracking()
            .Where(s => sagaIds.Contains(s.SagaId))
            .Where(s => s.State != SagaStepState.Compensated || s.LastFailureCode != null)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        var orderStatuses = await _dbContext.Orders
            .AsNoTracking()
            .Where(o => orderNumbers.Contains(o.OrderNumber))
            .Select(o => new { o.OrderNumber, o.Status })
            .ToListAsync(cancellationToken);

        var statusByOrder = orderStatuses.ToDictionary(o => o.OrderNumber, o => o.Status.ToString());
        var stepsBySaga = steps.GroupBy(s => s.SagaId).ToDictionary(g => g.Key, g => g.ToList());

        return [.. sagas.Select(saga => new SagaNeedingAttentionResponse(
            saga.Id,
            saga.OrderNumber,
            saga.CustomerPrincipalId,
            saga.State.ToString(),
            statusByOrder.GetValueOrDefault(saga.OrderNumber),
            saga.CompensationAttempts,
            saga.LastFailureCode,
            saga.FailureReason,
            saga.CorrelationId,
            saga.AbandonedAt,
            saga.NeedsAttentionAt,
            saga.UpdatedAt,
            [.. (stepsBySaga.GetValueOrDefault(saga.Id) ?? []).Select(ToStepResponse)]
        ))];
    }

    private static UnreleasedStepResponse ToStepResponse(CheckoutSagaStep step) => new(
        step.Name.ToString(),
        step.Reference,
        step.Quantity,
        step.MerchantPrincipalId,
        step.ProductId,
        step.State.ToString(),
        step.CompensationAttempts,
        step.LastFailureCode,
        step.Detail,
        RemedyFor(step)
    );

    private static string RemedyFor(CheckoutSagaStep step) => step.Name switch {
        SagaStepName.RedeemVoucher =>
            "pricing.v1.PricingService/ReleaseVoucherRedemption with this voucher code and order number",
        SagaStepName.AllocateFlashSaleStock =>
            "pricing.v1.PricingService/ReleaseFlashSaleAllocation with this flash sale id and order number",
        SagaStepName.ReserveStock =>
            "fulfillment.v1.BinStockService/ReleaseStock with this merchant, sku and order number",
        SagaStepName.CreateFulfillmentOrder =>
            "fulfillment.v1.FulfillmentTaskService/CancelFulfillmentTask with this merchant and the "
                + "warehouse task id in the reference",
        SagaStepName.CreateEscrowHold =>
            "payment.v1.PaymentService/RefundEscrow with idempotency_key 'order:<order number>' — "
                + "payment dedupes on that key and answers already_applied, so repeating it cannot "
                + "credit twice. Read GetEscrowStatus first to see what it will do: RELEASED means "
                + "the merchant has been paid and no refund can undo it from here",
        _ => "no automated compensation is defined for this step",
    };
}
