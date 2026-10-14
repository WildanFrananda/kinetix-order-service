using Kinetix.OrderService.Security;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class PeerAllowListTests {
    private const string Estate =
        "review@order.v1.OrderService/GetOrderDetails,"
      + "warehouse@order.v1.OrderService/FulfillmentPacked,"
      + "warehouse@order.v1.OrderService/OpenReturn,"
      + "warehouse@order.v1.OrderService/ReturnGoodsReceived,"
      + "matching@order.v1.OrderService/OrderDelivered,"
      + "search@order.v1.OrderService/OrdersChangedSince";

    private static readonly string[] OrderMethods = [
        "GetOrderDetails",
        "ListOrdersForPrincipal",
        "FulfillmentPacked",
        "OrderDelivered",
        "OpenReturn",
        "ReturnGoodsReceived",
        "OrdersChangedSince",
    ];

    [Theory]
    [InlineData("review", "GetOrderDetails")]
    [InlineData("warehouse", "FulfillmentPacked,OpenReturn,ReturnGoodsReceived")]
    [InlineData("matching", "OrderDelivered")]
    [InlineData("search", "OrdersChangedSince")]
    public void EachPeerReachesOnlyTheMethodsItIsGranted(string peer, string granted) {
        var allowed = PeerAllowList.Parse(Estate);
        var grants = granted.Split(',').ToHashSet(StringComparer.Ordinal);

        foreach (var method in OrderMethods) {
            Assert.Equal(
                grants.Contains(method),
                allowed.Permits(peer, $"/order.v1.OrderService/{method}")
            );
        }
    }

    [Fact]
    public void AServiceOffTheListReachesNothing() {
        var allowed = PeerAllowList.Parse(Estate);

        Assert.False(allowed.Names("payment"));
        Assert.False(allowed.Permits("payment", "/order.v1.OrderService/GetOrderDetails"));
        Assert.False(allowed.Permits("payment", "/grpc.reflection.v1.ServerReflection/ServerReflectionInfo"));
    }

    [Fact]
    public void APeerOnTheListMayReadTheSchema() {
        var allowed = PeerAllowList.Parse(Estate);

        Assert.True(allowed.Permits("review", "/grpc.reflection.v1.ServerReflection/ServerReflectionInfo"));
        Assert.True(allowed.Permits("review", "/grpc.reflection.v1alpha.ServerReflection/ServerReflectionInfo"));
    }

    [Fact]
    public void ABarePeerReachesEveryMethod() {
        var allowed = PeerAllowList.Parse("review");

        foreach (var method in OrderMethods) {
            Assert.True(allowed.Permits("review", $"/order.v1.OrderService/{method}"));
        }
    }

    [Fact]
    public void AServiceScopeReachesEveryMethodOfThatServiceOnly() {
        var allowed = PeerAllowList.Parse("review@order.v1.OrderService");

        Assert.True(allowed.Permits("review", "/order.v1.OrderService/OrderDelivered"));
        Assert.False(allowed.Permits("review", "/payment.v1.PaymentService/RefundGoods"));
    }

    [Fact]
    public void ABareEntryWinsOverAScopedOneInEitherOrder() {
        var scopedFirst = PeerAllowList.Parse("review@order.v1.OrderService/GetOrderDetails,review");
        var bareFirst = PeerAllowList.Parse("review,review@order.v1.OrderService/GetOrderDetails");

        Assert.True(scopedFirst.Permits("review", "/order.v1.OrderService/OrderDelivered"));
        Assert.True(bareFirst.Permits("review", "/order.v1.OrderService/OrderDelivered"));
    }

    [Theory]
    [InlineData("@order.v1.OrderService/GetOrderDetails")]
    [InlineData("review@")]
    public void AnEntryWithoutAPeerOrAScopeIsRefusedAtStartup(string entry) {
        Assert.Throws<InvalidOperationException>(() => PeerAllowList.Parse(entry));
    }
}
