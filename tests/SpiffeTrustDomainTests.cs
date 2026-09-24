using Kinetix.OrderService.Security;

namespace Kinetix.OrderService.Tests;

public class SpiffeTrustDomainTests {
    [Fact]
    public void AnUnsetVariableKeepsTheDomainTheEstateRunsToday() {
        Assert.Equal("kinetix.local", SpiffePeer.TrustDomain);
        Assert.Equal(["kinetix.local"], SpiffePeer.TrustDomains);
    }

    [Fact]
    public void ACutoverCanAcceptBothDomainsAtOnce() {
        string[] both = ["kinetix.local", "prod.kinetix"];

        foreach (var domain in both) {
            var id = $"spiffe://{domain}/service/order";
            var named = both.Select(d => SpiffePeer.ServiceOf(id, d)).FirstOrDefault(n => n is not null);
            Assert.Equal("order", named);
        }
    }

    [Fact]
    public void ADomainOutsideTheListIsStillRefused() {
        string[] accepted = ["kinetix.local", "prod.kinetix"];
        const string id = "spiffe://staging.kinetix/service/order";

        Assert.All(accepted, d => Assert.Null(SpiffePeer.ServiceOf(id, d)));
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
