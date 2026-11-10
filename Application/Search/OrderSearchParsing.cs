namespace Kinetix.OrderService.Application.Search;

public sealed record OrderSearchParsing(OrderSearchQuery? Query, OrderSearchRefusal? Refusal) {
    public static OrderSearchParsing Accepted(OrderSearchQuery query) => new(query, null);

    public static OrderSearchParsing Refused(OrderSearchRefusal refusal) => new(null, refusal);
}
