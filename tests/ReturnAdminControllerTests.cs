using System.Reflection;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Returns;
using Kinetix.OrderService.Controllers;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Requests;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Persistence;
using Kinetix.OrderService.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Kinetix.OrderService.Tests;

public class ReturnAdminControllerTests {
    private static OrderDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options
        );

    private static OrderReturn Return(string number, ReturnStatus status, int openedDaysAgo, DateTime? nextRefund = null) => new() {
        ReturnNumber = number,
        OrderNumber = $"ORD-{number}",
        MerchantPrincipalId = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
        Reason = "Wrong size",
        Status = status,
        OpenedAt = DateTime.UtcNow.AddDays(-openedDaysAgo),
        NextRefundAttemptAt = nextRefund,
    };

    [Fact]
    public async Task OnlyReturnsHoldingAnOrderOpenWithNothingInMotionNeedAttentionOldestFirst() {
        using var db = NewDbContext();
        db.OrderReturns.AddRange(
            Return("OPEN-NEW", ReturnStatus.OPEN, 2),
            Return("OPEN-OLD", ReturnStatus.OPEN, 20),
            Return("HALTED", ReturnStatus.GOODS_RECEIVED, 10),
            Return("RETRYING", ReturnStatus.GOODS_RECEIVED, 10, DateTime.UtcNow.AddMinutes(5)),
            Return("DONE", ReturnStatus.RESOLVED, 30),
            Return("REFUSED", ReturnStatus.REJECTED, 30)
        );
        await db.SaveChangesAsync();

        var result = await new ReturnAdminController(db, Mock.Of<IReturnRejection>()).NeedsAttention();

        var body = Assert.IsType<List<ReturnNeedingAttentionResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["OPEN-OLD", "HALTED", "OPEN-NEW"], body.Select(r => r.ReturnNumber));
    }

    [Theory]
    [InlineData(ReturnRejectionStatus.Rejected, typeof(NoContentResult))]
    [InlineData(ReturnRejectionStatus.NoSuchReturn, typeof(NotFoundObjectResult))]
    [InlineData(ReturnRejectionStatus.NoReasonGiven, typeof(BadRequestObjectResult))]
    [InlineData(ReturnRejectionStatus.AlreadyRejected, typeof(ConflictObjectResult))]
    [InlineData(ReturnRejectionStatus.GoodsAlreadyReceived, typeof(ConflictObjectResult))]
    public async Task ARejectionAnswersWhatHappened(ReturnRejectionStatus status, Type expected) {
        using var db = NewDbContext();
        var rejection = new Mock<IReturnRejection>();
        rejection.Setup(r => r.RejectAsync("RMA-1", "why", It.IsAny<CancellationToken>())).ReturnsAsync(status);

        var result = await new ReturnAdminController(db, rejection.Object)
            .Reject("RMA-1", new RejectReturnRequest("why"), CancellationToken.None);

        Assert.IsType(expected, result);
    }

    [Theory]
    [InlineData(nameof(ReturnAdminController.NeedsAttention), SagaAdminController.SagaOperatorPolicy)]
    [InlineData(nameof(ReturnAdminController.Reject), OrderPolicies.MerchantPayout)]
    public void EachRouteNamesItsPolicy(string action, string policy) {
        var attribute = typeof(ReturnAdminController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(policy, attribute.Policy);
    }
}
