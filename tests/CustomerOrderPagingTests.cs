using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderApplicationService = Kinetix.OrderService.Application.Services.OrderService;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

public class CustomerOrderPagingTests {
    private const string Buyer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";

    private static readonly DateTime PlacedAt = new(2026, 10, 27, 3, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Lower = Guid.Parse("10000000-0000-0000-0000-000000000000");
    private static readonly Guid Middle = Guid.Parse("20000000-0000-0000-0000-000000000000");
    private static readonly Guid Higher = Guid.Parse("30000000-0000-0000-0000-000000000000");
    private static readonly Guid Newest = Guid.Parse("05000000-0000-0000-0000-000000000000");

    [Fact]
    public async Task OrdersPlacedAtTheSameInstantComeBackInOneOrderAcrossPages() {
        using var db = InMemoryDb();
        db.Orders.AddRange(
            AnOrder(Lower, PlacedAt),
            AnOrder(Middle, PlacedAt),
            AnOrder(Newest, PlacedAt.AddSeconds(1)),
            AnOrder(Higher, PlacedAt)
        );
        await db.SaveChangesAsync();

        var service = ServiceOver(db);
        var pages = new List<Guid>();
        for (var page = 1; page <= 4; page++) {
            var read = await service.GetCustomerOrdersAsync(Buyer, null, page, 1);
            pages.Add(read.Orders.Single().Id);
        }

        Assert.Equal([Newest, Higher, Middle, Lower], pages);
    }

    [Fact]
    public async Task AStatusFilterKeepsTheSameOrderForTies() {
        using var db = InMemoryDb();
        db.Orders.AddRange(
            AnOrder(Lower, PlacedAt),
            AnOrder(Higher, PlacedAt),
            AnOrder(Middle, PlacedAt, OrderStatus.CANCELLED)
        );
        await db.SaveChangesAsync();

        var read = await ServiceOver(db).GetCustomerOrdersAsync(Buyer, OrderStatus.PAID, 1, 10);

        Assert.Equal([Higher, Lower], read.Orders.Select(o => o.Id));
        Assert.Equal(2, read.TotalCount);
    }

    private static OrderEntity AnOrder(Guid id, DateTime createdAt, OrderStatus status = OrderStatus.PAID) =>
        new() {
            Id = id,
            OrderNumber = $"ORD-TIE-{id:N}",
            CustomerPrincipalId = Buyer,
            MerchantPrincipalId = Merchant,
            Status = status,
            ShippingAddress = "Jl. Sudirman No. 45, Jakarta",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

    private static OrderDbContext InMemoryDb() =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options
        );

    private static OrderApplicationService ServiceOver(OrderDbContext db) =>
        new(
            db,
            new Mock<ICartService>().Object,
            new Mock<IProductDirectory>().Object,
            new Mock<IMerchantStanding>().Object,
            new Mock<IPricingClient>().Object,
            new Mock<IShippingClient>().Object,
            new Mock<IAddressDirectory>().Object,
            new Mock<IAddressGeocoder>().Object,
            null!,
            NullLogger<OrderApplicationService>.Instance
        );
}
