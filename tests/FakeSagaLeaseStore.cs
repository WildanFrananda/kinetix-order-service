using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Tests;

public sealed class FakeSagaLeaseStore(
    OrderDbContext dbContext,
    CompensationPolicy policy,
    string worker = "test-worker"
) : ISagaLeaseStore {
    private readonly OrderDbContext _dbContext = dbContext;
    private readonly CompensationPolicy _policy = policy;
    private readonly string _worker = worker;

    public async Task<SagaLease?> TryAcquireForwardAsync(Guid sagaId, CancellationToken cancellationToken) {
        var saga = await Load(sagaId, cancellationToken);
        if (saga is null || saga.State != SagaState.Running || !LeaseIsFree(saga)) {
            return null;
        }

        var owner = NewLeaseOwner();
        saga.LeaseOwner = owner;
        saga.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(_policy.LeaseSeconds);
        saga.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new SagaLease(sagaId, owner, 0);
    }

    public async Task<SagaLease?> TryClaimAsync(
        Guid sagaId, SagaLease? heldLease, CancellationToken cancellationToken) {

        var saga = await Load(sagaId, cancellationToken);
        if (saga is null) {
            return null;
        }

        var claimable = saga.State is SagaState.Running or SagaState.Compensating or SagaState.Stuck;
        var mine = heldLease is not null && saga.LeaseOwner == heldLease.Owner;
        var due = saga.NextAttemptAt is null || saga.NextAttemptAt <= DateTime.UtcNow;

        if (!claimable || !(LeaseIsFree(saga) || mine) || !due || saga.CompensationAttempts >= _policy.MaxAttempts) {
            return null;
        }

        var owner = NewLeaseOwner();
        saga.CompensationAttempts += 1;
        saga.State = SagaState.Compensating;
        saga.LeaseOwner = owner;
        saga.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(_policy.LeaseSeconds);
        saga.UpdatedAt = DateTime.UtcNow;

        var open = await _dbContext.CompensationAttempts
            .Where(a => a.SagaId == sagaId && a.FinishedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var attempt in open) {
            attempt.FinishedAt = DateTime.UtcNow;
            attempt.Outcome = CompensationAttemptOutcome.Crashed;
        }

        _dbContext.CompensationAttempts.Add(new CompensationAttempt {
            SagaId = sagaId,
            AttemptNo = saga.CompensationAttempts,
            Worker = owner,
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new SagaLease(sagaId, owner, saga.CompensationAttempts);
    }

    public async Task<bool> AbandonOverBudgetAsync(Guid sagaId, CancellationToken cancellationToken) {
        var saga = await Load(sagaId, cancellationToken);
        if (saga is null
            || saga.State is not (SagaState.Running or SagaState.Compensating or SagaState.Stuck)
            || !LeaseIsFree(saga)
            || saga.CompensationAttempts < _policy.MaxAttempts
        ) {
            return false;
        }

        saga.State = SagaState.Abandoned;
        saga.AbandonedAt ??= DateTime.UtcNow;
        saga.NeedsAttentionAt ??= DateTime.UtcNow;
        saga.LastFailureCode ??= CompensationFailureCode.AttemptsExhausted;
        saga.NextAttemptAt = null;
        saga.LeaseOwner = null;
        saga.LeaseExpiresAt = null;
        saga.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> HeartbeatAsync(SagaLease lease, CancellationToken cancellationToken) {
        var saga = await Load(lease.SagaId, cancellationToken);
        if (saga is null || !Holds(saga, lease)) {
            return false;
        }

        saga.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(_policy.LeaseSeconds);
        saga.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> FinishCompensationAsync(
        SagaLease lease,
        SagaState state,
        string failureReason,
        string failureCode,
        int delaySeconds,
        bool needsAttention,
        CancellationToken cancellationToken
    ) {
        var saga = await Load(lease.SagaId, cancellationToken);
        if (saga is null || !Holds(saga, lease)) {
            return false;
        }

        saga.State = state;
        saga.FailureReason = string.IsNullOrEmpty(failureReason) ? null : failureReason;
        saga.LastFailureCode = string.IsNullOrEmpty(failureCode) ? saga.LastFailureCode : failureCode;
        saga.NextAttemptAt = delaySeconds < 0 ? null : DateTime.UtcNow.AddSeconds(delaySeconds);
        if (state == SagaState.Abandoned) {
            saga.AbandonedAt ??= DateTime.UtcNow;
        }
        if (needsAttention || state == SagaState.Abandoned) {
            saga.NeedsAttentionAt ??= DateTime.UtcNow;
        }
        saga.LeaseOwner = null;
        saga.LeaseExpiresAt = null;
        saga.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteForwardAsync(SagaLease lease, CancellationToken cancellationToken) {
        var saga = await Load(lease.SagaId, cancellationToken);
        if (saga is null || !Holds(saga, lease)) {
            return false;
        }

        saga.State = SagaState.Completed;
        saga.LeaseOwner = null;
        saga.LeaseExpiresAt = null;
        saga.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TryCountStepDispatchAsync(
        SagaLease lease,
        Guid stepId,
        CancellationToken cancellationToken
    ) {
        var step = await StepUnderLease(lease, stepId, cancellationToken);
        if (step is null) {
            return false;
        }

        step.CompensationAttempts += 1;
        step.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TryRecordStepOutcomeAsync(
        SagaLease lease,
        Guid stepId,
        SagaStepState state,
        bool compensatedByRepeat,
        string? detail,
        string? failureCode,
        CancellationToken cancellationToken
    ) {
        var step = await StepUnderLease(lease, stepId, cancellationToken);
        if (step is null) {
            return false;
        }

        step.State = state;
        step.CompensatedByRepeat = compensatedByRepeat;
        step.Detail = string.IsNullOrEmpty(detail) ? null : detail;
        step.LastFailureCode = string.IsNullOrEmpty(failureCode) ? null : failureCode;
        step.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task RecordAttemptOutcomeAsync(
        SagaLease lease,
        CompensationAttemptOutcome outcome,
        string detail,
        CancellationToken cancellationToken
    ) {
        var attempt = await _dbContext.CompensationAttempts
            .FirstOrDefaultAsync(
                a => a.SagaId == lease.SagaId
                    && a.AttemptNo == lease.AttemptNumber
                    && a.FinishedAt == null,
                cancellationToken
            );
        if (attempt is null) {
            return;
        }

        attempt.FinishedAt = DateTime.UtcNow;
        attempt.Outcome = outcome;
        attempt.Detail = string.IsNullOrEmpty(detail) ? null : detail;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> DueForSweepAsync(
        int batchSize,
        int unattendedGraceSeconds,
        CancellationToken cancellationToken
    ) {
        var now = DateTime.UtcNow;
        var graceCutoff = now.AddSeconds(-unattendedGraceSeconds);

        var candidates = await _dbContext.CheckoutSagas
            .Where(s => s.State == SagaState.Running
                || s.State == SagaState.Compensating
                || s.State == SagaState.Stuck)
            .Where(s => s.LeaseExpiresAt == null || s.LeaseExpiresAt < now)
            .ToListAsync(cancellationToken);

        return [.. candidates
            .Where(s => s.CompensationAttempts >= _policy.MaxAttempts
                || ((s.NextAttemptAt is null || s.NextAttemptAt <= now)
                    && (s.State != SagaState.Running || s.UpdatedAt < graceCutoff)))
            .OrderBy(s => s.NextAttemptAt ?? s.UpdatedAt)
            .Take(batchSize)
            .Select(s => s.Id)
        ];
    }

    private string NewLeaseOwner() => $"{_worker}:{Guid.NewGuid():N}";

    private async Task<CheckoutSagaStep?> StepUnderLease(
        SagaLease lease,
        Guid stepId,
        CancellationToken cancellationToken
    ) {
        var saga = await Load(lease.SagaId, cancellationToken);
        if (saga is null || !Holds(saga, lease)) {
            return null;
        }

        return await _dbContext.CheckoutSagaSteps
            .FirstOrDefaultAsync(s => s.Id == stepId && s.SagaId == lease.SagaId, cancellationToken);
    }

    private Task<CheckoutSaga?> Load(Guid sagaId, CancellationToken cancellationToken) =>
        _dbContext.CheckoutSagas.FirstOrDefaultAsync(s => s.Id == sagaId, cancellationToken);

    private static bool LeaseIsFree(CheckoutSaga saga) =>
        saga.LeaseExpiresAt is null || saga.LeaseExpiresAt < DateTime.UtcNow;

    private static bool Holds(CheckoutSaga saga, SagaLease lease) =>
        saga.LeaseOwner == lease.Owner
        && saga.LeaseExpiresAt is not null
        && saga.LeaseExpiresAt > DateTime.UtcNow;
}
