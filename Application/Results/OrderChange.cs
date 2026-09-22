using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Application.Results;

public record OrderChange(
    string OrderNumber,
    string BuyerPrincipalId,
    string MerchantPrincipalId,
    OrderStatus Status,
    IReadOnlyList<string> LineTitles,
    DateTime PlacedAt,
    DateTime UpdatedAt
);
