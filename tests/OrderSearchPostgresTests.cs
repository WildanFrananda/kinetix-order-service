using System.Globalization;
using Kinetix.OrderService.Application.Search;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class OrderSearchPostgresTests {
    private static readonly DateTime Window = new(2199, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Guid LowId = Guid.Parse("10000000-0000-0000-0000-000000000000");
    private static readonly Guid BehindId = Guid.Parse("70000000-0000-0000-0000-000000000000");
    private static readonly Guid MiddleId = Guid.Parse("80000000-0000-0000-0000-000000000000");
    private static readonly Guid AheadId = Guid.Parse("90000000-0000-0000-0000-000000000000");
    private static readonly Guid HighId = Guid.Parse("f0000000-0000-0000-0000-000000000000");

    private static string? ConnectionString => PostgresDatabase.ConnectionString;

    private static string NewPrincipal() => Guid.NewGuid().ToString();

    private static string Instant(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static OrderEntity AnOrder(
        DateTime createdAt,
        string merchant,
        string customer,
        OrderStatus status = OrderStatus.PAID,
        Guid? id = null,
        int lines = 1
    ) {
        var orderId = id ?? Guid.NewGuid();

        return new OrderEntity {
            Id = orderId,
            OrderNumber = $"ORD-SEARCH-{orderId:N}",
            CustomerPrincipalId = customer,
            MerchantPrincipalId = merchant,
            Status = status,
            Subtotal = 150000m,
            FinalTotal = 165000m,
            FinalShippingFee = 15000m,
            ShippingAddress = "Jl. Cikini Raya No. 99",
            RecipientName = "Sarah",
            RecipientPhone = "081200000099",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Items = [.. Enumerable.Range(1, lines).Select(line => new OrderItem {
                OrderId = orderId,
                ProductId = $"SKU-{line}",
                ProductTitle = $"Product {line}",
                UnitPrice = 50000m,
                Quantity = 1,
                LineSubtotal = 50000m,
            })],
        };
    }

    private static List<Guid> NewestFirst(params OrderEntity[] orders) =>
        [.. orders
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id.ToString("D"), StringComparer.Ordinal)
            .Select(o => o.Id)];

    private static async Task SeedAsync(ScratchTransaction scratch, params OrderEntity[] orders) {
        await using var db = await scratch.ContextAsync();
        db.Orders.AddRange(orders);
        await db.SaveChangesAsync();
    }

    private static async Task<OrderSearchPage> SearchAsync(
        ScratchTransaction scratch,
        string queryString,
        CapturedCommands? commands = null
    ) {
        var parsing = OrderSearchParameters.Parse(QueryHelpers.ParseQuery(queryString));
        Assert.Null(parsing.Refusal);

        await using var db = commands is null ? await scratch.ContextAsync() : await scratch.ContextAsync(commands);
        var search = new OrderSearch(new OrderSearchRepository(db));

        return await search.SearchAsync(parsing.Query!, CancellationToken.None);
    }

    private static List<Guid> Ids(OrderSearchPage page) => [.. page.Orders.Select(o => o.Id)];

    [Fact]
    public async Task EveryOrderComesNewestFirstAndOrdersOfOneInstantByIdDescending() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var customer = NewPrincipal();
        var orders = new[] {
            AnOrder(Window.AddHours(1), merchant, customer),
            AnOrder(Window.AddHours(3), merchant, customer, id: LowId),
            AnOrder(Window.AddHours(4), merchant, customer),
            AnOrder(Window.AddHours(3), merchant, customer, id: HighId),
            AnOrder(Window, merchant, customer),
        };
        await SeedAsync(scratch, orders);

        var page = await SearchAsync(scratch, "?limit=5");

        Assert.Equal(NewestFirst(orders), Ids(page));
    }

    [Fact]
    public async Task AStatusFilterKeepsOnlyOrdersInThatStatus() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var customer = NewPrincipal();
        var paidLater = AnOrder(Window.AddHours(4), merchant, customer, OrderStatus.PAID);
        var cancelled = AnOrder(Window.AddHours(3), merchant, customer, OrderStatus.CANCELLED);
        var paidEarlier = AnOrder(Window.AddHours(2), merchant, customer, OrderStatus.PAID);
        var delivered = AnOrder(Window.AddHours(1), merchant, customer, OrderStatus.DELIVERED);
        await SeedAsync(scratch, paidLater, cancelled, paidEarlier, delivered);

        var page = await SearchAsync(scratch, "?status=PAID&limit=2");

        Assert.Equal([paidLater.Id, paidEarlier.Id], Ids(page));
        Assert.All(page.Orders, o => Assert.Equal("PAID", o.Status));
    }

    [Fact]
    public async Task AStatusSetKeepsOrdersInAnyOfItsStatuses() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var customer = NewPrincipal();
        var paid = AnOrder(Window.AddHours(4), merchant, customer, OrderStatus.PAID);
        var cancelled = AnOrder(Window.AddHours(3), merchant, customer, OrderStatus.CANCELLED);
        var delivered = AnOrder(Window.AddHours(2), merchant, customer, OrderStatus.DELIVERED);
        var completed = AnOrder(Window.AddHours(1), merchant, customer, OrderStatus.COMPLETED);
        await SeedAsync(scratch, paid, cancelled, delivered, completed);

        var page = await SearchAsync(scratch, $"?status=DELIVERED,PAID,COMPLETED&merchant={merchant}");

        Assert.Equal([paid.Id, delivered.Id, completed.Id], Ids(page));
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task TheCreatedRangeHoldsItsStartAndNotItsEnd() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var customer = NewPrincipal();
        var from = Window.AddDays(10);
        var to = from.AddHours(1);
        var justBefore = AnOrder(from.AddTicks(-10), merchant, customer);
        var atStart = AnOrder(from, merchant, customer);
        var justInside = AnOrder(to.AddTicks(-10), merchant, customer);
        var atEnd = AnOrder(to, merchant, customer);
        await SeedAsync(scratch, justBefore, atStart, justInside, atEnd);

        var fromInJakarta = from.AddHours(7).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var page = await SearchAsync(scratch, $"?createdFrom={fromInJakarta}%2B07:00&createdTo={Instant(to)}");

        Assert.Equal([justInside.Id, atStart.Id], Ids(page));
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task TheMerchantFilterKeepsOnlyThatMerchantsOrders() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var other = NewPrincipal();
        var customer = NewPrincipal();
        var older = AnOrder(Window.AddHours(1), merchant, customer);
        var others = AnOrder(Window.AddHours(2), other, customer);
        var newer = AnOrder(Window.AddHours(3), merchant, customer);
        await SeedAsync(scratch, older, others, newer);

        var page = await SearchAsync(scratch, $"?merchant={merchant.ToUpperInvariant()}");

        Assert.Equal([newer.Id, older.Id], Ids(page));
        Assert.All(page.Orders, o => Assert.Equal(merchant, o.MerchantPrincipalId));
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task TheCustomerFilterKeepsOnlyThatBuyersOrders() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var customer = NewPrincipal();
        var other = NewPrincipal();
        var mine = AnOrder(Window.AddHours(1), merchant, customer);
        var theirs = AnOrder(Window.AddHours(2), merchant, other);
        await SeedAsync(scratch, mine, theirs);

        var page = await SearchAsync(scratch, $"?customer={customer}");

        Assert.Equal([mine.Id], Ids(page));
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task AnOrderNumberMatchesThatOrderExactlyAndNoPrefixOfIt() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var customer = NewPrincipal();
        var wanted = AnOrder(Window.AddHours(1), merchant, customer);
        var neighbour = AnOrder(Window.AddHours(2), merchant, customer);
        await SeedAsync(scratch, wanted, neighbour);

        var exact = await SearchAsync(scratch, $"?orderNumber={wanted.OrderNumber}");
        var prefix = await SearchAsync(scratch, $"?orderNumber={wanted.OrderNumber[..^1]}");

        Assert.Equal([wanted.Id], Ids(exact));
        Assert.Empty(prefix.Orders);
        Assert.Null(prefix.Next);
    }

    [Fact]
    public async Task EveryFilterGivenMustHoldForAnOrderToBeFound() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var otherMerchant = NewPrincipal();
        var customer = NewPrincipal();
        var otherCustomer = NewPrincipal();
        var from = Window.AddDays(20);
        var to = from.AddDays(1);
        var match = AnOrder(from.AddHours(5), merchant, customer, OrderStatus.DELIVERED);
        var secondMatch = AnOrder(from.AddHours(2), merchant, customer, OrderStatus.PAID);
        var wrongStatus = AnOrder(from.AddHours(4), merchant, customer, OrderStatus.CANCELLED);
        var wrongMerchant = AnOrder(from.AddHours(3), otherMerchant, customer, OrderStatus.DELIVERED);
        var wrongCustomer = AnOrder(from.AddHours(1), merchant, otherCustomer, OrderStatus.DELIVERED);
        var tooLate = AnOrder(to, merchant, customer, OrderStatus.DELIVERED);
        var tooEarly = AnOrder(from.AddTicks(-10), merchant, customer, OrderStatus.PAID);
        await SeedAsync(scratch, match, secondMatch, wrongStatus, wrongMerchant, wrongCustomer, tooLate, tooEarly);

        var page = await SearchAsync(
            scratch,
            $"?status=DELIVERED,PAID&merchant={merchant}&customer={customer}"
                + $"&createdFrom={Instant(from)}&createdTo={Instant(to)}"
        );
        var oneOrder = await SearchAsync(
            scratch,
            $"?merchant={merchant}&orderNumber={secondMatch.OrderNumber}&status=PAID"
        );

        Assert.Equal([match.Id, secondMatch.Id], Ids(page));
        Assert.Equal([secondMatch.Id], Ids(oneOrder));
    }

    [Fact]
    public async Task PagesWalkEveryOrderOnceInOneOrderEvenThroughOrdersOfOneInstant() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var customer = NewPrincipal();
        var sameInstant = Window.AddDays(30);
        var orders = new[] {
            AnOrder(sameInstant.AddHours(2), merchant, customer),
            AnOrder(sameInstant, merchant, customer),
            AnOrder(sameInstant, merchant, customer),
            AnOrder(sameInstant, merchant, customer),
            AnOrder(sameInstant.AddHours(-1), merchant, customer),
            AnOrder(sameInstant.AddHours(-2), merchant, customer),
            AnOrder(sameInstant.AddHours(1), merchant, customer),
        };
        await SeedAsync(scratch, orders);

        var walked = new List<Guid>();
        var sizes = new List<int>();
        var before = string.Empty;
        for (var pages = 0; pages < 10; pages++) {
            var page = await SearchAsync(scratch, $"?merchant={merchant}&limit=2{before}");
            walked.AddRange(Ids(page));
            sizes.Add(page.Orders.Count);

            if (page.Next is not OrderSearchCursor next) {
                break;
            }

            before = $"&before={next.Encode()}";
        }

        Assert.Equal(NewestFirst(orders), walked);
        Assert.Equal([2, 2, 2, 1], sizes);
    }

    [Fact]
    public async Task AnOrderPlacedBetweenPagesIsNeitherRepeatedNorPushesAnotherOffTheNextPage() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var customer = NewPrincipal();
        var start = Window.AddDays(40);
        var o1 = AnOrder(start.AddHours(6), merchant, customer);
        var o2 = AnOrder(start.AddHours(5), merchant, customer);
        var o3 = AnOrder(start.AddHours(4), merchant, customer, id: MiddleId);
        var o4 = AnOrder(start.AddHours(3), merchant, customer);
        var o5 = AnOrder(start.AddHours(2), merchant, customer);
        var o6 = AnOrder(start.AddHours(1), merchant, customer);
        await SeedAsync(scratch, o1, o2, o3, o4, o5, o6);

        var first = await SearchAsync(scratch, $"?merchant={merchant}&limit=3");

        var newest = AnOrder(start.AddHours(7), merchant, customer);
        var sameInstantAhead = AnOrder(o3.CreatedAt, merchant, customer, id: AheadId);
        var sameInstantBehind = AnOrder(o3.CreatedAt, merchant, customer, id: BehindId);
        var between = AnOrder(start.AddHours(2.5), merchant, customer);
        await SeedAsync(scratch, newest, sameInstantAhead, sameInstantBehind, between);

        var second = await SearchAsync(scratch, $"?merchant={merchant}&limit=3&before={first.Next!.Value.Encode()}");
        var third = await SearchAsync(
            scratch,
            $"?merchant={merchant}&limit=3&before={second.Next!.Value.Encode()}"
        );

        Assert.Equal([o1.Id, o2.Id, o3.Id], Ids(first));
        Assert.Equal([sameInstantBehind.Id, o4.Id, between.Id], Ids(second));
        Assert.Equal([o5.Id, o6.Id], Ids(third));
        Assert.Null(third.Next);
    }

    [Fact]
    public async Task ThePageAndEveryItemCountComeFromOneQuery() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var customer = NewPrincipal();
        var none = AnOrder(Window.AddHours(3), merchant, customer, lines: 0);
        var one = AnOrder(Window.AddHours(2), merchant, customer, lines: 1);
        var three = AnOrder(Window.AddHours(1), merchant, customer, lines: 3);
        await SeedAsync(scratch, none, one, three);
        var commands = new CapturedCommands();

        var page = await SearchAsync(scratch, $"?merchant={merchant}", commands);

        Assert.Equal([0, 1, 3], page.Orders.Select(o => o.ItemCount));
        var command = Assert.Single(commands.Commands);
        Assert.Contains("count(*)", command.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EachOrderCarriesItsOwnAmountsStatusAndTimes() {
        if (ConnectionString is null) {
            return;
        }

        await using var scratch = await ScratchTransaction.BeginAsync(ConnectionString);
        var merchant = NewPrincipal();
        var customer = NewPrincipal();
        var order = AnOrder(Window.AddHours(1), merchant, customer, OrderStatus.COMPLETED, lines: 2);
        order.FinalTotal = 123456.78m;
        order.FinalShippingFee = 9876.54m;
        order.UpdatedAt = Window.AddHours(30);
        order.DeliveredAt = Window.AddHours(5);
        order.CompletedAt = Window.AddHours(29);
        await SeedAsync(scratch, order);

        var page = await SearchAsync(scratch, $"?merchant={merchant}");

        Assert.Equal(
            new OrderSummaryResponse(
                order.Id,
                order.OrderNumber,
                "COMPLETED",
                customer,
                merchant,
                123456.78m,
                9876.54m,
                2,
                order.CreatedAt,
                order.UpdatedAt,
                order.DeliveredAt,
                order.CompletedAt
            ),
            Assert.Single(page.Orders)
        );
        Assert.Equal(DateTimeKind.Utc, page.Orders[0].CreatedAt.Kind);
    }
}
