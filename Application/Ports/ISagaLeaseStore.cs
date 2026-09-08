using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Application.Ports;

public interface ISagaLeaseStore {
    Task<SagaLease?> TryAcquireForwardAsync(Guid sagaId, CancellationToken cancellationToken);

    Task<SagaLease?> TryClaimAsync(
        Guid sagaId, SagaLease? heldLease, CancellationToken cancellationToken
    );

    Task<bool> AbandonOverBudgetAsync(Guid sagaId, CancellationToken cancellationToken);

    Task<bool> HeartbeatAsync(SagaLease lease, CancellationToken cancellationToken);

    Task<bool> FinishCompensationAsync(
        SagaLease lease,
        SagaState state,
        string failureReason,
        string failureCode,
        int delaySeconds,
        bool needsAttention,
        CancellationToken cancellationToken
    );

    Task<bool> CompleteForwardAsync(SagaLease lease, CancellationToken cancellationToken);

    Task<bool> TryCountStepDispatchAsync(
        SagaLease lease, Guid stepId, CancellationToken cancellationToken
    );

    Task<bool> TryRecordStepOutcomeAsync(
        SagaLease lease,
        Guid stepId,
        SagaStepState state,
        bool compensatedByRepeat,
        string? detail,
        string? failureCode,
        CancellationToken cancellationToken);

    Task RecordAttemptOutcomeAsync(
        SagaLease lease,
        CompensationAttemptOutcome outcome,
        string detail,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<Guid>> DueForSweepAsync(
        int batchSize,
        int unattendedGraceSeconds,
        CancellationToken cancellationToken
    );
}
