using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs;
using Kinetix.OrderService.Infrastructure.Persistence;

namespace Kinetix.OrderService.Application.Services;

public class OrderService(OrderDbContext dbContext, ICartService cartService, IPricingClient pricingClient) : IOrderService {
    private readonly OrderDbContext _dbContext = dbContext;
    private readonly ICartService _cartService = cartService;
    private readonly IPricingClient _pricingClient = pricingClient;

    public async Task<OrderResponse> CheckoutAsync(long customerId, CheckoutRequest request, string? idempotencyKey) {
        if (!string.IsNullOrEmpty(idempotencyKey)) {
            var existingOrder = await _dbContext.Orders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey);

            if (existingOrder != null) {
                return MapToOrderResponse(existingOrder);
            }
        }

        var cart = await _cartService.GetCartAsync(customerId);
        if (cart.Items.Count == 0) {
            throw new InvalidOperationException("Cannot checkout an empty shopping cart");
        }

        decimal subtotal = cart.Items.Sum(i => i.LineTotal);
        string? appliedVoucher = request.VoucherCode ?? cart.AppliedVoucherCode;
        decimal baseShippingFee = request.BaseShippingFee;

        var priceResult = await _pricingClient.CalculatePriceAsync(appliedVoucher, subtotal, baseShippingFee);

        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8].ToUpper();
        string orderNumber = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{uniqueSuffix}";
        string serviceTier = request.ShippingServiceTier ?? "KINETIX_REGULAR";

        var order = new Order {
            OrderNumber = orderNumber,
            CustomerId = customerId,
            Status = OrderStatus.PENDING_PAYMENT,
            Subtotal = priceResult.Subtotal,
            DiscountAmount = priceResult.VoucherDiscount,
            AppliedVoucher = appliedVoucher,
            BaseShippingFee = priceResult.BaseShippingFee,
            ShippingDiscount = priceResult.ShippingDiscount,
            FinalShippingFee = priceResult.FinalShippingFee,
            FinalTotal = priceResult.FinalTotal,
            ShippingServiceTier = serviceTier,
            DistanceKm = request.DistanceKm,
            ShippingAddress = request.ShippingAddress,
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items = [.. cart.Items.Select(item => new OrderItem {
                ProductId = item.ProductId,
                ProductTitle = item.ProductTitle,
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity,
                LineSubtotal = item.LineTotal
            })]
        };

        _dbContext.Orders.Add(order);
        await _dbContext.SaveChangesAsync();

        await _cartService.ClearCartAsync(customerId);

        return MapToOrderResponse(order);
    }

    public async Task<OrderResponse?> GetOrderByIdAsync(Guid orderId) {
        var order = await _dbContext.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        return order == null ? null : MapToOrderResponse(order);
    }

    public async Task<List<OrderResponse>> GetCustomerOrdersAsync(long customerId, OrderStatus? status, int page, int pageSize) {
        var query = _dbContext.Orders
            .Include(o => o.Items)
            .Where(o => o.CustomerId == customerId);

        if (status.HasValue) {
            query = query.Where(o => o.Status == status.Value);
        }

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return [.. orders.Select(MapToOrderResponse)];
    }

    public async Task<OrderResponse> TransitionOrderStatusAsync(Guid orderId, OrderStatus newStatus) {
        var order = await _dbContext.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId)
            ?? throw new KeyNotFoundException($"Order '{orderId}' not found");

        ValidateStateTransition(order.Status, newStatus);

        order.Status = newStatus;
        order.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
        return MapToOrderResponse(order);
    }

    private static void ValidateStateTransition(OrderStatus currentStatus, OrderStatus newStatus) {
        if (currentStatus == newStatus) return;

        bool isValid = (currentStatus, newStatus) switch {
            (OrderStatus.PENDING_PAYMENT, OrderStatus.PAID) => true,
            (OrderStatus.PENDING_PAYMENT, OrderStatus.CANCELLED) => true,
            (OrderStatus.PAID, OrderStatus.PROCESSING_FULFILLMENT) => true,
            (OrderStatus.PAID, OrderStatus.CANCELLED) => true,
            (OrderStatus.PROCESSING_FULFILLMENT, OrderStatus.SHIPPED) => true,
            (OrderStatus.SHIPPED, OrderStatus.DELIVERED) => true,
            (OrderStatus.DELIVERED, OrderStatus.COMPLETED) => true,
            (OrderStatus.PAID, OrderStatus.REFUNDED) => true,
            (OrderStatus.PROCESSING_FULFILLMENT, OrderStatus.REFUNDED) => true,
            _ => false
        };

        if (!isValid) {
            throw new InvalidOperationException($"Invalid order state transition from '{currentStatus}' to '{newStatus}'");
        }
    }

    private static OrderResponse MapToOrderResponse(Order order) => new(
        order.Id,
        order.OrderNumber,
        order.CustomerId,
        order.Status.ToString(),
        order.Subtotal,
        order.DiscountAmount,
        order.AppliedVoucher,
        order.FinalTotal,
        order.ShippingAddress,
        order.ShippingServiceTier,
        order.BaseShippingFee,
        order.ShippingDiscount,
        order.FinalShippingFee,
        order.DistanceKm,
        order.CreatedAt,
        [.. order.Items.Select(i => new OrderItemResponse(
            i.Id,
            i.ProductId,
            i.ProductTitle,
            i.UnitPrice,
            i.Quantity,
            i.LineSubtotal
        ))]
    );
}
