using Kinetix.OrderService.Infrastructure.Observability;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class RouteLabelTests {
    [Theory]
    [InlineData("api/v1/orders/{orderId:guid}", "/api/v1/orders/{orderId}")]
    [InlineData("api/v1/orders/{orderId:guid}/status", "/api/v1/orders/{orderId}/status")]
    [InlineData("api/v1/cart/items/{productId}", "/api/v1/cart/items/{productId}")]
    [InlineData("/metrics", "/metrics")]
    [InlineData("health/ready", "/health/ready")]
    public void TheLabelIsTheTemplateWithTheConstraintStrippedOff(string pattern, string expected) {
        Assert.Equal(expected, RouteLabel.Of(RoutePatternFactory.Parse(pattern)));
    }

    [Fact]
    public void ARouteWithNoSegmentsIsStillARoute() {
        Assert.Equal("/", RouteLabel.Of(RoutePatternFactory.Parse("")));
    }

    [Fact]
    public void ACatchAllSaysSoRatherThanLookingLikeAnOrdinaryParameter() {
        Assert.Equal("/files/{*path}", RouteLabel.Of(RoutePatternFactory.Parse("files/{*path}")));
    }

    [Theory]
    [InlineData("api/v1/orders/{orderId?}", "/api/v1/orders/{orderId}")]
    [InlineData("api/v1/orders/{page=1}", "/api/v1/orders/{page}")]
    public void OptionalityAndDefaultsDoNotSplitTheSeries(string pattern, string expected) {
        Assert.Equal(expected, RouteLabel.Of(RoutePatternFactory.Parse(pattern)));
    }
}
