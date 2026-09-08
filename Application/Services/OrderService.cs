using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Requests;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Persistence;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Application.Services;

public class OrderService(
    OrderDbContext dbContext,
    ICartService cartService,
    IPricingClient pricingClient,
    IShippingClient shippingClient,
    CheckoutSagaRunner sagaRunner,
    ILogger<OrderService> logger
) : IOrderService {
    private readonly OrderDbContext _dbContext = dbContext;
    private readonly ICartService _cartService = cartService;
    private readonly IPricingClient _pricingClient = pricingClient;
    private readonly IShippingClient _shippingClient = shippingClient;
    private readonly CheckoutSagaRunner _sagaRunner = sagaRunner;
    private readonly ILogger<OrderService> _logger = logger;

    public async Task<OrderResponse> CheckoutAsync(string customerPrincipalId, CheckoutRequest request, string? idempotencyKey) {
        if (!string.IsNullOrEmpty(idempotencyKey)) {
            var existingOrder = await _dbContext.Orders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o =>
                    o.IdempotencyKey == idempotencyKey
                    && o.CustomerPrincipalId == customerPrincipalId
                );

            if (existingOrder != null) {
                return MapToOrderResponse(existingOrder);
            }
        }

        var cart = await _cartService.GetCartAsync(customerPrincipalId);
        if (cart.Items.Count == 0) {
            throw new InvalidOperationException("Cannot checkout an empty shopping cart");
        }

        string? appliedVoucher = request.VoucherCode ?? cart.AppliedVoucherCode;
        string merchantPrincipalId = ResolveMerchantPrincipal(cart);

        var shippingQuote = await QuoteShippingAsync(request.ShippingServiceTier, merchantPrincipalId);
        decimal baseShippingFee = shippingQuote.BaseShippingFee;

        var priceLines = cart.Items
            .Select(i => new PriceLine(i.ProductId, i.CategoryId, i.UnitPrice, i.Quantity))
            .ToList();

        var priceResult = await _pricingClient.CalculatePriceAsync(appliedVoucher, priceLines, baseShippingFee);

        if (priceResult.BaseShippingFee != baseShippingFee
            || priceResult.FinalShippingFee < 0m
            || priceResult.FinalShippingFee > baseShippingFee
        ) {

            _logger.LogError(
                "pricing contradicted matching's shipping quote: quoted base {QuotedBase}, pricing "
              + "returned base {PricingBase} and final {PricingFinal}. Refusing rather than "
              + "debiting a fee that no longer descends from the rate card.",
                baseShippingFee, priceResult.BaseShippingFee, priceResult.FinalShippingFee
            );

            throw new ShippingFeeContradictedException(
                baseShippingFee, priceResult.BaseShippingFee, priceResult.FinalShippingFee
            );
        }

        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8].ToUpper();
        string orderNumber = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{uniqueSuffix}";

        var order = new OrderEntity {
            OrderNumber = orderNumber,
            CustomerPrincipalId = customerPrincipalId,
            Status = OrderStatus.PENDING_PAYMENT,
            Subtotal = priceResult.Subtotal,
            DiscountAmount = priceResult.VoucherDiscount,
            AppliedVoucher = appliedVoucher,
            BaseShippingFee = priceResult.BaseShippingFee,
            ShippingDiscount = priceResult.ShippingDiscount,
            FinalShippingFee = priceResult.FinalShippingFee,
            FinalTotal = priceResult.FinalTotal,
            ShippingServiceTier = shippingQuote.ServiceTier,
            DistanceKm = shippingQuote.DistanceKm,
            ShippingQuoteBasis = ShippingQuoteBasis.TIER_FLOOR,
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

        var plan = new CheckoutPlan(
            OrderNumber: orderNumber,
            CustomerPrincipalId: customerPrincipalId,
            VoucherCode: appliedVoucher,
            Reservations: [.. cart.Items.Select(item =>
                new SagaReservation(item.MerchantPrincipalId ?? string.Empty, item.ProductId, item.Quantity)
            )],
            FlashSaleClaims: [.. priceResult.Lines
                .Where(l => l.AppliedFlashSaleId is not null)
                .Select(l => new FlashSaleClaim(l.AppliedFlashSaleId!, l.ProductId, l.Quantity))
            ],
            MerchantPrincipalId: merchantPrincipalId,
            TotalOrderAmount: priceResult.FinalTotal,
            MerchantAmount: priceResult.Subtotal - priceResult.VoucherDiscount,
            ShippingFeeAmount: priceResult.FinalShippingFee
        );

        var outcome = await _sagaRunner.RunAsync(plan);

        if (!outcome.Succeeded) {
            order.Status = OrderStatus.CANCELLED;
            order.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            throw new CheckoutFailedException(orderNumber, outcome.FailureReason ?? "a checkout step was refused");
        }

        await _cartService.ClearCartAsync(customerPrincipalId);

        return MapToOrderResponse(order);
    }

    private static string ResolveMerchantPrincipal(CustomerCart cart) {
        string? merchantPrincipalId = cart.Items.FirstOrDefault()?.MerchantPrincipalId;

        if (string.IsNullOrWhiteSpace(merchantPrincipalId)) {
            throw new InvalidOperationException(
                "the items in this cart carry no merchant, so there is nobody to quote shipping "
              + "for and nobody to pay; remove them and add them again"
            );
        }

        return merchantPrincipalId;
    }

    private static bool IsPriced(ShippingOptionResult option) => option.BaseShippingFee is > 0m;

    private static SelectedShippingQuote AsQuote(ShippingOptionResult option) =>
        new(option.ServiceTier, option.BaseShippingFee!.Value, option.DistanceKm);

    private static string Describe(ShippingOptionResult option) =>
        option.UnavailableReason is null ? option.ServiceTier : $"{option.ServiceTier}: {option.UnavailableReason}";

    private async Task<SelectedShippingQuote> QuoteShippingAsync(string? requestedTier, string merchantPrincipalId) {
        var quote = await _shippingClient.EstimateShippingOptionsAsync(
            ShippingRateCardProbe.Latitude,
            ShippingRateCardProbe.Longitude,
            ShippingRateCardProbe.Latitude,
            ShippingRateCardProbe.Longitude,
            ShippingRateCardProbe.WeightGrams,
            merchantPrincipalId
        );

        if (quote.DistanceKm != ShippingRateCardProbe.ExpectedDistanceKm) {
            _logger.LogWarning(
                "matching returned {DistanceKm}km between two identical points; the rate-card probe "
              + "assumes 0 and the fees on these orders are no longer tier floors",
                quote.DistanceKm
            );
        }

        if (quote.Options.Count == 0) {
            _logger.LogError(
                "matching answered EstimateShippingOptions with no courier options at all. That is "
              + "not a refusal — it carries no tier and no reason — so this checkout is refused as "
              + "an unreadable quote rather than reported to the customer as an address no courier "
              + "serves. Check the proto version and MATCHING_GRPC_URL before looking at the cart"
            );

            throw new ShippingQuoteMalformedException("the response carried no courier options at all");
        }

        var available = quote.Options.Where(o => o.IsAvailable).ToList();

        if (available.Count == 0) {
            throw new ShippingNotServiceableException([.. quote.Options.Select(Describe)]);
        }

        if (string.IsNullOrWhiteSpace(requestedTier)) {
            var unreadable = available.Where(o => !IsPriced(o)).ToList();

            if (unreadable.Count > 0) {
                _logger.LogError(
                    "matching offered {Tiers} as available with no usable fee, so the cheapest "
                  + "available tier cannot be established and this checkout is refused. An option "
                  + "with no Money on it used to be read as free shipping",
                    string.Join(", ", unreadable.Select(o => o.ServiceTier))
                );

                throw new ShippingQuoteMalformedException(
                    $"{string.Join(", ", unreadable.Select(o => o.ServiceTier))} came back available "
                  + "with no usable fee, so the cheapest available tier cannot be established"
                );
            }

            return AsQuote(available
                .OrderBy(o => o.BaseShippingFee!.Value)
                .ThenBy(o => o.ServiceTier, StringComparer.Ordinal)
                .First());
        }

        var matches = quote.Options
            .Where(o => string.Equals(o.ServiceTier, requestedTier, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) {
            throw new ShippingTierUnknownException(requestedTier, [.. available.Select(o => o.ServiceTier)]);
        }

        var offered = matches.Where(o => o.IsAvailable).ToList();

        if (offered.Count == 0) {
            throw new ShippingTierNotEstablishedException(
                requestedTier,
                matches.Select(o => o.UnavailableReason).FirstOrDefault(reason => reason is not null),
                [.. available.Select(o => o.ServiceTier)]
            );
        }

        var priced = offered.Where(IsPriced).OrderBy(o => o.BaseShippingFee!.Value).ToList();

        if (priced.Count == 0) {
            _logger.LogError(
                "matching offered {Tier} as available with no usable fee, so this checkout is "
              + "refused rather than priced at zero. An option with no Money on it used to be read "
              + "as free shipping",
                requestedTier
            );

            throw new ShippingQuoteMalformedException(
                $"'{requestedTier}' came back available with no usable fee"
            );
        }

        return AsQuote(priced[0]);
    }

    public async Task<OrderResponse?> GetOrderByIdAsync(Guid orderId) {
        var order = await _dbContext.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        return order == null ? null : MapToOrderResponse(order);
    }

    public async Task<CustomerOrderPage> GetCustomerOrdersAsync(string customerPrincipalId, OrderStatus? status, int page, int pageSize) {
        var query = _dbContext.Orders
            .Include(o => o.Items)
            .Where(o => o.CustomerPrincipalId == customerPrincipalId);

        if (status.HasValue) {
            query = query.Where(o => o.Status == status.Value);
        }

        var totalCount = await query.CountAsync();

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new CustomerOrderPage([.. orders.Select(MapToOrderResponse)], totalCount);
    }

    public async Task<OrderResponse> TransitionOrderStatusAsync(Guid orderId, OrderStatus newStatus) {
        var order = await _dbContext.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId)
            ?? throw new KeyNotFoundException($"OrderEntity '{orderId}' not found");

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

    private static OrderResponse MapToOrderResponse(OrderEntity order) => new(
        order.Id,
        order.OrderNumber,
        order.CustomerPrincipalId,
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
        order.ShippingQuoteBasis.ToString(),
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
