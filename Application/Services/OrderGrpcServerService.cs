using Grpc.Core;
using Kinetix.OrderService.Grpc.Order;

namespace Kinetix.OrderService.Application.Services;

public class OrderGrpcServerService(IOrderService orderService) : OrderGrpcService.OrderGrpcServiceBase {
    private readonly IOrderService _orderService = orderService;

    public override async Task<GetOrderDetailsResponse> GetOrderDetails(GetOrderDetailsRequest request, ServerCallContext context) {
        if (!Guid.TryParse(request.OrderId, out var orderGuid)) {
            return new GetOrderDetailsResponse { Found = false };
        }

        var order = await _orderService.GetOrderByIdAsync(orderGuid);
        if (order == null) {
            return new GetOrderDetailsResponse { Found = false };
        }

        var response = new GetOrderDetailsResponse {
            OrderId = order.Id.ToString(),
            OrderNumber = order.OrderNumber,
            CustomerId = order.CustomerId,
            Status = order.Status.ToString(),
            Subtotal = (double)order.Subtotal,
            DiscountAmount = (double)order.DiscountAmount,
            FinalTotal = (double)order.FinalTotal,
            Found = true
        };

        foreach (var item in order.Items) {
            response.Items.Add(new OrderItemMessage {
                ProductId = item.ProductId,
                ProductTitle = item.ProductTitle,
                UnitPrice = (double)item.UnitPrice,
                Quantity = item.Quantity,
                LineSubtotal = (double)item.LineSubtotal
            });
        }

        return response;
    }
}
