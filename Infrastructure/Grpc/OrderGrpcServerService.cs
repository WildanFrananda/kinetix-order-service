using Grpc.Core;
using Common.V1;
using Kinetix.OrderService.Application.Ports;
using OrderProto = global::Order.V1;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class OrderGrpcServerService(IOrderService orderService) : OrderProto.OrderService.OrderServiceBase {
    private readonly IOrderService _orderService = orderService;

    private const long MinorPerMajor = 100L;

    public override async Task<OrderProto.GetOrderDetailsResponse> GetOrderDetails(OrderProto.GetOrderDetailsRequest request, ServerCallContext context) {
        if (!Guid.TryParse(request.OrderId, out var orderGuid)) {
            return new OrderProto.GetOrderDetailsResponse { Found = false };
        }

        var order = await _orderService.GetOrderByIdAsync(orderGuid);
        if (order == null) {
            return new OrderProto.GetOrderDetailsResponse { Found = false };
        }

        var response = new OrderProto.GetOrderDetailsResponse {
            Found = true,
            OrderId = order.Id.ToString(),
            OrderNumber = order.OrderNumber,
            CustomerPrincipalId = order.CustomerPrincipalId ?? string.Empty,
            Status = MapStatus(order.Status),
            Subtotal = ToMoney(order.Subtotal),
            DiscountAmount = ToMoney(order.DiscountAmount),
            ShippingFee = ToMoney(order.FinalShippingFee),
            FinalTotal = ToMoney(order.FinalTotal)
        };

        foreach (var item in order.Items) {
            response.Lines.Add(new OrderProto.OrderLine {
                ProductId = item.ProductId,
                ProductTitle = item.ProductTitle,
                UnitPrice = ToMoney(item.UnitPrice),
                Quantity = item.Quantity,
                LineSubtotal = ToMoney(item.LineSubtotal)
            });
        }

        return response;
    }

    public override async Task<OrderProto.ListOrdersForPrincipalResponse> ListOrdersForPrincipal(
        OrderProto.ListOrdersForPrincipalRequest request, ServerCallContext context) {

        if (string.IsNullOrWhiteSpace(request.PrincipalId)) {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "a principal id is required"));
        }

        var page = request.Page > 0 ? request.Page : 1;
        var pageSize = request.PageSize is > 0 and <= 100 ? request.PageSize : 10;

        var requestedStatus = MapRequestedStatus(request.Status);

        if (request.Status != OrderStatus.Unspecified && requestedStatus is null) {
            return new OrderProto.ListOrdersForPrincipalResponse { TotalCount = 0 };
        }

        var result = await _orderService.GetCustomerOrdersAsync(
            request.PrincipalId, requestedStatus, page, pageSize);

        var response = new OrderProto.ListOrdersForPrincipalResponse {
            TotalCount = result.TotalCount
        };

        foreach (var order in result.Orders) {
            var details = new OrderProto.GetOrderDetailsResponse {
                Found = true,
                OrderId = order.Id.ToString(),
                OrderNumber = order.OrderNumber,
                CustomerPrincipalId = order.CustomerPrincipalId ?? string.Empty,
                Status = MapStatus(order.Status),
                Subtotal = ToMoney(order.Subtotal),
                DiscountAmount = ToMoney(order.DiscountAmount),
                ShippingFee = ToMoney(order.FinalShippingFee),
                FinalTotal = ToMoney(order.FinalTotal)
            };

            foreach (var item in order.Items) {
                details.Lines.Add(new OrderProto.OrderLine {
                    ProductId = item.ProductId,
                    ProductTitle = item.ProductTitle,
                    UnitPrice = ToMoney(item.UnitPrice),
                    Quantity = item.Quantity,
                    LineSubtotal = ToMoney(item.LineSubtotal)
                });
            }

            response.Orders.Add(details);
        }

        return response;
    }

    private static Domain.Enums.OrderStatus? MapRequestedStatus(OrderStatus status) => status switch {
        OrderStatus.PendingPayment => Domain.Enums.OrderStatus.PENDING_PAYMENT,
        OrderStatus.Paid => Domain.Enums.OrderStatus.PAID,
        OrderStatus.Packing => Domain.Enums.OrderStatus.PROCESSING_FULFILLMENT,
        OrderStatus.InTransit => Domain.Enums.OrderStatus.SHIPPED,
        OrderStatus.Delivered => Domain.Enums.OrderStatus.DELIVERED,
        OrderStatus.Completed => Domain.Enums.OrderStatus.COMPLETED,
        OrderStatus.Cancelled => Domain.Enums.OrderStatus.CANCELLED,
        OrderStatus.Refunded => Domain.Enums.OrderStatus.REFUNDED,
        _ => null,
    };

    private static Money ToMoney(decimal amount) {
        return new Money {
            AmountMinor = (long)Math.Round(amount * MinorPerMajor, MidpointRounding.AwayFromZero),
            Currency = "IDR"
        };
    }

    private static OrderStatus MapStatus(string status) {
        return status.ToUpperInvariant() switch {
            "PENDING_PAYMENT" => OrderStatus.PendingPayment,
            "PAID" => OrderStatus.Paid,
            "PROCESSING_FULFILLMENT" => OrderStatus.Packing,
            "SHIPPED" => OrderStatus.InTransit,
            "DELIVERED" => OrderStatus.Delivered,
            "COMPLETED" => OrderStatus.Completed,
            "CANCELLED" => OrderStatus.Cancelled,
            "REFUNDED" => OrderStatus.Refunded,
            _ => OrderStatus.Unspecified
        };
    }
}
