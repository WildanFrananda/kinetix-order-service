using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Search;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Responses;
using Moq;

namespace Kinetix.OrderService.Tests;

public class OrderSearchTests {
    private static readonly DateTime PlacedAt = new(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc);

    private static OrderSearchQuery Limited(int limit) => new(
        new HashSet<OrderStatus>(),
        null,
        null,
        null,
        null,
        null,
        null,
        limit
    );

    private static OrderSummaryResponse Row(int minutesAgo) => new(
        Guid.NewGuid(),
        $"ORD-20261110-{minutesAgo:D4}",
        "PAID",
        "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7",
        "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3",
        165000m,
        15000m,
        1,
        PlacedAt.AddMinutes(-minutesAgo),
        PlacedAt.AddMinutes(-minutesAgo),
        null,
        null
    );

    private static Mock<IOrderSearchRepository> Holding(params OrderSummaryResponse[] rows) {
        var repository = new Mock<IOrderSearchRepository>();
        repository
            .Setup(r => r.NewestFirstAsync(
                It.IsAny<OrderSearchQuery>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync((OrderSearchQuery _, int take, CancellationToken _) => [.. rows.Take(take)]);

        return repository;
    }

    [Fact]
    public async Task TheSearchAsksForOneRowMoreThanThePageToKnowWhetherAnotherPageExists() {
        var repository = Holding();

        await new OrderSearch(repository.Object).SearchAsync(Limited(25), CancellationToken.None);

        repository.Verify(
            r => r.NewestFirstAsync(It.Is<OrderSearchQuery>(q => q.Limit == 25), 26, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task MoreRowsThanThePageHoldsGiveACursorAtTheLastRowShown() {
        var rows = new[] { Row(1), Row(2), Row(3) };

        var page = await new OrderSearch(Holding(rows).Object).SearchAsync(Limited(2), CancellationToken.None);

        Assert.Equal([rows[0].Id, rows[1].Id], page.Orders.Select(o => o.Id));
        Assert.Equal(new OrderSearchCursor(rows[1].CreatedAt, rows[1].Id), page.Next);
    }

    [Fact]
    public async Task APageThatHoldsEveryRemainingRowIsTheLastAndHasNoCursor() {
        var rows = new[] { Row(1), Row(2) };

        var page = await new OrderSearch(Holding(rows).Object).SearchAsync(Limited(2), CancellationToken.None);

        Assert.Equal(2, page.Orders.Count);
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task NoOrdersIsAnEmptyLastPage() {
        var page = await new OrderSearch(Holding().Object).SearchAsync(Limited(50), CancellationToken.None);

        Assert.Empty(page.Orders);
        Assert.Null(page.Next);
    }
}
