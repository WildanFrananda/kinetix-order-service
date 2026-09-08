using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Controllers;
using Kinetix.OrderService.DTOs;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class SagaAdminControllerTests {
    private const string Customer = "f59fd296-a50f-4a32-970f-a1d1fddd76ae";

    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options
        );

    private static List<SagaNeedingAttentionResponse> Body(
        ActionResult<List<SagaNeedingAttentionResponse>> result) =>
        (List<SagaNeedingAttentionResponse>)((OkObjectResult)result.Result!).Value!;

    [Fact]
    public async Task AStepThatIsUnwoundButStillCarriesACodeIsShownWithItsRemedy() {
        using var db = NewDbContext();

        var saga = new CheckoutSaga {
            OrderNumber = "ORD-ADMIN-0001",
            CustomerPrincipalId = Customer,
            State = SagaState.Compensated,
            CompensationAttempts = 2,
            LastFailureCode = CompensationFailureCode.EscrowAbsent,
            FailureReason = "payment did not answer the hold in time",
            NeedsAttentionAt = DateTime.UtcNow,
        };
        db.CheckoutSagas.Add(saga);
        db.Orders.Add(new OrderEntity {
            OrderNumber = "ORD-ADMIN-0001",
            CustomerPrincipalId = Customer,
            Status = OrderStatus.CANCELLED,
        });

        db.CheckoutSagaSteps.Add(new CheckoutSagaStep {
            SagaId = saga.Id,
            Name = SagaStepName.CreateEscrowHold,
            Reference = "ORD-ADMIN-0001",
            Quantity = 1,
            State = SagaStepState.Compensated,
            CompensationAttempts = 2,
            LastFailureCode = CompensationFailureCode.EscrowAbsent,
            Detail = "payment confirmed twice that it held nothing to refund",
        });

        db.CheckoutSagaSteps.Add(new CheckoutSagaStep {
            SagaId = saga.Id,
            Name = SagaStepName.ReserveStock,
            Reference = "SKU-1",
            Quantity = 1,
            State = SagaStepState.Compensated,
        });
        await db.SaveChangesAsync();

        var body = Body(await new SagaAdminController(db).NeedsAttention());

        var row = Assert.Single(body);
        Assert.Equal("CANCELLED", row.OrderStatus);
        Assert.Equal(CompensationFailureCode.EscrowAbsent, row.LastFailureCode);

        var step = Assert.Single(row.UnreleasedSteps);
        Assert.Equal(nameof(SagaStepName.CreateEscrowHold), step.Name);
        Assert.Equal(CompensationFailureCode.EscrowAbsent, step.LastFailureCode);
        Assert.Contains("RefundEscrow", step.Remedy);
        Assert.Contains("idempotency_key", step.Remedy);
    }

    [Fact]
    public async Task AStepStillHeldIsShownWhetherOrNotItHasACode() {
        using var db = NewDbContext();

        var saga = new CheckoutSaga {
            OrderNumber = "ORD-ADMIN-0002",
            CustomerPrincipalId = Customer,
            State = SagaState.Abandoned,
            CompensationAttempts = 6,
            NeedsAttentionAt = DateTime.UtcNow,
            AbandonedAt = DateTime.UtcNow,
        };
        db.CheckoutSagas.Add(saga);
        db.CheckoutSagaSteps.Add(new CheckoutSagaStep {
            SagaId = saga.Id,
            Name = SagaStepName.ReserveStock,
            Reference = "SKU-9",
            Quantity = 3,
            MerchantPrincipalId = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
            State = SagaStepState.Done,
        });
        await db.SaveChangesAsync();

        var row = Assert.Single(Body(await new SagaAdminController(db).NeedsAttention()));
        var step = Assert.Single(row.UnreleasedSteps);
        Assert.Equal("SKU-9", step.Reference);
        Assert.Null(step.LastFailureCode);
    }

    [Fact]
    public async Task ASagaNobodyHasToLookAtIsNotInTheQueue() {
        using var db = NewDbContext();

        db.CheckoutSagas.Add(new CheckoutSaga {
            OrderNumber = "ORD-ADMIN-0003",
            CustomerPrincipalId = Customer,
            State = SagaState.Compensated,
        });
        await db.SaveChangesAsync();

        var controller = new SagaAdminController(db);
        Assert.Empty(Body(await controller.NeedsAttention()));
        Assert.Equal(0, ((OkObjectResult)(await controller.NeedsAttentionCount(CancellationToken.None)).Result!).Value);
    }
}
