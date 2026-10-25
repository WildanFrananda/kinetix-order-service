using System.Text.Json;
using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Controllers;
using Kinetix.OrderService.DTOs.Responses;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Kinetix.OrderService.Tests;

public class OrderCompletionControllerTests {
    private const string Admin = "0b6f1c7e-2d4a-4e8b-9c3f-5a1d7e9b2c40";

    private static async Task<IActionResult> Answer(CompletionOutcome outcome) {
        var completion = new Mock<IOrderCompletion>();
        completion
            .Setup(c => c.CompleteAsync("ORD-1", false, LifecycleActor.Admin(Admin), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var controller = new OrderCompletionController(completion.Object, TestAudit.Silent()) {
            ControllerContext = SignedIn.As(Admin),
        };

        return await controller.Complete("ORD-1", CancellationToken.None);
    }

    [Fact]
    public async Task ACompletionWhoseTokenNamesNobodyIsRefusedBeforeAnythingChanges() {
        var completion = new Mock<IOrderCompletion>(MockBehavior.Strict);
        var controller = new OrderCompletionController(completion.Object, TestAudit.Silent()) {
            ControllerContext = SignedIn.As(null),
        };

        var result = await controller.Complete("ORD-1", CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task AnAdminCompletionNeverWaitsForTheWindowAndSaysWhetherTheMerchantWasPaid() {
        var result = await Answer(
            new CompletionOutcome(CompletionStatus.Completed, false, OrderCompletion.ReleaseRetried)
        );

        var body = Assert.IsType<OrderCompletionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(body.EscrowReleased);
        Assert.Equal(OrderCompletion.ReleaseRetried, body.Detail);
    }

    [Fact]
    public async Task AnAlreadyCompletedOrderPromisesNoRetryForAReleaseThatWaitsForStaff() {
        var result = await Answer(new CompletionOutcome(CompletionStatus.AlreadyCompleted, false, null));

        var body = JsonSerializer.Serialize(Assert.IsType<ConflictObjectResult>(result).Value);
        Assert.Contains("ORDER_ALREADY_COMPLETED", body);
        Assert.DoesNotContain("until payment accepts", body);
        Assert.Contains("staff", body);
    }

    [Theory]
    [InlineData(CompletionStatus.AlreadyCompleted, typeof(ConflictObjectResult))]
    [InlineData(CompletionStatus.NotDelivered, typeof(ConflictObjectResult))]
    [InlineData(CompletionStatus.ReturnUnresolved, typeof(ConflictObjectResult))]
    [InlineData(CompletionStatus.NoSuchOrder, typeof(NotFoundObjectResult))]
    public async Task ARefusalIsNotReportedAsSuccess(CompletionStatus status, Type expected) {
        var result = await Answer(new CompletionOutcome(status, false, "because"));

        Assert.IsType(expected, result);
    }
}
