using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kinetix.OrderService.Application.Lifecycle;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Search;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Tests;

[Collection(PostgresCollection.Name)]
public class OrderSearchEndToEndPostgresTests {
    private const string Admin = "0b7f3c52-6a1e-4f9d-8c2b-5e4a1d7f9c30";
    private const string Address = "Jl. Kebon Sirih No. 17, Jakarta Pusat";
    private const string Recipient = "Ratna Sari";
    private const string Phone = "081377720041";

    private static readonly DateTime PlacedAt = new(2098, 3, 1, 2, 0, 0, DateTimeKind.Utc);

    private static string? ConnectionString => PostgresDatabase.ConnectionString;

    private static OrderEntity AnOrder(string merchant, string customer, DateTime createdAt) {
        var id = Guid.NewGuid();

        return new OrderEntity {
            Id = id,
            OrderNumber = $"ORD-E2E-{id:N}",
            CustomerPrincipalId = customer,
            MerchantPrincipalId = merchant,
            Status = OrderStatus.PAID,
            Subtotal = 100000m,
            FinalTotal = 112000m,
            FinalShippingFee = 12000m,
            ShippingAddress = Address,
            RecipientName = Recipient,
            RecipientPhone = Phone,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Items = [
                new OrderItem {
                    OrderId = id,
                    ProductId = "SKU-E2E",
                    ProductTitle = "Kopi Gayo 250 g",
                    UnitPrice = 50000m,
                    Quantity = 2,
                    LineSubtotal = 100000m,
                },
            ],
        };
    }

    private static HttpRequestMessage Signed(string url, string token) {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    [Fact]
    public async Task AnAdminPagesThroughAMerchantsCommittedOrdersOverHttp() {
        if (ConnectionString is null) {
            return;
        }

        var merchant = Guid.NewGuid().ToString();
        var customer = Guid.NewGuid().ToString();
        var newer = AnOrder(merchant, customer, PlacedAt.AddMinutes(1));
        var older = AnOrder(merchant, customer, PlacedAt);

        await using (var db = PostgresDatabase.NewDbContext(ConnectionString)) {
            foreach (var order in new[] { newer, older }) {
                db.Orders.Add(order);
                db.RecordPlaced(order, LifecycleActor.Buyer(customer));
            }

            await db.SaveChangesAsync();
        }

        await using var identity = await TestIdentityIssuer.PublishingAsync();
        await using var host = await OrderApiHost.StartAsync(identity.JwksUrl, services => {
            services.AddDbContext<OrderDbContext>(options => options.UseNpgsql(ConnectionString));
            services.AddScoped<IOrderSearchRepository, OrderSearchRepository>();
            services.AddScoped<IOrderSearch, OrderSearch>();
        });
        using var client = new HttpClient();
        var token = identity.AccessToken(Admin, "admin");

        using var first = await client.SendAsync(Signed(
            $"{host.Address}/api/v1/orders/search?merchant={merchant}&limit=1",
            token
        ));
        var firstBody = await first.Content.ReadAsStringAsync();
        using var firstJson = JsonDocument.Parse(firstBody);
        var cursor = firstJson.RootElement.GetProperty("nextCursor").GetString();

        using var second = await client.SendAsync(Signed(
            $"{host.Address}/api/v1/orders/search?merchant={merchant}&limit=1&before={cursor}",
            token
        ));
        var secondBody = await second.Content.ReadAsStringAsync();
        using var secondJson = JsonDocument.Parse(secondBody);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(OrderSearchCursor.TryDecode(cursor, out _));

        var shown = Assert.Single(firstJson.RootElement.GetProperty("orders").EnumerateArray());
        Assert.Equal(newer.Id, shown.GetProperty("id").GetGuid());
        Assert.Equal(newer.OrderNumber, shown.GetProperty("orderNumber").GetString());
        Assert.Equal("PAID", shown.GetProperty("status").GetString());
        Assert.Equal(1, shown.GetProperty("itemCount").GetInt32());
        Assert.Equal(112000m, shown.GetProperty("finalTotal").GetDecimal());

        var next = Assert.Single(secondJson.RootElement.GetProperty("orders").EnumerateArray());
        Assert.Equal(older.Id, next.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, secondJson.RootElement.GetProperty("nextCursor").ValueKind);

        foreach (var body in new[] { firstBody, secondBody }) {
            Assert.DoesNotContain(Address, body, StringComparison.Ordinal);
            Assert.DoesNotContain(Recipient, body, StringComparison.Ordinal);
            Assert.DoesNotContain(Phone, body, StringComparison.Ordinal);
        }
    }
}
