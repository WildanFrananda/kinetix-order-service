using System.Globalization;
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
            throw new EmptyCartException();
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

        decimal merchantAmount = priceResult.Subtotal - priceResult.VoucherDiscount;
        var unchargeable = UnchargeableAmounts(priceResult, merchantAmount);

        if (unchargeable.Count > 0) {
            _logger.LogError(
                "pricing's amounts for this checkout cannot be escrowed as they stand, so it is "
              + "refused before an order row exists rather than debited and rolled back: {Faults}",
                string.Join("; ", unchargeable)
            );

            throw new OrderAmountsUnchargeableException(string.Join("; ", unchargeable));
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
            ShippingQuoteBasis = shippingQuote.Basis,
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
            MerchantAmount: merchantAmount,
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
            throw new CartItemsHaveNoMerchantException();
        }

        return merchantPrincipalId;
    }

    private static string Amount(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static List<string> UnchargeableAmounts(PriceCalculationResult price, decimal merchantAmount) {
        var faults = new List<string>();

        if (merchantAmount < 0m) {
            faults.Add(
                $"a voucher discount of {Amount(price.VoucherDiscount)} against a subtotal of "
              + $"{Amount(price.Subtotal)} would pay the merchant {Amount(merchantAmount)}"
            );
        }

        decimal split = merchantAmount + price.FinalShippingFee;

        if (price.FinalTotal != split) {
            faults.Add(
                $"the total to escrow ({Amount(price.FinalTotal)}) is not what that escrow would "
              + $"split into: {Amount(merchantAmount)} to the merchant plus "
              + $"{Amount(price.FinalShippingFee)} of shipping comes to {Amount(split)}"
            );
        }

        faults.AddRange(new (string Field, decimal Amount)[] {
            ("total", price.FinalTotal),
            ("merchant", merchantAmount),
            ("shipping", price.FinalShippingFee),
        }
            .Where(a => !MinorUnits.IsWhole(a.Amount))
            .Select(a => $"the {a.Field} amount {Amount(a.Amount)} is not a whole number of minor units")
        );

        return faults;
    }

    private static bool IsPriced(ShippingOptionResult option) => option.BaseShippingFee is > 0m;

    private static string Name(ShippingOptionResult option) =>
        string.IsNullOrWhiteSpace(option.ServiceTier) ? "an unnamed tier" : $"'{option.ServiceTier}'";

    private static string Fee(ShippingOptionResult option) =>
        option.BaseShippingFee is null ? "no fee at all" : Amount(option.BaseShippingFee.Value);

    private static string Describe(ShippingOptionResult option) =>
        option.UnavailableReason is null ? option.ServiceTier : $"{option.ServiceTier}: {option.UnavailableReason}";

    private static List<string> UnreadableParts(IReadOnlyList<ShippingOptionResult> options) {
        var faults = new List<string>();

        foreach (var option in options) {
            if (string.IsNullOrWhiteSpace(option.ServiceTier)) {
                faults.Add("an option carries no service tier, so nothing names what would be sold");
            } else if (option.ServiceTier.Length > OrderEntity.ServiceTierMaxLength) {
                faults.Add(
                    $"'{option.ServiceTier}' is longer than the {OrderEntity.ServiceTierMaxLength} "
                  + "characters an order can record as its tier"
                );
            }

            if (!option.IsAvailable) {
                continue;
            }

            if (option.UnavailableReason is not null) {
                faults.Add(
                    $"{Name(option)} is offered as available and states why it is not: {option.UnavailableReason}"
                );
            }

            if (!IsPriced(option)) {
                faults.Add($"{Name(option)} is offered as available with no usable fee ({Fee(option)})");
            }
        }

        faults.AddRange(options
            .GroupBy(o => o.ServiceTier, StringComparer.Ordinal)
            .Where(tier => tier.Count() > 1)
            .Select(tier =>
                $"'{tier.Key}' is listed {tier.Count()} times, at {string.Join(" and ", tier.Select(Fee))}, "
              + "so which of those fees is the price cannot be told"
            )
        );

        if (options.Count > 0 && options.All(o => !o.IsAvailable)) {
            faults.Add(
                $"every tier came back unavailable ({string.Join("; ", options.Select(Describe))}), "
              + $"and this quote was asked at {ShippingRateCardProbe.ExpectedDistanceKm} km and "
              + $"{ShippingRateCardProbe.WeightGrams} g, where a rate card has no route and no "
              + "parcel to refuse, so this is an answer order cannot read rather than a verdict "
              + "about this delivery"
            );
        }

        return faults;
    }

    private SelectedShippingQuote Establish(ShippingOptionResult option) {
        var basis = option.DistanceKm == ShippingRateCardProbe.ExpectedDistanceKm
            ? ShippingQuoteBasis.TIER_FLOOR
            : ShippingQuoteBasis.DISTANCE_QUOTED;

        if (basis != ShippingQuoteBasis.TIER_FLOOR) {
            _logger.LogWarning(
                "{Tier} was priced at {Fee} against {DistanceKm}km, not the "
              + "{ExpectedDistanceKm}km the rate-card probe asks at, so this order's fee is not a "
              + "tier floor and its row is stamped {Basis}",
                option.ServiceTier, option.BaseShippingFee!.Value, option.DistanceKm,
                ShippingRateCardProbe.ExpectedDistanceKm, basis
            );
        }

        return new SelectedShippingQuote(
            option.ServiceTier, option.BaseShippingFee!.Value, option.DistanceKm, basis
        );
    }

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
                "matching measured {DistanceKm}km between the two identical points this quote was "
              + "asked at, where the rate-card probe expects {ExpectedDistanceKm}; matching is no "
              + "longer answering the question order thinks it is asking",
                quote.DistanceKm, ShippingRateCardProbe.ExpectedDistanceKm
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

        var unreadable = UnreadableParts(quote.Options);

        if (unreadable.Count > 0) {
            _logger.LogError(
                "matching's shipping answer cannot be read as a rate card, so this checkout is "
              + "refused rather than priced from a line order cannot trust, and the customer is "
              + "told this is our fault rather than a courier's verdict: {Faults}. Check the proto "
              + "version and MATCHING_GRPC_URL before looking at the cart",
                string.Join("; ", unreadable)
            );

            throw new ShippingQuoteMalformedException(string.Join("; ", unreadable));
        }

        if (string.IsNullOrWhiteSpace(requestedTier)) {
            return Establish(quote.Options
                .Where(o => o.IsAvailable)
                .OrderBy(o => o.BaseShippingFee!.Value)
                .ThenBy(o => o.ServiceTier, StringComparer.Ordinal)
                .First());
        }

        var availableTiers = quote.Options.Where(o => o.IsAvailable).Select(o => o.ServiceTier).ToList();

        var matches = quote.Options
            .Where(o => string.Equals(o.ServiceTier, requestedTier, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) {
            throw new ShippingTierUnknownException(requestedTier, availableTiers);
        }

        var match = matches[0];

        if (!match.IsAvailable) {
            throw new ShippingTierNotEstablishedException(
                requestedTier, match.UnavailableReason, availableTiers
            );
        }

        return Establish(match);
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
