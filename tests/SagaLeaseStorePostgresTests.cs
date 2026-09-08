using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class SagaLeaseStorePostgresTests {
    private const string Customer = "f59fd296-a50f-4a32-970f-a1d1fddd76ae";

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("KINETIX_TEST_POSTGRES");

    private static OrderDbContext NewDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(connectionString)
            .Options
        );

    private static SagaLeaseStore StoreFor(OrderDbContext db, CompensationPolicy policy) =>
        new(db, new SagaWorkerIdentity(), policy, NullLogger<SagaLeaseStore>.Instance);

    private static async Task<Guid> SeedAsync(
        OrderDbContext db,
        SagaState state,
        int attempts,
        string orderNumber
    ) {
        var stale = await db.CheckoutSagas.FirstOrDefaultAsync(s => s.OrderNumber == orderNumber);
        if (stale is not null) {
            db.CheckoutSagas.Remove(stale);
            await db.SaveChangesAsync();
        }

        var saga = new CheckoutSaga {
            OrderNumber = orderNumber,
            CustomerPrincipalId = Customer,
            State = state,
            CompensationAttempts = attempts,
            CorrelationId = "postgres-test",
        };
        db.CheckoutSagas.Add(saga);
        await db.SaveChangesAsync();
        return saga.Id;
    }

    [Fact]
    public async Task TwoWorkersRacingForOneSagaLeaveExactlyOneClaimAndOneIncrement() {
        if (ConnectionString is null) {
            return;
        }

        var policy = new CompensationPolicy();
        using var setup = NewDbContext(ConnectionString);
        await setup.Database.MigrateAsync();
        var sagaId = await SeedAsync(setup, SagaState.Stuck, 0, "ORD-PGTEST-RACE");

        using var dbA = NewDbContext(ConnectionString);
        using var dbB = NewDbContext(ConnectionString);

        var both = await Task.WhenAll(
            StoreFor(dbA, policy).TryClaimAsync(sagaId, null, CancellationToken.None),
            StoreFor(dbB, policy).TryClaimAsync(sagaId, null, CancellationToken.None)
        );

        var winners = both.Where(l => l is not null).ToList();
        Assert.Single(winners);

        var reloaded = await setup.CheckoutSagas.AsNoTracking().SingleAsync(s => s.Id == sagaId);
        Assert.Equal(1, reloaded.CompensationAttempts);
        Assert.Equal(SagaState.Compensating, reloaded.State);
        Assert.Equal(1, winners[0]!.AttemptNumber);

        var attempts = await setup.CompensationAttempts.AsNoTracking()
            .Where(a => a.SagaId == sagaId).ToListAsync();
        Assert.Single(attempts);
    }

    [Fact]
    public async Task EveryWriteBySupersededOwnerMatchesNothing() {
        if (ConnectionString is null) {
            return;
        }

        var policy = new CompensationPolicy();
        using var db = NewDbContext(ConnectionString);
        await db.Database.MigrateAsync();
        var sagaId = await SeedAsync(db, SagaState.Stuck, 0, "ORD-PGTEST-FENCE");

        var store = StoreFor(db, policy);
        var first = await store.TryClaimAsync(sagaId, null, CancellationToken.None);
        Assert.NotNull(first);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE checkout_sagas SET lease_expires_at = now() - interval '1 second' WHERE id = {sagaId}"
        );
        var second = await store.TryClaimAsync(sagaId, null, CancellationToken.None);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Owner, second!.Owner);

        Assert.False(await store.HeartbeatAsync(first, CancellationToken.None));
        Assert.False(await store.CompleteForwardAsync(first, CancellationToken.None));
        Assert.False(await store.FinishCompensationAsync(
            first, SagaState.Compensated, "zombie", "", -1, false, CancellationToken.None
        ));

        Assert.True(await store.HeartbeatAsync(second, CancellationToken.None));

        var reloaded = await db.CheckoutSagas.AsNoTracking().SingleAsync(s => s.Id == sagaId);
        Assert.Equal(SagaState.Compensating, reloaded.State);
        Assert.Equal(second.Owner, reloaded.LeaseOwner);
    }

    [Fact]
    public async Task ASupersededOwnerCannotWriteAStepEither() {
        if (ConnectionString is null) {
            return;
        }

        var policy = new CompensationPolicy();
        using var db = NewDbContext(ConnectionString);
        await db.Database.MigrateAsync();
        var sagaId = await SeedAsync(db, SagaState.Stuck, 0, "ORD-PGTEST-STEPFENCE");
        var otherSagaId = await SeedAsync(db, SagaState.Stuck, 0, "ORD-PGTEST-STEPFENCE-OTHER");

        var step = new CheckoutSagaStep {
            SagaId = sagaId,
            Name = SagaStepName.CreateEscrowHold,
            Reference = "ORD-PGTEST-STEPFENCE",
            Quantity = 1,
            State = SagaStepState.Attempting,
        };
        db.CheckoutSagaSteps.Add(step);
        await db.SaveChangesAsync();
        db.Entry(step).State = EntityState.Detached;

        var store = StoreFor(db, policy);
        var first = await store.TryClaimAsync(sagaId, null, CancellationToken.None);
        Assert.NotNull(first);

        Assert.True(await store.TryCountStepDispatchAsync(first!, step.Id, CancellationToken.None));

        Assert.False(await store.TryCountStepDispatchAsync(
            new SagaLease(otherSagaId, first.Owner, first.AttemptNumber), step.Id,
            CancellationToken.None
        ));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE checkout_sagas SET lease_expires_at = now() - interval '1 second' WHERE id = {sagaId}"
        );
        var second = await store.TryClaimAsync(sagaId, null, CancellationToken.None);
        Assert.NotNull(second);

        Assert.False(await store.TryCountStepDispatchAsync(first, step.Id, CancellationToken.None));
        Assert.False(await store.TryRecordStepOutcomeAsync(
            first, step.Id, SagaStepState.Compensated, true, "zombie",
            CompensationFailureCode.EscrowAbsent, CancellationToken.None
        ));

        var untouched = await db.CheckoutSagaSteps.AsNoTracking().SingleAsync(s => s.Id == step.Id);
        Assert.Equal(SagaStepState.Attempting, untouched.State);
        Assert.Equal(1, untouched.CompensationAttempts);
        Assert.Null(untouched.LastFailureCode);
        Assert.Null(untouched.Detail);

        Assert.True(await store.TryRecordStepOutcomeAsync(
            second!, step.Id, SagaStepState.Compensated, true, "given back",
            CompensationFailureCode.EscrowAbsent, CancellationToken.None
        ));

        var written = await db.CheckoutSagaSteps.AsNoTracking().SingleAsync(s => s.Id == step.Id);
        Assert.Equal(SagaStepState.Compensated, written.State);
        Assert.True(written.CompensatedByRepeat);
        Assert.Equal(CompensationFailureCode.EscrowAbsent, written.LastFailureCode);
    }

    [Fact]
    public async Task AnAttemptRowStampedCrashedIsNotRewrittenByTheWorkerThatCrashed() {
        if (ConnectionString is null) {
            return;
        }

        var policy = new CompensationPolicy();
        using var db = NewDbContext(ConnectionString);
        await db.Database.MigrateAsync();
        var sagaId = await SeedAsync(db, SagaState.Stuck, 0, "ORD-PGTEST-ATTEMPTROW");

        var store = StoreFor(db, policy);
        var first = await store.TryClaimAsync(sagaId, null, CancellationToken.None);
        Assert.NotNull(first);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE checkout_sagas SET lease_expires_at = now() - interval '1 second' WHERE id = {sagaId}"
        );
        Assert.NotNull(await store.TryClaimAsync(sagaId, null, CancellationToken.None));

        await store.RecordAttemptOutcomeAsync(
            first!, CompensationAttemptOutcome.LeaseLost, "noticed late", CancellationToken.None);

        var attempt = await db.CompensationAttempts.AsNoTracking()
            .SingleAsync(a => a.SagaId == sagaId && a.AttemptNo == first.AttemptNumber
        );
        Assert.Equal(CompensationAttemptOutcome.Crashed, attempt.Outcome);
        Assert.Equal("no worker finished this round", attempt.Detail);
    }

    [Fact]
    public async Task ALaterCleanRoundDoesNotEraseTheFailureCodeOnTheSagaRow() {
        if (ConnectionString is null) {
            return;
        }

        var policy = new CompensationPolicy();
        using var db = NewDbContext(ConnectionString);
        await db.Database.MigrateAsync();
        var sagaId = await SeedAsync(db, SagaState.Stuck, 0, "ORD-PGTEST-STICKY");

        var store = StoreFor(db, policy);
        var first = await store.TryClaimAsync(sagaId, null, CancellationToken.None);
        Assert.True(await store.FinishCompensationAsync(
            first!, SagaState.Stuck, "a leg did not come back", CompensationFailureCode.EscrowAbsent,
            0, needsAttention: true, CancellationToken.None)
        );

        var second = await store.TryClaimAsync(sagaId, null, CancellationToken.None);
        Assert.NotNull(second);
        Assert.True(await store.FinishCompensationAsync(
            second!, SagaState.Compensated, "a leg did not come back", string.Empty, -1,
            needsAttention: false, CancellationToken.None
        ));

        var reloaded = await db.CheckoutSagas.AsNoTracking().SingleAsync(s => s.Id == sagaId);
        Assert.Equal(SagaState.Compensated, reloaded.State);
        Assert.NotNull(reloaded.NeedsAttentionAt);
        Assert.Equal(CompensationFailureCode.EscrowAbsent, reloaded.LastFailureCode);
    }

    [Fact]
    public async Task ALiveLeaseIsHandedOnOnlyToTheWorkerThatAlreadyHoldsIt() {
        if (ConnectionString is null) {
            return;
        }

        var policy = new CompensationPolicy();
        using var db = NewDbContext(ConnectionString);
        await db.Database.MigrateAsync();
        var sagaId = await SeedAsync(db, SagaState.Running, 0, "ORD-PGTEST-HANDOFF");

        var store = StoreFor(db, policy);
        var forward = await store.TryAcquireForwardAsync(sagaId, CancellationToken.None);
        Assert.NotNull(forward);

        Assert.Null(await store.TryClaimAsync(sagaId, null, CancellationToken.None));

        var impostor = new SagaLease(sagaId, forward!.Owner + "-not-really", 0);
        Assert.Null(await store.TryClaimAsync(sagaId, impostor, CancellationToken.None));

        var claimed = await store.TryClaimAsync(sagaId, forward, CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(1, claimed!.AttemptNumber);
    }

    [Fact]
    public async Task ASagaWhoseEveryRoundCrashedIsAbandonedRatherThanRejectedForever() {
        if (ConnectionString is null) {
            return;
        }

        var policy = new CompensationPolicy(maxAttempts: 2);
        using var db = NewDbContext(ConnectionString);
        await db.Database.MigrateAsync();
        var sagaId = await SeedAsync(db, SagaState.Compensating, 2, "ORD-PGTEST-BUDGET");

        var store = StoreFor(db, policy);

        Assert.Null(await store.TryClaimAsync(sagaId, null, CancellationToken.None));
        Assert.True(await store.AbandonOverBudgetAsync(sagaId, CancellationToken.None));
        Assert.False(await store.AbandonOverBudgetAsync(sagaId, CancellationToken.None));

        var reloaded = await db.CheckoutSagas.AsNoTracking().SingleAsync(s => s.Id == sagaId);
        Assert.Equal(SagaState.Abandoned, reloaded.State);
        Assert.Equal(CompensationFailureCode.AttemptsExhausted, reloaded.LastFailureCode);
        Assert.NotNull(reloaded.AbandonedAt);
        Assert.NotNull(reloaded.NeedsAttentionAt);
        Assert.Null(reloaded.NextAttemptAt);
    }
}
