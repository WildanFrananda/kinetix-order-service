using Common.V1;
using OrderProto = global::Order.V1;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public static class OrderWireStatus {
    public static OrderStatus Of(string status) {
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

    public static OrderStatus Of(Domain.Enums.OrderStatus? status) {
        return status is { } known ? Of(known.ToString()) : OrderStatus.Unspecified;
    }

    public static OrderProto.ReturnStatus Of(Domain.Enums.ReturnStatus? status) {
        return status switch {
            Domain.Enums.ReturnStatus.OPEN => OrderProto.ReturnStatus.Open,
            Domain.Enums.ReturnStatus.GOODS_RECEIVED => OrderProto.ReturnStatus.GoodsReceived,
            Domain.Enums.ReturnStatus.RESOLVED => OrderProto.ReturnStatus.Resolved,
            Domain.Enums.ReturnStatus.REJECTED => OrderProto.ReturnStatus.Rejected,
            _ => OrderProto.ReturnStatus.Unspecified
        };
    }
}
