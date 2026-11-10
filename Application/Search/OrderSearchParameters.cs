using System.Globalization;
using Kinetix.OrderService.Domain.Enums;
using Microsoft.Extensions.Primitives;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Application.Search;

public static class OrderSearchParameters {
    public const string Status = "status";
    public const string CreatedFrom = "createdFrom";
    public const string CreatedTo = "createdTo";
    public const string Merchant = "merchant";
    public const string Customer = "customer";
    public const string OrderNumber = "orderNumber";
    public const string Before = "before";
    public const string Limit = "limit";

    public const string UnknownParameter = "UNKNOWN_PARAMETER";
    public const string InvalidStatus = "INVALID_STATUS";
    public const string InvalidCreatedRange = "INVALID_CREATED_RANGE";
    public const string InvalidMerchant = "INVALID_MERCHANT";
    public const string InvalidCustomer = "INVALID_CUSTOMER";
    public const string InvalidOrderNumber = "INVALID_ORDER_NUMBER";
    public const string InvalidCursor = "INVALID_CURSOR";
    public const string InvalidLimit = "INVALID_LIMIT";

    public static readonly IReadOnlyList<string> All = [
        Status,
        CreatedFrom,
        CreatedTo,
        Merchant,
        Customer,
        OrderNumber,
        Before,
        Limit,
    ];

    private const int PrincipalLength = 36;

    private static readonly IReadOnlyList<string> StatusNames = Enum.GetNames<OrderStatus>();

    private static readonly string[] InstantFormats = [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'",
    ];

