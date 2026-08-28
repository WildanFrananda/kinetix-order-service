using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Controllers;

namespace Kinetix.OrderService.Tests;

public class CartControllerTests {
    [Fact]
    public async Task GetCart_MissingXUserIdHeader_ReturnsUnauthorized() {
        // Arrange
        var mockCartService = new Mock<ICartService>();
        var controller = new CartController(mockCartService.Object) {
            ControllerContext = new ControllerContext {
                HttpContext = new DefaultHttpContext()
            }
        };

        // Act
        var result = await controller.GetCart();

        // Assert
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, unauthorizedResult.StatusCode);
    }
}
