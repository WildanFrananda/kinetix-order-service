using Grpc.Core;
using Common.V1;
using OrderProto = global::Order.V1;

namespace Kinetix.OrderService.Application.Services;

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

    public override Task<OrderProto.ListOrdersForPrincipalResponse> ListOrdersForPrincipal(
        OrderProto.ListOrdersForPrincipalRequest request, ServerCallContext context) {
        throw new RpcException(new Status(
            StatusCode.Unimplemented, "ListOrdersForPrincipal lands with the saga in S11"));
    }

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
            "RECEIVED" => OrderStatus.Received,
            "PACKING" => OrderStatus.Packing,
            "PACKED" => OrderStatus.Packed,
            "ASSIGNED" => OrderStatus.Assigned,
            "DISPATCHED" => OrderStatus.Dispatched,
            "IN_TRANSIT" => OrderStatus.InTransit,
            "DELIVERED" => OrderStatus.Delivered,
            "COMPLETED" => OrderStatus.Completed,
            "CANCELLED" => OrderStatus.Cancelled,
            _ => OrderStatus.Unspecified
        };
    }
}
