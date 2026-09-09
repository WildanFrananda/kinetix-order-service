using Kinetix.OrderService.Infrastructure.Observability;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class HttpMethodLabelTests {

    [Theory]
    [InlineData("GET", "GET")]
    [InlineData("get", "GET")]
    [InlineData("DELETE", "DELETE")]
    public void AKnownMethodKeepsItsName(string method, string expected) {
        Assert.Equal(expected, HttpMethodLabel.Of(method));
    }

    [Theory]
    [InlineData("PROPFIND")]
    [InlineData("QUERY-8f3a1b2c")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseCollapsesToOneLabel(string? method) {
        Assert.Equal(HttpMethodLabel.Other, HttpMethodLabel.Of(method));
    }
}
