using Grpc.Core;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Http;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Application.Checkout;

public class CheckoutSagaRunner(
    OrderDbContext dbContext,
    IVoucherQuotaClient voucherClient,
    IFlashSaleClient flashSaleClient,
    IStockClient stockClient,
    IEscrowClient escrowClient,
    ISagaLeaseStore leaseStore,
    CompensationPolicy policy,
    RequestIdAccessor requestIds,
    ILogger<CheckoutSagaRunner> logger
) {
    public const string GivingUpToken = "GIVING UP";

    private const decimal MinorPerMajor = 100m;

    private readonly OrderDbContext _dbContext = dbContext;
    private readonly IVoucherQuotaClient _voucherClient = voucherClient;
    private readonly IFlashSaleClient _flashSaleClient = flashSaleClient;
    private readonly IStockClient _stockClient = stockClient;
    private readonly IEscrowClient _escrowClient = escrowClient;
    private readonly ISagaLeaseStore _leaseStore = leaseStore;
    private readonly CompensationPolicy _policy = policy;
    private readonly RequestIdAccessor _requestIds = requestIds;
    private readonly ILogger<CheckoutSagaRunner> _logger = logger;

    public async Task<SagaOutcome> RunAsync(CheckoutPlan plan) {
        var saga = new CheckoutSaga {
            OrderNumber = plan.OrderNumber,
            CustomerPrincipalId = plan.CustomerPrincipalId,
            State = SagaState.Running,
            CorrelationId = _requestIds.Current,
        };
        _dbContext.CheckoutSagas.Add(saga);
        await _dbContext.SaveChangesAsync();

        var lease = await _leaseStore.TryAcquireForwardAsync(saga.Id, CancellationToken.None);

        _dbContext.Entry(saga).State = EntityState.Detached;

        if (lease is null) {
            _logger.LogError(
                "could not take the lease on the saga just created for {Order}; refusing to run a "
                    + "checkout that nothing is holding",
                plan.OrderNumber
            );
            return new SagaOutcome(false, "this checkout could not be started safely");
        }

        ForwardPassOutcome forward;
        try {
            forward = await RunForwardAsync(saga, plan, lease);
        } catch (Exception e) {
            _logger.LogError(e, "checkout saga for {Order} threw; compensating", plan.OrderNumber);
            forward = ForwardPassOutcome.Failed("a service this checkout depends on did not answer");
        }

        if (forward.LeaseLost) {
            _logger.LogWarning(
                "the lease on {Order} changed hands while its checkout was still running; the "
                    + "worker holding it now is unwinding this saga",
                plan.OrderNumber
            );
            return new SagaOutcome(false, "this checkout was taken over and is being unwound");
        }

        if (forward.FailureReason is null) {
            if (await _leaseStore.CompleteForwardAsync(lease, CancellationToken.None)) {
                return new SagaOutcome(true, null);
            }

            _logger.LogWarning(
                "the lease on {Order} was gone by the time its checkout finished, so it was not "
                    + "marked Completed; the worker holding it is unwinding this saga",
                plan.OrderNumber
            );
            return new SagaOutcome(false, "this checkout was taken over and is being unwound");
        }

        return await CompensateAsync(saga, forward.FailureReason, lease, CancellationToken.None);
    }

    public async Task<SagaOutcome> CompensateAsync(
        CheckoutSaga saga,
        string reason,
        SagaLease? heldLease,
        CancellationToken cancellationToken
    ) {
        var lease = await _leaseStore.TryClaimAsync(saga.Id, heldLease, cancellationToken);

        _dbContext.Entry(saga).State = EntityState.Detached;

        if (lease is null) {
            if (await _leaseStore.AbandonOverBudgetAsync(saga.Id, cancellationToken)) {
                await CancelOrderIfStillPendingAsync(saga.OrderNumber, cancellationToken);
                _logger.LogCritical(
                    "{Token} on {Order}: all {Max} compensation round(s) ended before they could "
                        + "record an outcome, so the budget is spent with nothing released. Money "
                        + "or stock may still be held downstream. Nothing further will be "
                        + "attempted automatically; settle it by hand",
                    GivingUpToken, saga.OrderNumber, _policy.MaxAttempts
                );
                return new SagaOutcome(false, reason);
            }

            _logger.LogInformation(
                "not compensating {Order}: another worker holds it, or it is not due yet",
                saga.OrderNumber
            );
            return new SagaOutcome(false, reason);
        }

        using var correlation = _requestIds.Override(CorrelationFor(saga, lease));
        using var logScope = _logger.BeginScope(new Dictionary<string, object?> {
            ["SagaId"] = saga.Id,
            ["OrderNumber"] = saga.OrderNumber,
            ["CorrelationId"] = saga.CorrelationId,
            ["Attempt"] = lease.AttemptNumber,
        });

        try {
            return await CompensateUnderLeaseAsync(saga, reason, lease, cancellationToken);
        } catch (Exception e) when (e is not OperationCanceledException) {
            _logger.LogError(e,
                "compensation round {Attempt} for {Order} threw and is being abandoned; the lease "
                    + "will expire and another round will pick it up if the budget allows",
                lease.AttemptNumber, saga.OrderNumber
            );
            return new SagaOutcome(false, reason);
        }
    }

    private async Task<ForwardPassOutcome> RunForwardAsync(
        CheckoutSaga saga,
        CheckoutPlan plan,
        SagaLease lease
    ) {
        if (!string.IsNullOrWhiteSpace(plan.VoucherCode)) {
            if (!await _leaseStore.HeartbeatAsync(lease, CancellationToken.None)) {
                return ForwardPassOutcome.Lost();
            }

            var step = await BeginStep(saga, SagaStepName.RedeemVoucher, plan.VoucherCode!, 1);
            var result = await _voucherClient.RedeemVoucherAsync(
                plan.VoucherCode!, plan.OrderNumber, plan.CustomerPrincipalId);
            var settled = await Settle(lease, step, result);
            if (settled is null) {
                return ForwardPassOutcome.Lost();
            }
            if (!settled.Value) {
                return ForwardPassOutcome.Failed($"voucher: {result.Detail}");
            }
        }

        foreach (var claim in plan.FlashSaleClaims) {
            if (!await _leaseStore.HeartbeatAsync(lease, CancellationToken.None)) {
                return ForwardPassOutcome.Lost();
            }

            var step = await BeginStep(saga, SagaStepName.AllocateFlashSaleStock,
                claim.FlashSaleId, claim.Quantity, productId: claim.ProductId);
            var result = await _flashSaleClient.AllocateAsync(
                claim.FlashSaleId, claim.ProductId, claim.Quantity, plan.OrderNumber);
            var settled = await Settle(lease, step, result);
            if (settled is null) {
                return ForwardPassOutcome.Lost();
            }
            if (!settled.Value) {
                return ForwardPassOutcome.Failed($"flash sale {claim.FlashSaleId}: {result.Detail}");
            }
        }

        foreach (var reservation in plan.Reservations) {
            if (!await _leaseStore.HeartbeatAsync(lease, CancellationToken.None)) {
                return ForwardPassOutcome.Lost();
            }

            var step = await BeginStep(saga, SagaStepName.ReserveStock, reservation.Sku,
                reservation.Quantity, reservation.MerchantPrincipalId);
            var result = await _stockClient.ReserveStockAsync(
                reservation.MerchantPrincipalId, reservation.Sku, reservation.Quantity, plan.OrderNumber);
            var settled = await Settle(lease, step, result);
            if (settled is null) {
                return ForwardPassOutcome.Lost();
            }
            if (!settled.Value) {
                return ForwardPassOutcome.Failed($"stock for {reservation.Sku}: {result.Detail}");
            }
        }

        if (!await _leaseStore.HeartbeatAsync(lease, CancellationToken.None)) {
            return ForwardPassOutcome.Lost();
        }

        var escrowStep = await BeginStep(saga, SagaStepName.CreateEscrowHold, plan.OrderNumber, 1,
            plan.MerchantPrincipalId);
        var escrow = await _escrowClient.CreateHoldAsync(
            plan.OrderNumber,
            plan.CustomerPrincipalId,
            plan.MerchantPrincipalId,
            null,
            plan.TotalOrderAmount,
            plan.MerchantAmount,
            plan.ShippingFeeAmount);
        var escrowSettled = await Settle(lease, escrowStep, escrow);
        if (escrowSettled is null) {
            return ForwardPassOutcome.Lost();
        }
        if (!escrowSettled.Value) {
            return ForwardPassOutcome.Failed($"escrow: {escrow.Detail}");
        }

        return ForwardPassOutcome.Completed();
    }

    private async Task<SagaOutcome> CompensateUnderLeaseAsync(
        CheckoutSaga saga,
        string reason,
        SagaLease lease,
        CancellationToken cancellationToken
    ) {
        var steps = await _dbContext.CheckoutSagaSteps
            .AsNoTracking()
            .Where(s => s.SagaId == saga.Id)
            .Where(s => s.State == SagaStepState.Done
                || s.State == SagaStepState.Attempting
                || (s.State == SagaStepState.Failed && s.Name == SagaStepName.CreateEscrowHold)
            )
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        _logger.LogInformation(
            "compensating {Order}: round {Attempt} of {Max}, {Count} step(s) still outstanding",
            saga.OrderNumber, lease.AttemptNumber, _policy.MaxAttempts, steps.Count
        );

        var allReleased = true;
        var terminal = false;
        var needsAttention = false;
        var leaseLost = false;
        string? attentionCode = null;
        string? lastCode = null;

        foreach (var step in steps) {
            if (!await _leaseStore.HeartbeatAsync(lease, cancellationToken)) {
                leaseLost = true;
                break;
            }

            var outcome = await CompensateStepAsync(saga, step, lease, reason, cancellationToken);
            if (outcome is null) {
                leaseLost = true;
                break;
            }

            var recorded = await _leaseStore.TryRecordStepOutcomeAsync(
                lease,
                step.Id,
                outcome.Landed ? SagaStepState.Compensated : step.State,
                outcome.Landed ? outcome.AlreadyDone : step.CompensatedByRepeat,
                outcome.Detail,
                outcome.Code,
                cancellationToken
            );

            if (!recorded) {
                leaseLost = true;
                break;
            }

            if (outcome.Landed) {
                step.State = SagaStepState.Compensated;
                step.CompensatedByRepeat = outcome.AlreadyDone;
                _logger.LogInformation(
                    "gave back {Step} {Reference} for {Order}{Already}",
                    step.Name, step.Reference, saga.OrderNumber,
                    outcome.AlreadyDone ? " (the downstream had already released it)" : string.Empty
                );
            } else {
                allReleased = false;
                _logger.LogWarning(
                    "could not undo {Step} {Reference} for {Order}: {Detail}",
                    step.Name, step.Reference, saga.OrderNumber, outcome.Detail
                );
            }

            step.Detail = outcome.Detail;
            step.LastFailureCode = outcome.Code;
            step.UpdatedAt = DateTime.UtcNow;

            terminal |= outcome.Kind == CompensationFailureKind.Terminal;
            needsAttention |= outcome.NeedsAttention;
            if (!string.IsNullOrEmpty(outcome.Code)) {
                lastCode = outcome.Code;
                if ((outcome.NeedsAttention || outcome.Kind == CompensationFailureKind.Terminal)
                    && attentionCode is null
                ) {
                    attentionCode = outcome.Code;
                }
            }
        }

        if (leaseLost) {
            _logger.LogWarning(
                "lost the lease on {Order} part-way through round {Attempt}; stopping without "
                    + "another downstream call. Whoever holds it now will finish the unwind",
                saga.OrderNumber, lease.AttemptNumber
            );
            await _leaseStore.RecordAttemptOutcomeAsync(
                lease, CompensationAttemptOutcome.LeaseLost,
                "the lease was gone before this round finished", cancellationToken
            );
            return new SagaOutcome(false, reason);
        }

        var overBudget = lease.AttemptNumber >= _policy.MaxAttempts;
        var state = allReleased ? SagaState.Compensated
            : terminal || overBudget ? SagaState.Abandoned
            : SagaState.Stuck;

        var failureCode = attentionCode ?? lastCode ?? string.Empty;
        if (state == SagaState.Abandoned && string.IsNullOrEmpty(failureCode)) {
            failureCode = CompensationFailureCode.AttemptsExhausted;
        }

        var delaySeconds = state == SagaState.Stuck ? _policy.DelaySeconds(lease.AttemptNumber) : -1;

        var written = await _leaseStore.FinishCompensationAsync(
            lease, state, reason, failureCode, delaySeconds, needsAttention, cancellationToken
        );

        if (!written) {
            _logger.LogWarning(
                "the lease on {Order} was gone before round {Attempt} could record its outcome; "
                    + "the worker holding it now decides what this saga becomes",
                saga.OrderNumber, lease.AttemptNumber
            );
            await _leaseStore.RecordAttemptOutcomeAsync(
                lease, CompensationAttemptOutcome.LeaseLost,
                "the fenced outcome write matched no row", cancellationToken
            );
            return new SagaOutcome(false, reason);
        }

        await _leaseStore.RecordAttemptOutcomeAsync(
            lease,
            state switch {
                SagaState.Compensated => CompensationAttemptOutcome.Released,
                SagaState.Abandoned => CompensationAttemptOutcome.Abandoned,
                _ => CompensationAttemptOutcome.Partial,
            },
            failureCode,
            cancellationToken
        );

        if (state != SagaState.Stuck) {
            await CancelOrderIfStillPendingAsync(saga.OrderNumber, cancellationToken);
        }

        if (state == SagaState.Abandoned) {
            _logger.LogCritical(
                "{Token} on {Order} after {Attempt} round(s): {Count} step(s) never came back — "
                    + "{Outstanding}. Failure code {Code}. Money or stock is still held downstream "
                    + "for customer {Customer} and nothing further will be attempted "
                    + "automatically; settle it by hand",
                GivingUpToken, saga.OrderNumber, lease.AttemptNumber,
                steps.Count(s => s.State != SagaStepState.Compensated), Outstanding(steps),
                failureCode, saga.CustomerPrincipalId
            );
        } else if (state == SagaState.Stuck) {
            _logger.LogWarning(
                "{Order} did not fully unwind on round {Attempt} of {Max}; trying again in about "
                    + "{Delay}s. Still held: {Outstanding}",
                saga.OrderNumber, lease.AttemptNumber, _policy.MaxAttempts, delaySeconds,
                Outstanding(steps)
            );
        }

        return new SagaOutcome(false, reason);
    }

    private async Task<StepCompensationOutcome?> CompensateStepAsync(
        CheckoutSaga saga,
        CheckoutSagaStep step,
        SagaLease lease,
        string reason,
        CancellationToken cancellationToken
    ) {
        if (step.Name == SagaStepName.CreateEscrowHold) {
            return await CompensateEscrowAsync(saga, step, lease, reason, cancellationToken);
        }

        if (step.Name is not (
            SagaStepName.RedeemVoucher
            or SagaStepName.ReserveStock
            or SagaStepName.AllocateFlashSaleStock
            )) {
            return StepCompensationOutcome.Terminal(
                CompensationFailureCode.NoCompensationDefined,
                $"no compensation is defined for {step.Name}"
            );
        }

        if (!await _leaseStore.TryCountStepDispatchAsync(lease, step.Id, cancellationToken)) {
            return null;
        }
        step.CompensationAttempts += 1;

        try {
            var result = step.Name switch {
                SagaStepName.RedeemVoucher =>
                    await _voucherClient.ReleaseVoucherAsync(step.Reference, saga.OrderNumber),
                SagaStepName.ReserveStock =>
                    await _stockClient.ReleaseStockAsync(
                        step.MerchantPrincipalId, step.Reference, step.Quantity, saga.OrderNumber),
                _ =>
                    await _flashSaleClient.ReleaseAsync(
                        step.Reference, step.ProductId ?? string.Empty, step.Quantity, saga.OrderNumber),
            };

            if (result.Success) {
                return StepCompensationOutcome.Released(result.AlreadyDone);
            }

            return StepCompensationOutcome.Retryable(
                CompensationFailureCode.DownstreamRefused, result.Detail
            );
        } catch (RpcException e) {
            return CompensationPolicy.Classify(e) == CompensationFailureKind.Terminal
                ? StepCompensationOutcome.Terminal(
                    CompensationFailureCode.DownstreamRefused,
                    $"{e.StatusCode}: {e.Status.Detail}"
                  )
                : StepCompensationOutcome.Retryable(
                    CompensationFailureCode.DownstreamUnavailable,
                    $"{e.StatusCode}: {e.Status.Detail}"
                  );
        } catch (Exception e) when (e is not OperationCanceledException) {
            return StepCompensationOutcome.Retryable(
                CompensationFailureCode.DownstreamUnavailable, e.Message
            );
        }
    }

    private async Task<StepCompensationOutcome?> CompensateEscrowAsync(
        CheckoutSaga saga,
        CheckoutSagaStep step,
        SagaLease lease,
        string reason,
        CancellationToken cancellationToken
    ) {
        if (step.State == SagaStepState.Failed) {
            return await SettleRefusedHoldAsync(saga, cancellationToken);
        }

        if (!await _leaseStore.TryCountStepDispatchAsync(lease, step.Id, cancellationToken)) {
            return null;
        }
        step.CompensationAttempts += 1;

        var refundSettled = false;
        try {
            var result = await _escrowClient.RefundHoldAsync(saga.OrderNumber, reason);
            if (result.Success) {
                return StepCompensationOutcome.Released(result.AlreadyDone);
            }

            refundSettled = result.AlreadyDone;
        } catch (Exception e) when (e is not OperationCanceledException) {
            _logger.LogWarning(e,
                "the refund for {Order} did not come back cleanly; reading payment's ledger "
                    + "before deciding what happened to the money",
                saga.OrderNumber
            );
        }

        return await SettleEscrowFromLedgerAsync(saga, step, refundSettled, cancellationToken);
    }

    private async Task<StepCompensationOutcome> SettleRefusedHoldAsync(
        CheckoutSaga saga,
        CancellationToken cancellationToken
    ) {
        EscrowStanding standing;

        try {
            standing = await _escrowClient.GetStandingAsync(saga.OrderNumber);
        } catch (Exception e) when (e is not OperationCanceledException) {
            return StepCompensationOutcome.Retryable(
                CompensationFailureCode.DownstreamUnavailable,
                $"payment did not answer GetEscrowStatus for a hold it had refused: {e.Message}"
            );
        }

        if (!standing.Found) {
            return StepCompensationOutcome.Released(alreadyDone: true);
        }

        if (standing.Status == EscrowStandingStatus.Refunded) {
            return StepCompensationOutcome.Released(alreadyDone: true);
        }

        return StepCompensationOutcome.Terminal(
            CompensationFailureCode.EscrowRefusedButHeld,
            $"payment refused this hold and reports it as {standing.Status} anyway; the two "
                + "services disagree about this customer's money and no automated refund will be "
                + "sent against an amount payment said it did not take"
        );
    }

    private async Task<StepCompensationOutcome> SettleEscrowFromLedgerAsync(
        CheckoutSaga saga,
        CheckoutSagaStep step,
        bool refundSettled,
        CancellationToken cancellationToken
    ) {
        EscrowStanding standing;

        try {
            standing = await _escrowClient.GetStandingAsync(saga.OrderNumber);
        } catch (Exception e) when (e is not OperationCanceledException) {
            return StepCompensationOutcome.Retryable(
                CompensationFailureCode.DownstreamUnavailable,
                $"payment did not answer GetEscrowStatus: {e.Message}"
            );
        }

        if (!standing.Found) {
            if (step.State == SagaStepState.Done) {
                return StepCompensationOutcome.Terminal(
                    CompensationFailureCode.EscrowMissingAfterHold,
                    "order recorded an escrow hold that payment does not have; the two services "
                        + "disagree about this customer's money"
                );
            }

            var amountMinor = await OrderAmountMinorAsync(saga.OrderNumber, cancellationToken);

            if (!refundSettled) {
                _logger.LogWarning(
                    "payment has no escrow hold for {Order} ({AmountMinor} minor units, customer "
                        + "{Customer}), so its refund recorded a tombstone. Asking again next round: "
                        + "a hold that committed behind that tombstone is unwound by the repeat and "
                        + "by nothing else",
                    saga.OrderNumber, amountMinor, saga.CustomerPrincipalId
                );

                return StepCompensationOutcome.Stalled(
                    CompensationFailureCode.EscrowAbsent,
                    "payment held nothing to refund and has recorded that; the refund is sent once "
                        + "more so payment can unwind a hold that raced it"
                );
            }

            _logger.LogWarning(
                "payment has confirmed twice that it holds nothing for {Order} ({AmountMinor} minor "
                    + "units, customer {Customer}); the escrow leg is unwound. This saga stays "
                    + "flagged so the customer's wallet is eyeballed once by hand",
                saga.OrderNumber, amountMinor, saga.CustomerPrincipalId
            );

            return StepCompensationOutcome.ReleasedWithConcern(
                CompensationFailureCode.EscrowAbsent,
                "payment confirmed twice that it held nothing to refund; check the customer's "
                    + "wallet once by hand and close this out"
            );
        }

        switch (standing.Status) {
            case EscrowStandingStatus.Refunded:
                return StepCompensationOutcome.Released(alreadyDone: true);

            case EscrowStandingStatus.Held:
                return StepCompensationOutcome.Retryable(
                    CompensationFailureCode.EscrowRefundUnconfirmed,
                    "a refund was dispatched and did not answer, and payment still reports the hold "
                        + "as HELD; it is sent again next round under the same idempotency key"
                );

            case EscrowStandingStatus.Released:
                return StepCompensationOutcome.Terminal(
                    CompensationFailureCode.EscrowAlreadyReleased,
                    "the money has already gone to the merchant; no refund can undo this order "
                        + "from here"
                );

            default:
                return StepCompensationOutcome.Terminal(
                    CompensationFailureCode.EscrowUnexpectedStatus,
                    $"payment reports the hold as {standing.Status}, which compensation cannot act on"
                );
        }
    }

    private async Task CancelOrderIfStillPendingAsync(string orderNumber, CancellationToken cancellationToken) {
        var order = await _dbContext.Orders
            .FirstOrDefaultAsync(
                o => o.OrderNumber == orderNumber && o.Status == OrderStatus.PENDING_PAYMENT,
                cancellationToken
            );
        if (order is null) {
            return;
        }

        order.Status = OrderStatus.CANCELLED;
        order.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<long> OrderAmountMinorAsync(string orderNumber, CancellationToken cancellationToken) {
        var total = await _dbContext.Orders
            .Where(o => o.OrderNumber == orderNumber)
            .Select(o => (decimal?)o.FinalTotal)
            .FirstOrDefaultAsync(cancellationToken);

        return total is null
            ? 0
            : (long)Math.Round(total.Value * MinorPerMajor, MidpointRounding.AwayFromZero);
    }

    private static string CorrelationFor(CheckoutSaga saga, SagaLease lease) =>
        string.IsNullOrWhiteSpace(saga.CorrelationId)
            ? $"cmp-{saga.Id:N}-{lease.AttemptNumber}"
            : $"{saga.CorrelationId}/cmp-{lease.AttemptNumber}";

    private static string Outstanding(IEnumerable<CheckoutSagaStep> steps) =>
        string.Join("; ", steps
            .Where(s => s.State != SagaStepState.Compensated)
            .Select(s => $"{s.Name} {s.Reference} x{s.Quantity} "
                + $"[{s.LastFailureCode ?? "no code"}] ({s.Detail ?? "no detail"})"
            )
        );

    private async Task<CheckoutSagaStep> BeginStep(
        CheckoutSaga saga,
        SagaStepName name,
        string reference,
        int quantity,
        string merchantPrincipalId = "",
        string? productId = null
    ) {
        var step = new CheckoutSagaStep {
            SagaId = saga.Id,
            Name = name,
            Reference = reference,
            Quantity = quantity,
            MerchantPrincipalId = merchantPrincipalId,
            ProductId = productId,
            State = SagaStepState.Attempting,
        };
        _dbContext.CheckoutSagaSteps.Add(step);
        await _dbContext.SaveChangesAsync();

        _dbContext.Entry(step).State = EntityState.Detached;
        return step;
    }

    private async Task<bool?> Settle(
        SagaLease lease,
        CheckoutSagaStep step,
        StepResult result
    ) {
        var state = result.Success ? SagaStepState.Done : SagaStepState.Failed;

        var written = await _leaseStore.TryRecordStepOutcomeAsync(
            lease, step.Id, state, step.CompensatedByRepeat, result.Detail, step.LastFailureCode,
            CancellationToken.None);
        if (!written) {
            return null;
        }

        step.State = state;
        step.Detail = result.Detail;
        step.UpdatedAt = DateTime.UtcNow;
        return result.Success;
    }
}
