using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kinetix.OrderService.Infrastructure.Persistence;

public class SagaLeaseStore(
    OrderDbContext dbContext,
    SagaWorkerIdentity worker,
    CompensationPolicy policy,
    ILogger<SagaLeaseStore> logger
) : ISagaLeaseStore {
    private readonly OrderDbContext _dbContext = dbContext;
    private readonly SagaWorkerIdentity _worker = worker;
    private readonly CompensationPolicy _policy = policy;
    private readonly ILogger<SagaLeaseStore> _logger = logger;

    public async Task<SagaLease?> TryAcquireForwardAsync(Guid sagaId, CancellationToken cancellationToken) {
        var running = nameof(SagaState.Running);
        var owner = _worker.NewLeaseOwner();
        var leaseSeconds = _policy.LeaseSeconds;

        var claimed = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE checkout_sagas
               SET lease_owner = {owner},
                   lease_expires_at = now() + ({leaseSeconds} * interval '1 second'),
                   updated_at = now()
             WHERE id = {sagaId}
               AND state = {running}
               AND (lease_expires_at IS NULL OR lease_expires_at < now())", cancellationToken
        );

        return claimed == 1 ? new SagaLease(sagaId, owner, 0) : null;
    }

    public async Task<SagaLease?> TryClaimAsync(
        Guid sagaId,
        SagaLease? heldLease,
        CancellationToken cancellationToken
    ) {
        var owner = _worker.NewLeaseOwner();
        var leaseSeconds = _policy.LeaseSeconds;
        var maxAttempts = _policy.MaxAttempts;
        var compensating = nameof(SagaState.Compensating);
        var running = nameof(SagaState.Running);
        var stuck = nameof(SagaState.Stuck);

        var held = heldLease?.Owner ?? string.Empty;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE checkout_sagas
               SET state = {compensating},
                   lease_owner = {owner},
                   lease_expires_at = now() + ({leaseSeconds} * interval '1 second'),
                   compensation_attempts = compensation_attempts + 1,
                   updated_at = now()
             WHERE id = {sagaId}
               AND state IN ({running}, {compensating}, {stuck})
               AND (lease_expires_at IS NULL OR lease_expires_at < now() OR lease_owner = {held})
               AND (next_attempt_at IS NULL OR next_attempt_at <= now())
               AND compensation_attempts < {maxAttempts}", cancellationToken
        );

        if (claimed == 0) {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var attemptNo = await _dbContext.Database
            .SqlQuery<int>($@"SELECT compensation_attempts AS ""Value"" FROM checkout_sagas WHERE id = {sagaId}")
            .SingleAsync(cancellationToken);

        await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE checkout_saga_compensation_attempts
               SET finished_at = now(),
                   outcome = 'Crashed',
                   detail = COALESCE(detail, 'no worker finished this round')
             WHERE saga_id = {sagaId} AND finished_at IS NULL", cancellationToken
        );

        var attemptId = Guid.NewGuid();
        try {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO checkout_saga_compensation_attempts
                       (id, saga_id, attempt_no, worker, started_at)
                VALUES ({attemptId}, {sagaId}, {attemptNo}, {owner}, now())", cancellationToken
            );
        } catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(e,
                "two workers both claimed compensation round {Attempt} for saga {Saga}; the lease "
                    + "leaked. This round is being discarded, but check for a paused or partitioned "
                    + "worker before trusting the next one",
                attemptNo, sagaId
            );
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return new SagaLease(sagaId, owner, attemptNo);
    }

    public async Task<bool> AbandonOverBudgetAsync(Guid sagaId, CancellationToken cancellationToken) {
        var abandoned = nameof(SagaState.Abandoned);
        var compensating = nameof(SagaState.Compensating);
        var running = nameof(SagaState.Running);
        var stuck = nameof(SagaState.Stuck);
        var maxAttempts = _policy.MaxAttempts;
        var code = CompensationFailureCode.AttemptsExhausted;

        var abandonedCount = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE checkout_sagas
               SET state = {abandoned},
                   abandoned_at = COALESCE(abandoned_at, now()),
                   needs_attention_at = COALESCE(needs_attention_at, now()),
                   last_failure_code = COALESCE(last_failure_code, {code}),
                   next_attempt_at = NULL,
                   lease_owner = NULL,
                   lease_expires_at = NULL,
                   updated_at = now()
             WHERE id = {sagaId}
               AND state IN ({running}, {compensating}, {stuck})
               AND (lease_expires_at IS NULL OR lease_expires_at < now())
               AND compensation_attempts >= {maxAttempts}", cancellationToken
        );

        return abandonedCount == 1;
    }

    public async Task<bool> HeartbeatAsync(SagaLease lease, CancellationToken cancellationToken) {
        var leaseSeconds = _policy.LeaseSeconds;

        var extended = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE checkout_sagas
               SET lease_expires_at = now() + ({leaseSeconds} * interval '1 second'),
                   updated_at = now()
             WHERE id = {lease.SagaId}
               AND lease_owner = {lease.Owner}
               AND lease_expires_at > now()", cancellationToken
        );

        return extended == 1;
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
        var stateName = state.ToString();
        var abandoned = nameof(SagaState.Abandoned);

        var written = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE checkout_sagas
               SET state = {stateName},
                   failure_reason = NULLIF({failureReason}, ''),
                   last_failure_code = COALESCE(NULLIF({failureCode}, ''), last_failure_code),
                   next_attempt_at = CASE WHEN {delaySeconds} < 0 THEN NULL
                                          ELSE now() + ({delaySeconds} * interval '1 second') END,
                   abandoned_at = CASE WHEN {stateName} = {abandoned} THEN COALESCE(abandoned_at, now())
                                       ELSE abandoned_at END,
                   needs_attention_at = CASE WHEN {needsAttention} OR {stateName} = {abandoned}
                                             THEN COALESCE(needs_attention_at, now())
                                             ELSE needs_attention_at END,
                   lease_owner = NULL,
                   lease_expires_at = NULL,
                   updated_at = now()
             WHERE id = {lease.SagaId}
               AND lease_owner = {lease.Owner}
               AND lease_expires_at > now()", cancellationToken
        );

        return written == 1;
    }

    public async Task<bool> CompleteForwardAsync(SagaLease lease, CancellationToken cancellationToken) {
        var completed = nameof(SagaState.Completed);

        var written = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE checkout_sagas
               SET state = {completed},
                   lease_owner = NULL,
                   lease_expires_at = NULL,
                   updated_at = now()
             WHERE id = {lease.SagaId}
               AND lease_owner = {lease.Owner}
               AND lease_expires_at > now()", cancellationToken
        );

        return written == 1;
    }

    public async Task<bool> TryCountStepDispatchAsync(
        SagaLease lease, Guid stepId, CancellationToken cancellationToken) {

        var counted = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE checkout_saga_steps
               SET compensation_attempts = compensation_attempts + 1,
                   updated_at = now()
             WHERE id = {stepId}
               AND saga_id = {lease.SagaId}
               AND EXISTS (SELECT 1
                             FROM checkout_sagas s
                            WHERE s.id = {lease.SagaId}
                              AND s.lease_owner = {lease.Owner}
                              AND s.lease_expires_at > now())", cancellationToken
        );

        return counted == 1;
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
        var stateName = state.ToString();
        var detailText = detail ?? string.Empty;
        var codeText = failureCode ?? string.Empty;

        var written = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE checkout_saga_steps
               SET state = {stateName},
                   compensated_by_repeat = {compensatedByRepeat},
                   detail = NULLIF({detailText}, ''),
                   last_failure_code = NULLIF({codeText}, ''),
                   updated_at = now()
             WHERE id = {stepId}
               AND saga_id = {lease.SagaId}
               AND EXISTS (SELECT 1
                             FROM checkout_sagas s
                            WHERE s.id = {lease.SagaId}
                              AND s.lease_owner = {lease.Owner}
                              AND s.lease_expires_at > now())", cancellationToken
        );

        return written == 1;
    }

    public async Task RecordAttemptOutcomeAsync(
        SagaLease lease,
        CompensationAttemptOutcome outcome,
        string detail,
        CancellationToken cancellationToken
    ) {
        var outcomeName = outcome.ToString();

        await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE checkout_saga_compensation_attempts
               SET finished_at = now(),
                   outcome = {outcomeName},
                   detail = NULLIF({detail}, '')
             WHERE saga_id = {lease.SagaId}
               AND attempt_no = {lease.AttemptNumber}
               AND finished_at IS NULL", cancellationToken
        );
    }

    public async Task<IReadOnlyList<Guid>> DueForSweepAsync(
        int batchSize,
        int unattendedGraceSeconds,
        CancellationToken cancellationToken
    ) {
        var running = nameof(SagaState.Running);
        var compensating = nameof(SagaState.Compensating);
        var stuck = nameof(SagaState.Stuck);
        var maxAttempts = _policy.MaxAttempts;

        return await _dbContext.Database.SqlQuery<Guid>($@"
            SELECT id AS ""Value""
              FROM checkout_sagas
             WHERE state IN ({running}, {compensating}, {stuck})
               AND (lease_expires_at IS NULL OR lease_expires_at < now())
               AND (
                     compensation_attempts >= {maxAttempts}
                  OR (
                       (next_attempt_at IS NULL OR next_attempt_at <= now())
                       AND (state <> {running}
                            OR updated_at < now() - ({unattendedGraceSeconds} * interval '1 second'))
                     )
                   )
             ORDER BY COALESCE(next_attempt_at, updated_at)
             LIMIT {batchSize}"
        ).ToListAsync(cancellationToken);
    }
}
