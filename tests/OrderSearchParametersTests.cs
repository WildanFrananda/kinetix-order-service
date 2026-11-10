using Kinetix.OrderService.Application.Search;
using Kinetix.OrderService.Domain.Enums;
using Microsoft.AspNetCore.WebUtilities;

namespace Kinetix.OrderService.Tests;

public class OrderSearchParametersTests {
    private const string Merchant = "3aa957c8-b802-4d58-b9fc-f7b76ce60fa3";
    private const string Customer = "9f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7";

    private static OrderSearchParsing Parse(string queryString) =>
        OrderSearchParameters.Parse(QueryHelpers.ParseQuery(queryString));

    private static OrderSearchQuery Accepted(string queryString) {
        var parsing = Parse(queryString);

        Assert.Null(parsing.Refusal);
        return parsing.Query!;
    }

    private static OrderSearchRefusal Refused(string queryString) {
        var parsing = Parse(queryString);

        Assert.Null(parsing.Query);
        return parsing.Refusal!;
    }

    [Fact]
    public void NoParametersAskForTheNewestFiftyOrdersOfAnyKind() {
        var query = Accepted("");

        Assert.Empty(query.Statuses);
        Assert.Null(query.CreatedFrom);
        Assert.Null(query.CreatedTo);
        Assert.Null(query.MerchantPrincipalId);
        Assert.Null(query.CustomerPrincipalId);
        Assert.Null(query.OrderNumber);
        Assert.Null(query.Before);
        Assert.Equal(50, query.Limit);
    }

    [Fact]
    public void AStatusListIsASetOfStatuses() {
        var query = Accepted("?status=PAID,CANCELLED,PAID");

        Assert.Equal(
            new HashSet<OrderStatus> { OrderStatus.PAID, OrderStatus.CANCELLED },
            query.Statuses
        );
    }

