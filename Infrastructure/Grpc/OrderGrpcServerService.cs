using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Common.V1;
using Kinetix.OrderService.Application.Ports;
using OrderProto = global::Order.V1;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class OrderGrpcServerService(
    IOrderService orderService,
    IFulfillmentPackedHandler fulfillmentPacked,
    IOrderDeliveredHandler orderDelivered,
    IReturnsHandler returns
) : OrderProto.OrderService.OrderServiceBase {
    private readonly IOrderService _orderService = orderService;
    private readonly IFulfillmentPackedHandler _fulfillmentPacked = fulfillmentPacked;
    private readonly IOrderDeliveredHandler _orderDelivered = orderDelivered;
    private readonly IReturnsHandler _returns = returns;

    private const long MinorPerMajor = 100L;
    private const int ChangeFeedDefaultLimit = 100;
    private const int ChangeFeedMaxLimit = 500;

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
        OrderProto.ListOrdersForPrincipalRequest request, ServerCallContext context
    ) {

        if (string.IsNullOrWhiteSpace(request.PrincipalId)) {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "a principal id is required"
            ));
        }

        var page = request.Page > 0 ? request.Page : 1;
        var pageSize = request.PageSize is > 0 and <= 100 ? request.PageSize : 10;

        var requestedStatus = MapRequestedStatus(request.Status);

        if (request.Status != OrderStatus.Unspecified && requestedStatus is null) {
            return new OrderProto.ListOrdersForPrincipalResponse { TotalCount = 0 };
        }

        var result = await _orderService.GetCustomerOrdersAsync(
            request.PrincipalId, requestedStatus, page, pageSize
        );

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

    public override async Task<OrderProto.FulfillmentPackedResponse> FulfillmentPacked(
        OrderProto.FulfillmentPackedRequest request, ServerCallContext context
    ) {
        if (string.IsNullOrWhiteSpace(request.OrderNumber)) {
            return new OrderProto.FulfillmentPackedResponse {
                Success = false,
                Error = new ErrorDetail {
                    ErrorCode = "BLANK_ORDER_NUMBER",
                    Message = "order_number is required: a packed parcel belongs to a named order"
                }
            };
        }

        var outcome = await _fulfillmentPacked.HandleAsync(
            request.MerchantPrincipalId, request.OrderNumber, request.FulfillmentTaskId
        );

        if (!outcome.Found) {
            return new OrderProto.FulfillmentPackedResponse {
                Success = false,
                Error = new ErrorDetail {
                    ErrorCode = "NO_SUCH_ORDER",
                    Message = outcome.Detail ?? "no order carries that number"
                }
            };
        }

        return new OrderProto.FulfillmentPackedResponse {
            Success = true,
            AlreadyPacked = outcome.AlreadyPacked,
            DispatchRef = outcome.DispatchRef
        };
    }

    public override async Task<OrderProto.OrderDeliveredResponse> OrderDelivered(
        OrderProto.OrderDeliveredRequest request, ServerCallContext context
    ) {
        if (string.IsNullOrWhiteSpace(request.OrderNumber)) {
            return new OrderProto.OrderDeliveredResponse {
                Accepted = false,
                Error = new ErrorDetail {
                    ErrorCode = "BLANK_ORDER_NUMBER",
                    Message = "order_number is required: a delivery belongs to a named order"
                }
            };
        }

        if (string.IsNullOrWhiteSpace(request.DriverPrincipalId)) {
            return new OrderProto.OrderDeliveredResponse {
                Accepted = false,
                Error = new ErrorDetail {
                    ErrorCode = "BLANK_DRIVER_PRINCIPAL",
                    Message = "driver_principal_id is required: a fee with no payee is owed to nobody"
                }
            };
        }

        var deliveredAt = DateTime.TryParse(
            request.DeliveredAt,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed
        ) ? parsed : DateTime.UtcNow;

        var outcome = await _orderDelivered.HandleAsync(
            request.OrderNumber, request.DriverPrincipalId, deliveredAt
        );

        if (!outcome.Accepted) {
            return new OrderProto.OrderDeliveredResponse {
                Accepted = false,
                Error = new ErrorDetail {
                    ErrorCode = "NO_SUCH_ORDER",
                    Message = outcome.Error ?? "no order carries that number"
                }
            };
        }

        return new OrderProto.OrderDeliveredResponse {
            Accepted = true,
            AlreadyDelivered = outcome.AlreadyDelivered
        };
    }

    public override async Task<OrderProto.OpenReturnResponse> OpenReturn(
        OrderProto.OpenReturnRequest request, ServerCallContext context
    ) {
        var outcome = await _returns.OpenAsync(
            request.OrderNumber, request.MerchantPrincipalId, request.Reason
        );

        if (!outcome.Success) {
            return new OrderProto.OpenReturnResponse {
                Success = false,
                Error = new ErrorDetail { ErrorCode = "RETURN_REFUSED", Message = outcome.Fault ?? "refused" }
            };
        }

        return new OrderProto.OpenReturnResponse {
            Success = true,
            ReturnNumber = outcome.ReturnNumber,
            Status = MapReturnStatus(outcome.Status),
            AlreadyOpen = outcome.AlreadyOpen
        };
    }

    public override async Task<OrderProto.ReturnGoodsReceivedResponse> ReturnGoodsReceived(
        OrderProto.ReturnGoodsReceivedRequest request, ServerCallContext context
    ) {
        var lines = request.Lines
            .Select(line => new ReturnedLine(line.Sku, line.Quantity))
            .ToList();

        var receivedAt = DateTime.TryParse(
            request.ReceivedAt,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed
        ) ? parsed : default;

        var outcome = await _returns.GoodsReceivedAsync(
            request.ReturnNumber, request.MerchantPrincipalId, lines, request.BinCode, receivedAt
        );

        if (!outcome.Accepted) {
            return new OrderProto.ReturnGoodsReceivedResponse {
                Accepted = false,
                Error = new ErrorDetail {
                    ErrorCode = "RETURN_GOODS_REFUSED",
                    Message = outcome.Fault ?? "refused"
                }
            };
        }

        return new OrderProto.ReturnGoodsReceivedResponse {
            Accepted = true,
            AlreadyRecorded = outcome.AlreadyRecorded,
            Status = MapReturnStatus(outcome.Status)
        };
    }

    public override async Task<OrderProto.OrdersChangedSinceResponse> OrdersChangedSince(
        OrderProto.OrdersChangedSinceRequest request, ServerCallContext context
    ) {
        int limit = request.Limit switch {
            <= 0 => ChangeFeedDefaultLimit,
            > ChangeFeedMaxLimit => ChangeFeedMaxLimit,
            _ => request.Limit
        };

        DateTime? updatedThrough = request.Cursor?.UpdatedThrough?.ToDateTime();
        string lastOrderNumber = request.Cursor?.LastOrderNumber ?? string.Empty;

        var page = await _orderService.OrdersChangedSinceAsync(
            updatedThrough, lastOrderNumber, limit
        );

        var response = new OrderProto.OrdersChangedSinceResponse { HasMore = page.HasMore };

        foreach (var change in page.Changes) {
            var record = new OrderProto.OrderRecord {
                OrderNumber = change.OrderNumber,
                BuyerPrincipalId = change.BuyerPrincipalId,
                MerchantPrincipalId = change.MerchantPrincipalId,
                Status = MapStatus(change.Status.ToString()),
                PlacedAt = Timestamp.FromDateTime(change.PlacedAt),
                UpdatedAt = Timestamp.FromDateTime(change.UpdatedAt)
            };

            record.LineTitles.Add(change.LineTitles);
            response.Upserted.Add(record);
        }

        var last = page.Changes.Count > 0 ? page.Changes[^1] : null;

        response.Next = last is null
            ? new OrderProto.OrderCursor {
                UpdatedThrough = updatedThrough is DateTime resumed
                    ? Timestamp.FromDateTime(resumed)
                    : null,
                LastOrderNumber = lastOrderNumber
            }
            : new OrderProto.OrderCursor {
                UpdatedThrough = Timestamp.FromDateTime(last.UpdatedAt),
                LastOrderNumber = last.OrderNumber
            };

        return response;
    }

    private static OrderProto.ReturnStatus MapReturnStatus(Domain.Enums.ReturnStatus status) {
        return status switch {
            Domain.Enums.ReturnStatus.OPEN => OrderProto.ReturnStatus.Open,
            Domain.Enums.ReturnStatus.GOODS_RECEIVED => OrderProto.ReturnStatus.GoodsReceived,
            Domain.Enums.ReturnStatus.RESOLVED => OrderProto.ReturnStatus.Resolved,
            Domain.Enums.ReturnStatus.REJECTED => OrderProto.ReturnStatus.Rejected,
            _ => OrderProto.ReturnStatus.Unspecified
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
