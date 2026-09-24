using Kinetix.OrderService.Security;

namespace Kinetix.OrderService.Tests;

public class SpiffeTrustDomainTests {
    [Fact]
    public void AnUnsetVariableKeepsTheDomainTheEstateRunsToday() {
        Assert.Equal("kinetix.local", SpiffePeer.TrustDomain);
    }

    [Fact]
    public void AnIdFromTheConfiguredDomainNamesItsService() {
        Assert.Equal("warehouse", SpiffePeer.ServiceOf("spiffe://kinetix.local/service/warehouse", "kinetix.local"));
    }

    [Fact]
    public void ADifferentDomainCanBeConfiguredWithoutTouchingThisCode() {
        Assert.Equal("warehouse", SpiffePeer.ServiceOf("spiffe://prod.kinetix/service/warehouse", "prod.kinetix"));
    }

    [Fact]
    public void AnIdFromAnotherTrustDomainNamesNobody() {
        Assert.Null(SpiffePeer.ServiceOf("spiffe://prod.kinetix/service/warehouse", "kinetix.local"));
        Assert.Null(SpiffePeer.ServiceOf("spiffe://kinetix.local/service/warehouse", "prod.kinetix"));
    }

    [Fact]
    public void ADomainThisOneIsMerelyAPrefixOfIsRefused() {
        Assert.Null(SpiffePeer.ServiceOf("spiffe://kinetix.local.example.com/service/warehouse", "kinetix.local"));
    }

    [Fact]
    public void AnIdThatNamesNoServiceIsRefused() {
        Assert.Null(SpiffePeer.ServiceOf("spiffe://kinetix.local/warehouse", "kinetix.local"));
        Assert.Null(SpiffePeer.ServiceOf(null, "kinetix.local"));
    }
}