    [Theory]
    [InlineData("?status=paid", "'paid' is not an order status")]
    [InlineData("?status=3", "'3' is not an order status")]
    [InlineData("?status=PAID,%20CANCELLED", "' CANCELLED' is not an order status")]
    [InlineData("?status=PAID,", "status holds an empty name")]
    [InlineData("?status=", "status holds an empty name")]
    [InlineData("?status", "status holds an empty name")]
    [InlineData("?status=SHIPPED,LOST", "'LOST' is not an order status")]
    public void AStatusThatIsNotAnOrderStatusNameIsRefusedWithTheNamesThatAre(string queryString, string message) {
        var refusal = Refused(queryString);

        Assert.Equal("INVALID_STATUS", refusal.Code);
        Assert.Equal("status", refusal.Parameter);
        Assert.StartsWith(message, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(Enum.GetNames<OrderStatus>(), refusal.Allowed);
    }

    [Theory]
    [InlineData("2026-11-10T00:00:00%2B07:00", "2026-11-09T17:00:00.0000000Z")]
    [InlineData("2026-11-09T17:00:00Z", "2026-11-09T17:00:00.0000000Z")]
    [InlineData("2026-11-10T00:00:00.123456%2B07:00", "2026-11-09T17:00:00.1234560Z")]
    [InlineData("2026-11-09T12:30:00-04:30", "2026-11-09T17:00:00.0000000Z")]
    public void AnInstantIsReadWithItsOffsetAndKeptInUtc(string sent, string utc) {
        var query = Accepted($"?createdFrom={sent}");

        Assert.Equal(DateTimeKind.Utc, query.CreatedFrom!.Value.Kind);
        Assert.Equal(utc, query.CreatedFrom.Value.ToString("O"));
    }

    [Theory]
    [InlineData("createdFrom", "2026-11-10T00:00:00")]
    [InlineData("createdFrom", "2026-11-10T00:00:00+07:00")]
    [InlineData("createdFrom", "2026-11-10")]
    [InlineData("createdFrom", "2026-11-10T00:00:00.1234567Z")]
    [InlineData("createdFrom", "1762732800")]
    [InlineData("createdFrom", "10/11/2026 00:00:00 %2B07:00")]
    [InlineData("createdFrom", "")]
    [InlineData("createdTo", "0001-01-01T00:00:00%2B07:00")]
    [InlineData("createdTo", "9999-12-31T23:59:59-07:00")]
    [InlineData("createdTo", "tomorrow")]
    public void AnInstantWithoutAnOffsetOrInAnotherShapeIsRefused(string parameter, string sent) {
        var refusal = Refused($"?{parameter}={sent}");

        Assert.Equal("INVALID_CREATED_RANGE", refusal.Code);
        Assert.Equal(parameter, refusal.Parameter);
        Assert.Contains("%2B", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-11-10T00:00:00Z", "2026-11-10T00:00:00Z")]
    [InlineData("2026-11-10T00:00:00Z", "2026-11-09T23:59:59Z")]
    [InlineData("2026-11-10T07:00:00%2B07:00", "2026-11-10T00:00:00Z")]
    public void ARangeThatEndsWhereItStartsOrEarlierIsRefused(string from, string to) {
        var refusal = Refused($"?createdFrom={from}&createdTo={to}");

        Assert.Equal("INVALID_CREATED_RANGE", refusal.Code);
        Assert.Equal("createdTo", refusal.Parameter);
    }

    [Fact]
    public void ARangeOfOneMicrosecondIsAccepted() {
        var query = Accepted("?createdFrom=2026-11-10T00:00:00Z&createdTo=2026-11-10T00:00:00.000001Z");

        Assert.Equal(TimeSpan.FromTicks(10), query.CreatedTo - query.CreatedFrom);
    }

    [Fact]
    public void APrincipalIsMatchedInTheLowercaseFormIdentityIssues() {
        var query = Accepted($"?merchant={Merchant.ToUpperInvariant()}&customer={Customer}");

        Assert.Equal(Merchant, query.MerchantPrincipalId);
        Assert.Equal(Customer, query.CustomerPrincipalId);
    }

    [Theory]
    [InlineData("merchant", "INVALID_MERCHANT", "not-a-principal")]
    [InlineData("merchant", "INVALID_MERCHANT", "3aa957c8b8024d58b9fcf7b76ce60fa3")]
    [InlineData("merchant", "INVALID_MERCHANT", "{3aa957c8-b802-4d58-b9fc-f7b76ce60fa3}")]
    [InlineData("merchant", "INVALID_MERCHANT", "")]
    [InlineData("customer", "INVALID_CUSTOMER", "ORD-20261110-0001")]
    [InlineData("customer", "INVALID_CUSTOMER", "%209f1d4a3e-1c62-4d0a-9a7b-2f5c8e0b41d7")]
    public void APrincipalThatIsNotAUuidIsRefused(string parameter, string code, string sent) {
        var refusal = Refused($"?{parameter}={sent}");

        Assert.Equal(code, refusal.Code);
        Assert.Equal(parameter, refusal.Parameter);
    }

    [Fact]
    public void AnOrderNumberIsKeptExactlyAsSent() {
        Assert.Equal("ORD-20260917-E4C9664A", Accepted("?orderNumber=ORD-20260917-E4C9664A").OrderNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%20%20%20")]
    [InlineData("ORD-000000000000000000000000000000000000000000000001")]
    public void AnOrderNumberThatIsBlankOrLongerThanTheColumnIsRefused(string sent) {
        var refusal = Refused($"?orderNumber={sent}");

        Assert.Equal("INVALID_ORDER_NUMBER", refusal.Code);
        Assert.Equal("orderNumber", refusal.Parameter);
    }

    [Fact]
    public void ACursorThisSearchGaveOutIsReadBack() {
        var cursor = new OrderSearchCursor(
            new DateTime(2026, 11, 10, 3, 4, 5, DateTimeKind.Utc).AddTicks(1230),
            Guid.Parse("5c0f2a8e-9d41-4b7a-8e3c-1f6d2b9a7e05")
        );

        Assert.Equal(cursor, Accepted($"?before={cursor.Encode()}").Before);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("MjAyNi0xMS0xMFQwMzowNDowNVo")]
    public void ACursorThisSearchDidNotGiveOutIsRefused(string sent) {
        var refusal = Refused($"?before={sent}");

        Assert.Equal("INVALID_CURSOR", refusal.Code);
        Assert.Equal("before", refusal.Parameter);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("100", 100)]
    [InlineData("050", 50)]
    public void ALimitFromOneToAHundredIsAccepted(string sent, int limit) {
        Assert.Equal(limit, Accepted($"?limit={sent}").Limit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("%2B5")]
    [InlineData("%205")]
    [InlineData("5.0")]
    [InlineData("five")]
    [InlineData("")]
    [InlineData("99999999999")]
    public void ALimitOutsideOneToAHundredOrNotAWholeNumberIsRefused(string sent) {
        var refusal = Refused($"?limit={sent}");

        Assert.Equal("INVALID_LIMIT", refusal.Code);
        Assert.Equal("limit", refusal.Parameter);
        Assert.Equal("limit is a whole number from 1 to 100", refusal.Message);
    }

    [Fact]
    public void AParameterTheSearchDoesNotTakeIsRefusedRatherThanIgnored() {
        var refusal = Refused("?stauts=PAID");

        Assert.Equal("UNKNOWN_PARAMETER", refusal.Code);
        Assert.Equal("stauts", refusal.Parameter);
        Assert.Equal(OrderSearchParameters.All, refusal.Allowed);
    }

    [Fact]
    public void ParameterNamesAreMatchedWhateverTheirCase() {
        var query = Accepted("?Status=PAID&LIMIT=5");

        Assert.Equal([OrderStatus.PAID], query.Statuses);
        Assert.Equal(5, query.Limit);
    }

    [Theory]
    [InlineData("?status=PAID&status=CANCELLED", "INVALID_STATUS", "status")]
    [InlineData("?limit=5&limit=6", "INVALID_LIMIT", "limit")]
    [InlineData("?merchant=a&Merchant=b", "INVALID_MERCHANT", "merchant")]
    [InlineData("?createdTo=2026-11-10T00:00:00Z&createdTo=2026-11-11T00:00:00Z", "INVALID_CREATED_RANGE", "createdTo")]
    public void AParameterGivenTwiceIsRefusedRatherThanOneOfThemChosen(
        string queryString,
        string code,
        string parameter
    ) {
        var refusal = Refused(queryString);

        Assert.Equal(code, refusal.Code);
        Assert.Equal(parameter, refusal.Parameter);
        Assert.Contains("more than once", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFilterIsCarriedTogether() {
        var query = Accepted(
            $"?status=DELIVERED&createdFrom=2026-11-01T00:00:00%2B07:00&createdTo=2026-11-10T00:00:00%2B07:00"
                + $"&merchant={Merchant}&customer={Customer}&orderNumber=ORD-20261105-0001&limit=10"
        );

        Assert.Equal([OrderStatus.DELIVERED], query.Statuses);
        Assert.Equal(new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc), query.CreatedFrom);
        Assert.Equal(new DateTime(2026, 11, 9, 17, 0, 0, DateTimeKind.Utc), query.CreatedTo);
        Assert.Equal(Merchant, query.MerchantPrincipalId);
        Assert.Equal(Customer, query.CustomerPrincipalId);
        Assert.Equal("ORD-20261105-0001", query.OrderNumber);
        Assert.Equal(10, query.Limit);
    }

    [Fact]
    public void TheParametersAreNamedInOneOrderWhateverOrderTheyWereSentIn() {
        var named = OrderSearchParameters.NamedIn(
            QueryHelpers.ParseQuery($"?before=x&merchant={Merchant}&STATUS=PAID&unknown=1")
        );

        Assert.Equal(["status", "merchant", "before"], named);
    }
}
