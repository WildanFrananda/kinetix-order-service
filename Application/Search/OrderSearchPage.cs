using Kinetix.OrderService.DTOs.Responses;

namespace Kinetix.OrderService.Application.Search;

public sealed record OrderSearchPage(IReadOnlyList<OrderSummaryResponse> Orders, OrderSearchCursor? Next);
