using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Application.Services;

public class CheckoutSagaRunner(
    OrderDbContext dbContext,
    IVoucherQuotaClient voucherClient,
    IFlashSaleClient flashSaleClient,
    IStockClient stockClient,
    IEscrowClient escrowClient,
    ILogger<CheckoutSagaRunner> logger
) {
    private readonly OrderDbContext _dbContext = dbContext;
    private readonly IVoucherQuotaClient _voucherClient = voucherClient;
    private readonly IFlashSaleClient _flashSaleClient = flashSaleClient;
    private readonly IStockClient _stockClient = stockClient;
    private readonly IEscrowClient _escrowClient = escrowClient;
    private readonly ILogger<CheckoutSagaRunner> _logger = logger;

    public async Task<SagaOutcome> RunAsync(CheckoutPlan plan) {
        var saga = new CheckoutSaga {
            OrderNumber = plan.OrderNumber,
            CustomerPrincipalId = plan.CustomerPrincipalId,
            State = SagaState.Running,
        };
        _dbContext.CheckoutSagas.Add(saga);
        await _dbContext.SaveChangesAsync();

        try {
            if (!string.IsNullOrWhiteSpace(plan.VoucherCode)) {
                var step = await BeginStep(saga, SagaStepName.RedeemVoucher, plan.VoucherCode!, 1);
                var result = await _voucherClient.RedeemVoucherAsync(
                    plan.VoucherCode!, plan.OrderNumber, plan.CustomerPrincipalId);
                if (!await Settle(step, result)) {
                    return await Compensate(saga, $"voucher: {result.Detail}");
                }
            }

            foreach (var claim in plan.FlashSaleClaims) {
                var step = await BeginStep(saga, SagaStepName.AllocateFlashSaleStock,
                    claim.FlashSaleId, claim.Quantity, productId: claim.ProductId);
                var result = await _flashSaleClient.AllocateAsync(
                    claim.FlashSaleId, claim.ProductId, claim.Quantity, plan.OrderNumber);
                if (!await Settle(step, result)) {
                    return await Compensate(saga, $"flash sale {claim.FlashSaleId}: {result.Detail}");
                }
            }

            foreach (var reservation in plan.Reservations) {
                var step = await BeginStep(saga, SagaStepName.ReserveStock, reservation.Sku,
                    reservation.Quantity, reservation.MerchantPrincipalId);
                var result = await _stockClient.ReserveStockAsync(
                    reservation.MerchantPrincipalId, reservation.Sku, reservation.Quantity, plan.OrderNumber);
                if (!await Settle(step, result)) {
                    return await Compensate(saga, $"stock for {reservation.Sku}: {result.Detail}");
                }
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
            if (!await Settle(escrowStep, escrow)) {
                return await Compensate(saga, $"escrow: {escrow.Detail}");
            }

            saga.State = SagaState.Completed;
            saga.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            return new SagaOutcome(true, null);

        } catch (Exception e) {
            _logger.LogError(e, "checkout saga for {Order} threw; compensating", plan.OrderNumber);
            return await Compensate(saga, e.Message);
        }
    }

    public async Task<SagaOutcome> Compensate(CheckoutSaga saga, string reason) {
        saga.State = SagaState.Compensating;
        saga.FailureReason = reason;
        saga.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        var steps = await _dbContext.CheckoutSagaSteps
            .Where(s => s.SagaId == saga.Id)
            .Where(s => s.State == SagaStepState.Done || s.State == SagaStepState.Attempting)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        var allReleased = true;

        foreach (var step in steps) {
            try {
                var result = step.Name switch {
                    SagaStepName.RedeemVoucher =>
                        await _voucherClient.ReleaseVoucherAsync(step.Reference, saga.OrderNumber),
                    SagaStepName.ReserveStock =>
                        await _stockClient.ReleaseStockAsync(
                            step.MerchantPrincipalId, step.Reference, step.Quantity, saga.OrderNumber),
                    SagaStepName.AllocateFlashSaleStock =>
                        await _flashSaleClient.ReleaseAsync(
                            step.Reference, step.ProductId ?? string.Empty, step.Quantity, saga.OrderNumber),
                    SagaStepName.CreateEscrowHold =>
                        await _escrowClient.RefundHoldAsync(saga.OrderNumber, reason),
                    _ => StepResult.Refused($"no compensation is defined for {step.Name}"),
                };

                if (result.Success) {
                    step.State = SagaStepState.Compensated;
                    step.Detail = null;
                } else {
                    allReleased = false;
                    step.Detail = result.Detail;
                    _logger.LogError(
                        "could not undo {Step} {Reference} for {Order}: {Detail}",
                        step.Name, step.Reference, saga.OrderNumber, result.Detail);
                }
            } catch (Exception e) {
                allReleased = false;
                step.Detail = e.Message;
                _logger.LogError(e, "undoing {Step} {Reference} for {Order} threw",
                    step.Name, step.Reference, saga.OrderNumber);
            }
            step.UpdatedAt = DateTime.UtcNow;
        }

        saga.State = allReleased ? SagaState.Compensated : SagaState.Stuck;
        saga.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        return new SagaOutcome(false, reason);
    }

    private async Task<CheckoutSagaStep> BeginStep(
        CheckoutSaga saga, SagaStepName name, string reference, int quantity,
        string merchantPrincipalId = "", string? productId = null) {

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
        return step;
    }

    private async Task<bool> Settle(CheckoutSagaStep step, StepResult result) {
        step.State = result.Success ? SagaStepState.Done : SagaStepState.Failed;
        step.Detail = result.Detail;
        step.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
        return result.Success;
    }
}