    public static IReadOnlyList<string> NamedIn(IEnumerable<KeyValuePair<string, StringValues>> parameters) {
        var given = parameters.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. All.Where(given.Contains)];
    }

    public static OrderSearchParsing Parse(IEnumerable<KeyValuePair<string, StringValues>> parameters) {
        var given = Gather(parameters);

        if ((UnknownIn(given) ?? RepeatedIn(given)) is OrderSearchRefusal malformed) {
            return OrderSearchParsing.Refused(malformed);
        }

        if (ReadStatuses(given, out var statuses) is OrderSearchRefusal badStatus) {
            return OrderSearchParsing.Refused(badStatus);
        }

        if (ReadInstant(given, CreatedFrom, out var createdFrom) is OrderSearchRefusal badFrom) {
            return OrderSearchParsing.Refused(badFrom);
        }

        if (ReadInstant(given, CreatedTo, out var createdTo) is OrderSearchRefusal badTo) {
            return OrderSearchParsing.Refused(badTo);
        }

        if (createdFrom >= createdTo) {
            return OrderSearchParsing.Refused(EmptyRange());
        }

        if (ReadPrincipal(given, Merchant, InvalidMerchant, out var merchant) is OrderSearchRefusal badMerchant) {
            return OrderSearchParsing.Refused(badMerchant);
        }

        if (ReadPrincipal(given, Customer, InvalidCustomer, out var customer) is OrderSearchRefusal badCustomer) {
            return OrderSearchParsing.Refused(badCustomer);
        }

        if (ReadOrderNumber(given, out var orderNumber) is OrderSearchRefusal badOrderNumber) {
            return OrderSearchParsing.Refused(badOrderNumber);
        }

        if (ReadCursor(given, out var before) is OrderSearchRefusal badCursor) {
            return OrderSearchParsing.Refused(badCursor);
        }

        if (ReadLimit(given, out var limit) is OrderSearchRefusal badLimit) {
            return OrderSearchParsing.Refused(badLimit);
        }

        return OrderSearchParsing.Accepted(new OrderSearchQuery(
            statuses,
            createdFrom,
            createdTo,
            merchant,
            customer,
            orderNumber,
            before,
            limit
        ));
    }

    private static Dictionary<string, StringValues> Gather(IEnumerable<KeyValuePair<string, StringValues>> parameters) {
        var given = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, values) in parameters) {
            given[name] = given.TryGetValue(name, out var earlier) ? StringValues.Concat(earlier, values) : values;
        }

        return given;
    }

    private static string? ValueOf(Dictionary<string, StringValues> given, string name) =>
        given.TryGetValue(name, out var values) ? values.ToString() : null;

    private static OrderSearchRefusal? UnknownIn(Dictionary<string, StringValues> given) {
        var unknown = given.Keys.FirstOrDefault(name => !All.Contains(name, StringComparer.OrdinalIgnoreCase));

        return unknown is null
            ? null
            : new OrderSearchRefusal(
                UnknownParameter,
                unknown,
                $"the search takes only {string.Join(", ", All)}",
                All
            );
    }

    private static OrderSearchRefusal? RepeatedIn(Dictionary<string, StringValues> given) {
        var repeated = All.FirstOrDefault(name => given.TryGetValue(name, out var values) && values.Count > 1);

        return repeated is null
            ? null
            : new OrderSearchRefusal(
                CodeOf(repeated),
                repeated,
                repeated == Status
                    ? "status is given more than once; several statuses are one value, comma-separated"
                    : $"{repeated} is given more than once; give it once"
            );
    }

    private static OrderSearchRefusal? ReadStatuses(
        Dictionary<string, StringValues> given,
        out IReadOnlySet<OrderStatus> statuses
    ) {
        var read = new HashSet<OrderStatus>();
        statuses = read;

        var value = ValueOf(given, Status);
        if (value is null) {
            return null;
        }

        foreach (var name in value.Split(',')) {
            if (!StatusNames.Contains(name, StringComparer.Ordinal)) {
                return new OrderSearchRefusal(
                    InvalidStatus,
                    Status,
                    name.Length == 0
                        ? "status holds an empty name; give one or more order statuses, comma-separated"
                        : $"'{name}' is not an order status",
                    StatusNames
                );
            }

            read.Add(Enum.Parse<OrderStatus>(name));
        }

        return null;
    }

    private static OrderSearchRefusal? ReadInstant(
        Dictionary<string, StringValues> given,
        string parameter,
        out DateTime? instant
    ) {
        instant = null;

        var value = ValueOf(given, parameter);
        if (value is null) {
            return null;
        }

        if (!DateTimeOffset.TryParseExact(
            value,
            InstantFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed
        )) {
            return new OrderSearchRefusal(
                InvalidCreatedRange,
                parameter,
                $"{parameter} is an instant with an offset, to the microsecond at most, such as "
                    + "2026-11-10T00:00:00+07:00 or 2026-11-09T17:00:00Z; in a query string + is sent as %2B"
            );
        }

        instant = parsed.UtcDateTime;
        return null;
    }

    private static OrderSearchRefusal EmptyRange() =>
        new(
            InvalidCreatedRange,
            CreatedTo,
            "createdTo must be after createdFrom: the range is [createdFrom, createdTo), "
                + "so a range that ends where it starts or before holds no order"
        );

    private static OrderSearchRefusal? ReadPrincipal(
        Dictionary<string, StringValues> given,
        string parameter,
        string code,
        out string? principal
    ) {
        principal = null;

        var value = ValueOf(given, parameter);
        if (value is null) {
            return null;
        }

        if (value.Length != PrincipalLength || !Guid.TryParseExact(value, "D", out var id)) {
            return new OrderSearchRefusal(
                code,
                parameter,
                $"{parameter} is a {parameter} principal id: a UUID in its 36-character form"
            );
        }

        principal = id.ToString("D");
        return null;
    }

    private static OrderSearchRefusal? ReadOrderNumber(
        Dictionary<string, StringValues> given,
        out string? orderNumber
    ) {
        orderNumber = ValueOf(given, OrderNumber);

        if (orderNumber is null
            || (!string.IsNullOrWhiteSpace(orderNumber) && orderNumber.Length <= OrderEntity.OrderNumberMaxLength)) {
            return null;
        }

        return new OrderSearchRefusal(
            InvalidOrderNumber,
            OrderNumber,
            $"orderNumber is one order number, exactly as issued, of 1 to {OrderEntity.OrderNumberMaxLength} "
                + "characters"
        );
    }

    private static OrderSearchRefusal? ReadCursor(
        Dictionary<string, StringValues> given,
        out OrderSearchCursor? before
    ) {
        before = null;

        var value = ValueOf(given, Before);
        if (value is null) {
            return null;
        }

        if (!OrderSearchCursor.TryDecode(value, out var cursor)) {
            return new OrderSearchRefusal(
                InvalidCursor,
                Before,
                "before is not a cursor this search gave out; send the previous page's nextCursor unchanged, "
                    + "or leave before out for the first page"
            );
        }

        before = cursor;
        return null;
    }

    private static OrderSearchRefusal? ReadLimit(Dictionary<string, StringValues> given, out int limit) {
        limit = OrderSearchQuery.DefaultLimit;

        var value = ValueOf(given, Limit);
        if (value is null) {
            return null;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            && limit >= OrderSearchQuery.MinLimit
            && limit <= OrderSearchQuery.MaxLimit) {
            return null;
        }

        return new OrderSearchRefusal(
            InvalidLimit,
            Limit,
            $"limit is a whole number from {OrderSearchQuery.MinLimit} to {OrderSearchQuery.MaxLimit}"
        );
    }

    private static string CodeOf(string parameter) => parameter switch {
        Status => InvalidStatus,
        CreatedFrom or CreatedTo => InvalidCreatedRange,
        Merchant => InvalidMerchant,
        Customer => InvalidCustomer,
        OrderNumber => InvalidOrderNumber,
        Before => InvalidCursor,
        Limit => InvalidLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "not a search parameter"),
    };
}
