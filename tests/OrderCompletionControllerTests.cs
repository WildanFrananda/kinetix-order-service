using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Controllers;
using Kinetix.OrderService.DTOs.Responses;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Kinetix.OrderService.Tests;

public class OrderCompletionControllerTests {
    private static async Task<IActionResult> Answer(CompletionOutcome outcome) {
        var completion = new Mock<IOrderCompletion>();
        completion.Setup(c => c.CompleteAsync("ORD-1", false, It.IsAny<CancellationToken>())).ReturnsAsync(outcome);

        return await new OrderCompletionController(completion.Object).Complete("ORD-1", CancellationToken.None);
    }

    [Fact]
    public async Task AnAdminCompletionNeverWaitsForTheWindowAndSaysWhetherTheMerchantWasPaid() {
        var result = await Answer(new CompletionOutcome(CompletionStatus.Completed, false, "payment is restarting"));

        var body = Assert.IsType<OrderCompletionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(body.EscrowReleased);
        Assert.Equal("payment is restarting", body.Detail);
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
