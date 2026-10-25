using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Products;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Requests;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Persistence;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;
using Kinetix.OrderService.Application.Lifecycle;

namespace Kinetix.OrderService.Application.Services;

public class OrderService(
    OrderDbContext dbContext,
    ICartService cartService,
    IProductDirectory products,
    IMerchantStanding merchants,
    IPricingClient pricingClient,
    IShippingClient shippingClient,
    IAddressDirectory addresses,
    IAddressGeocoder geocoder,
    CheckoutSagaRunner sagaRunner,
    ILogger<OrderService> logger
) : IOrderService {
    private readonly OrderDbContext _dbContext = dbContext;
    private readonly ICartService _cartService = cartService;
    private readonly IProductDirectory _products = products;
    private readonly IMerchantStanding _merchants = merchants;
    private readonly IPricingClient _pricingClient = pricingClient;
    private readonly IShippingClient _shippingClient = shippingClient;
    private readonly IAddressDirectory _addresses = addresses;
    private readonly IAddressGeocoder _geocoder = geocoder;
    private readonly CheckoutSagaRunner _sagaRunner = sagaRunner;
    private readonly ILogger<OrderService> _logger = logger;

    public async Task<OrderResponse> CheckoutAsync(
        string customerPrincipalId,
        CheckoutRequest request,
        string? idempotencyKey
    ) {
        var recipient = CheckoutRecipient.From(request);
        var key = KeyOf(idempotencyKey);
        var fingerprint = CheckoutFingerprint.Of(request);

        if (key is not null && await ReplayAsync(customerPrincipalId, key, fingerprint) is { } replayed) {
            return replayed;
        }

        var cart = await _cartService.GetCartAsync(customerPrincipalId);
        if (cart.Items.Count == 0) {
            throw new EmptyCartException();
        }

        RequireLineQuantities(cart.Items);

        string? requestedVoucher = request.VoucherCode ?? cart.AppliedVoucherCode;

        var products = new List<CatalogProduct>(cart.Items.Count);
        foreach (var item in cart.Items) {
            products.Add(await _products.GetProductAsync(item.ProductId));
        }

        RefuseOwnProducts(customerPrincipalId, products);
        string merchantPrincipalId = ResolveMerchantPrincipal(products);

        if (!await _merchants.MaySellAsync(merchantPrincipalId)) {
            throw new MerchantMayNotSellException(merchantPrincipalId);
        }

        long parcelWeightGrams = ParcelWeight(cart.Items, products);
        var route = await RouteAsync(merchantPrincipalId, recipient.ShippingAddress);

        var shippingQuote = await QuoteShippingAsync(
            request.ShippingServiceTier,
            merchantPrincipalId,
            route,
            parcelWeightGrams
        );
        decimal baseShippingFee = shippingQuote.BaseShippingFee;

        var priceLines = cart.Items
            .Zip(
                products,
                (item, product) =>
                    new PriceLine(product.ProductId, product.CategoryId, product.UnitPrice, item.Quantity)
            )
            .ToList();

        var priceResult = await _pricingClient.CalculatePriceAsync(
            requestedVoucher,
            priceLines,
            shippingQuote.Journey,
            merchantPrincipalId
        );

        string? appliedVoucher = priceResult.AppliedVoucher;

        if (
            priceResult.FinalShippingFee < 0m || priceResult.FinalShippingFee > priceResult.BaseShippingFee
        ) {
            _logger.LogError(
                "pricing returned a final shipping fee of {PricingFinal} against a base of "
              + "{PricingBase}, which is not a discount of its own quote. Refusing before an "
              + "order row exists rather than debiting it.",
                priceResult.FinalShippingFee,
                priceResult.BaseShippingFee
            );

            throw new ShippingFeeContradictedException(
                baseShippingFee,
                priceResult.BaseShippingFee,
                priceResult.FinalShippingFee
            );
        }

        decimal merchantAmount = priceResult.Subtotal - priceResult.VoucherDiscount;
        var unchargeable = UnchargeableAmounts(priceResult, merchantAmount);
        var pricedItems = PricedItems(cart.Items, products, priceResult, unchargeable);

        if (unchargeable.Count > 0) {
            _logger.LogError(
                "pricing's amounts for this checkout cannot be escrowed as they stand, so it is "
              + "refused before an order row exists rather than debited and rolled back: {Faults}",
                string.Join("; ", unchargeable)
            );

            throw new OrderAmountsUnchargeableException(string.Join("; ", unchargeable));
        }

        if (priceResult.FinalTotal == 0m) {
            throw new OrderTotalNotChargeableException();
        }

        string uniqueSuffix = Guid.NewGuid().ToString("N")[..8].ToUpper();
        string orderNumber = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{uniqueSuffix}";

        var order = new OrderEntity {
            OrderNumber = orderNumber,
            CustomerPrincipalId = customerPrincipalId,
            MerchantPrincipalId = merchantPrincipalId,
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
            DeliveryLatitude = route.DropOff.Latitude,
            DeliveryLongitude = route.DropOff.Longitude,
            ParcelWeightGrams = parcelWeightGrams,
            ShippingAddress = recipient.ShippingAddress,
            RecipientName = recipient.RecipientName,
            RecipientPhone = recipient.RecipientPhone,
            IdempotencyKey = key,
            IdempotencyFingerprint = key is null ? null : fingerprint,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items = pricedItems
        };

        _dbContext.Orders.Add(order);
        _dbContext.RecordPlaced(order, LifecycleActor.Buyer(customerPrincipalId));

        try {
            await _dbContext.SaveChangesAsync();
        } catch (DbUpdateException e) when (
            key is not null && UniqueViolation.On(e, OrderDbContext.IdempotencyKeyIndex)
        ) {
            _dbContext.ChangeTracker.Clear();

            return await ReplayAsync(customerPrincipalId, key, fingerprint)
                ?? throw new InvalidOperationException(
                    $"the Idempotency-Key of {orderNumber} collided with an order that cannot be read back",
                    e
                );
        }

        var plan = new CheckoutPlan(
            OrderNumber: orderNumber,
            CustomerPrincipalId: customerPrincipalId,
            VoucherCode: appliedVoucher,
            Reservations: [.. cart.Items.Select(item =>
                new SagaReservation(item.MerchantPrincipalId ?? string.Empty, item.ProductId, item.Quantity)
            )],
            FlashSaleClaims: [
                .. priceResult.Lines
                    .Where(l => l.AppliedFlashSaleId is not null)
                    .Select(l => new FlashSaleClaim(l.AppliedFlashSaleId!, l.ProductId, l.Quantity))
            ],
            MerchantPrincipalId: merchantPrincipalId,
            TotalOrderAmount: priceResult.FinalTotal,
            MerchantAmount: merchantAmount,
            ShippingFeeAmount: priceResult.FinalShippingFee,
            ShippingAddress: recipient.ShippingAddress,
            RecipientName: recipient.RecipientName,
            RecipientPhone: recipient.RecipientPhone,
            FulfillmentLines: [.. cart.Items.Select(item =>
                new FulfillmentLine(item.ProductId, item.Quantity)
            )]
        );

        var outcome = await _sagaRunner.RunAsync(plan);

        if (!outcome.Succeeded) {
            _dbContext.ChangeStatus(order, OrderStatus.CANCELLED, LifecycleActor.System, DateTime.UtcNow);
            await _dbContext.SaveChangesAsync();

            throw new CheckoutFailedException(
                orderNumber,
                outcome.FailureReason ?? "a checkout step was refused",
                outcome.RolledBack,
                outcome.DependencyUnavailable,
                replayed: false
            );
        }

        _dbContext.ChangeStatus(order, OrderStatus.PAID, LifecycleActor.Buyer(customerPrincipalId), DateTime.UtcNow);
        await _dbContext.SaveChangesAsync();

        await ClearPaidCartAsync(customerPrincipalId, cart, orderNumber);

        return MapToOrderResponse(order, null);
    }

    private static string? KeyOf(string? idempotencyKey) {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) {
            return null;
        }

        if (idempotencyKey.Length > OrderEntity.IdempotencyKeyMaxLength) {
            throw new FieldTooLongException("Idempotency-Key", OrderEntity.IdempotencyKeyMaxLength);
        }

        return idempotencyKey;
    }

    private async Task<OrderResponse?> ReplayAsync(string customerPrincipalId, string key, string fingerprint) {
        var existing = await _dbContext.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.IdempotencyKey == key && o.CustomerPrincipalId == customerPrincipalId);

        if (existing is null) {
            return null;
        }

        if (existing.IdempotencyFingerprint is not null && existing.IdempotencyFingerprint != fingerprint) {
            throw new IdempotencyKeyReusedException(existing.OrderNumber);
        }

        if (existing.Status == OrderStatus.PENDING_PAYMENT) {
            throw new CheckoutInProgressException(existing.OrderNumber);
        }

        var saga = await _dbContext.CheckoutSagas
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrderNumber == existing.OrderNumber);

        if (existing.Status == OrderStatus.CANCELLED && saga is not null && saga.State != SagaState.Completed) {
            throw new CheckoutFailedException(
                existing.OrderNumber,
                saga.FailureReason ?? "a checkout step was refused",
                rolledBack: saga.State == SagaState.Compensated,
                dependencyUnavailable: false,
                replayed: true
            );
        }

        return MapToOrderResponse(existing, await DriverOfAsync(existing.OrderNumber));
    }

    private async Task ClearPaidCartAsync(string customerPrincipalId, CustomerCart checkedOut, string orderNumber) {
        try {
            await _cartService.RemoveCheckedOutAsync(customerPrincipalId, checkedOut);
        } catch (Exception e) when (e is not OperationCanceledException) {
            _logger.LogWarning(
                e,
                "{Order} is PAID, but the lines it bought could not be taken out of the buyer's cart; it still "
                  + "holds them. The order stands and the checkout answers it as placed",
                orderNumber
            );
        }
    }

    private static void RequireLineQuantities(IReadOnlyList<CartItem> items) {
        var outOfRange = items.FirstOrDefault(item =>
            item.Quantity < CartItem.MinQuantity || item.Quantity > CartItem.MaxQuantity
        );

        if (outOfRange is not null) {
            throw new CartLineQuantityOutOfRangeException(
                outOfRange.ProductId,
                outOfRange.Quantity,
                CartItem.MinQuantity,
                CartItem.MaxQuantity
            );
        }
    }

    private static void RefuseOwnProducts(string customerPrincipalId, IReadOnlyList<CatalogProduct> products) {
        var own = products.FirstOrDefault(product =>
            string.Equals(product.MerchantPrincipalId, customerPrincipalId, StringComparison.Ordinal)
        );

        if (own is not null) {
            throw new OwnProductNotPurchasableException(own.ProductId);
        }
    }

    private static long ParcelWeight(IReadOnlyList<CartItem> items, IReadOnlyList<CatalogProduct> products) {
        var unweighed = products
            .Where(p => p.WeightGrams is null or <= 0)
            .Select(p => p.ProductId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unweighed.Count > 0) {
            throw new ProductWeightUnknownException(unweighed);
        }

        return checked(items.Zip(products, (item, product) => product.WeightGrams!.Value * item.Quantity).Sum());
    }

    private async Task<ShippingRoute> RouteAsync(string merchantPrincipalId, string shippingAddress) {
        var pickup = await _addresses.PickupPointAsync(merchantPrincipalId)
            ?? throw new MerchantStoreUnplacedException(merchantPrincipalId);

        var placement = await _geocoder.PlaceAsync(shippingAddress);

        if (placement.Point is { } dropOff) {
            return new ShippingRoute(pickup, dropOff);
        }

        if (placement.Failure is { } failure) {
            throw new ShippingAddressUnplacedException(failure, placement.Detail);
        }

        throw new AddressPlacementUnavailableException("the address came back neither placed nor refused");
    }

    private static string ResolveMerchantPrincipal(IReadOnlyList<CatalogProduct> products) {
        var merchantless = products.FirstOrDefault(product => string.IsNullOrWhiteSpace(product.MerchantPrincipalId));
        if (merchantless is not null) {
            throw new ProductHasNoMerchantException(merchantless.ProductId);
        }

        var merchants = products
            .Select(product => product.MerchantPrincipalId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (merchants.Count > 1) {
            throw new CartSpansTwoMerchantsException(merchants.Count);
        }

        return merchants[0];
    }

    private static string Amount(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static List<OrderItem> PricedItems(
        IReadOnlyList<CartItem> cartItems,
        IReadOnlyList<CatalogProduct> products,
        PriceCalculationResult price,
        List<string> faults
    ) {
        var items = new List<OrderItem>(cartItems.Count);

        for (int index = 0; index < cartItems.Count; index++) {
            var product = products[index];
            var quantity = cartItems[index].Quantity;
            var priced = price.Lines.FirstOrDefault(line =>
                string.Equals(line.ProductId, product.ProductId, StringComparison.Ordinal)
            );

            if (priced is null || priced.Quantity != quantity) {
                faults.Add($"pricing did not price {quantity} of {product.ProductId}");
                continue;
            }

            items.Add(new OrderItem {
                ProductId = product.ProductId,
                ProductTitle = product.Title,
                UnitPrice = priced.FinalUnitPrice,
                Quantity = quantity,
                LineSubtotal = priced.LineTotal,
            });
        }

        decimal pricedTotal = items.Sum(item => item.LineSubtotal);
        if (faults.Count == 0 && pricedTotal != price.Subtotal) {
            faults.Add(
                $"the priced lines come to {Amount(pricedTotal)} but the subtotal is {Amount(price.Subtotal)}"
            );
        }

        return items;
    }

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

    private static string Name(ShippingOptionResult option) =>
        string.IsNullOrWhiteSpace(option.ServiceTier) ? "an unnamed tier" : $"'{option.ServiceTier}'";

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
        }

        faults.AddRange(
            options
                .GroupBy(o => o.ServiceTier, StringComparer.Ordinal)
                .Where(tier => tier.Count() > 1)
                .Select(tier =>
                    $"'{tier.Key}' is listed {tier.Count()} times, so which of those entries describes "
                  + "the journey cannot be told"
                )
        );

        return faults;
    }

    private static SelectedShippingQuote Establish(
        ShippingOptionResult option,
        QuotedShippingResult quoted,
        long parcelWeightGrams
    ) =>
        new(
            option.ServiceTier,
            quoted.BaseShippingFee,
            option.DistanceKm,
            ShippingQuoteBasis.DISTANCE_QUOTED,
            Journey(option, parcelWeightGrams)
        );

    private static ShippingJourney Journey(ShippingOptionResult option, long parcelWeightGrams) =>
        new(option.ServiceTier, option.DistanceKm, parcelWeightGrams);

    private async Task<SelectedShippingQuote> QuoteShippingAsync(
        string? requestedTier,
        string merchantPrincipalId,
        ShippingRoute route,
        long parcelWeightGrams
    ) {
        var quote = await _shippingClient.EstimateShippingOptionsAsync(
            route.Pickup.Latitude,
            route.Pickup.Longitude,
            route.DropOff.Latitude,
            route.DropOff.Longitude,
            parcelWeightGrams,
            merchantPrincipalId
        );

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

        var available = quote.Options.Where(o => o.IsAvailable).ToList();

        if (available.Count == 0) {
            throw new ShippingNotServiceableException([.. quote.Options.Select(Describe)]);
        }

        var quoted = await _pricingClient.QuoteShippingAsync([.. available.Select(o => Journey(o, parcelWeightGrams))]);

        var priced = quoted
            .Where(q => q.Priced)
            .ToDictionary(q => q.ServiceTier, StringComparer.Ordinal);

        var availableTiers = available.Select(o => o.ServiceTier).ToList();

        if (string.IsNullOrWhiteSpace(requestedTier)) {
            var cheapest = available
                .Where(o => priced.ContainsKey(o.ServiceTier))
                .OrderBy(o => priced[o.ServiceTier].BaseShippingFee)
                .ThenBy(o => o.ServiceTier, StringComparer.Ordinal)
                .FirstOrDefault();

            if (cheapest is null) {
                _logger.LogError(
                    "the fleet offered {Count} tier(s) for this parcel and pricing holds a rate "
                  + "for none of them ({Tiers}), so this checkout is refused rather than shipped "
                  + "at a fee nobody quoted. An unpriced tier is one nobody may be charged for, "
                  + "not one that is free",
                    available.Count,
                    string.Join(", ", availableTiers)
                );

                throw new ShippingTierNotEstablishedException(
                    "any",
                    "pricing holds no rate for it",
                    availableTiers
                );
            }

            return Establish(cheapest, priced[cheapest.ServiceTier], parcelWeightGrams);
        }

        var matches = quote.Options
            .Where(o => string.Equals(o.ServiceTier, requestedTier, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) {
            throw new ShippingTierUnknownException(requestedTier, availableTiers);
        }

        var match = matches[0];

        if (!match.IsAvailable) {
            throw new ShippingTierNotEstablishedException(
                requestedTier,
                match.UnavailableReason,
                availableTiers
            );
        }

        if (!priced.TryGetValue(requestedTier, out var quotedTier)) {
            throw new ShippingTierNotEstablishedException(
                requestedTier,
                "pricing holds no rate for it",
                availableTiers
            );
        }

        return Establish(match, quotedTier, parcelWeightGrams);
    }

    public async Task<OrderResponse?> GetOrderByIdAsync(Guid orderId) {
        var order = await _dbContext.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) {
            return null;
        }

        return MapToOrderResponse(order, await DriverOfAsync(order.OrderNumber));
    }

    public async Task<CustomerOrderPage> GetCustomerOrdersAsync(
        string customerPrincipalId,
        OrderStatus? status,
        int page,
        int pageSize
    ) {
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

        var drivers = await DriversOfAsync([.. orders.Select(o => o.OrderNumber)]);

        return new CustomerOrderPage(
            [.. orders.Select(o => MapToOrderResponse(o, drivers.GetValueOrDefault(o.OrderNumber)))],
            totalCount
        );
    }

    public async Task<OrderChangePage> OrdersChangedSinceAsync(
        DateTime? updatedThrough,
        string lastOrderNumber,
        int limit
    ) {
        var query = _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .AsQueryable();

        if (updatedThrough is DateTime through) {
            query = query.Where(o =>
                o.UpdatedAt > through
             || (o.UpdatedAt == through && string.Compare(o.OrderNumber, lastOrderNumber) > 0)
            );
        }

        var rows = await query
            .OrderBy(o => o.UpdatedAt)
            .ThenBy(o => o.OrderNumber)
            .Take(limit + 1)
            .ToListAsync();

        bool hasMore = rows.Count > limit;
        var page = hasMore ? rows.Take(limit) : rows;

        return new OrderChangePage(
            [.. page.Select(o =>
                new OrderChange(
                    o.OrderNumber,
                    o.CustomerPrincipalId,
                    o.MerchantPrincipalId,
                    o.Status,
                    [.. o.Items.Select(i => i.ProductTitle)],
                    DateTime.SpecifyKind(o.CreatedAt, DateTimeKind.Utc),
                    DateTime.SpecifyKind(o.UpdatedAt, DateTimeKind.Utc)
                )
            )],
            hasMore
        );
    }

    public async Task<OrderChangePage> OrdersChangedAfterAsync(
        ulong transactionId,
        string lastOrderNumber,
        int limit
    ) {
        var after = transactionId.ToString(CultureInfo.InvariantCulture);
        var take = limit + 1;

        var rows = await _dbContext.Orders
            .FromSqlInterpolated($@"
                SELECT *
                FROM orders
                WHERE change_transaction_id < pg_snapshot_xmin(pg_current_snapshot())
                  AND (change_transaction_id, order_number) > (CAST({after} AS xid8), {lastOrderNumber})
                ORDER BY change_transaction_id, order_number
                LIMIT {take}")
            .AsNoTracking()
            .ToListAsync();

        bool hasMore = rows.Count > limit;
        var page = hasMore ? rows[..limit] : rows;
        var ids = page.Select(o => o.Id).ToList();

        var titles = (await _dbContext.OrderItems
            .AsNoTracking()
            .Where(i => ids.Contains(i.OrderId))
            .OrderBy(i => i.Id)
            .Select(i => new { i.OrderId, i.ProductTitle })
            .ToListAsync()
        ).ToLookup(i => i.OrderId, i => i.ProductTitle);

        return new OrderChangePage(
            [.. page.Select(o =>
                new OrderChange(
                    o.OrderNumber,
                    o.CustomerPrincipalId,
                    o.MerchantPrincipalId,
                    o.Status,
                    [.. titles[o.Id]],
                    DateTime.SpecifyKind(o.CreatedAt, DateTimeKind.Utc),
                    DateTime.SpecifyKind(o.UpdatedAt, DateTimeKind.Utc)
                )
            )],
            hasMore,
            page.Count > 0 ? page[^1].ChangeTransactionId : transactionId
        );
    }

    private async Task<string?> DriverOfAsync(string orderNumber) {
        return await _dbContext.ShippingSettlements
            .Where(s => s.OrderNumber == orderNumber)
            .Select(s => s.DriverPrincipalId)
            .FirstOrDefaultAsync();
    }

    private async Task<Dictionary<string, string>> DriversOfAsync(List<string> orderNumbers) {
        return await _dbContext.ShippingSettlements
            .Where(s => orderNumbers.Contains(s.OrderNumber))
            .ToDictionaryAsync(s => s.OrderNumber, s => s.DriverPrincipalId, StringComparer.Ordinal);
    }

    private static OrderResponse MapToOrderResponse(OrderEntity order, string? driverPrincipalId) => new(
        order.Id,
        order.OrderNumber,
        order.CustomerPrincipalId,
        order.MerchantPrincipalId,
        driverPrincipalId,
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
        [.. order.Items.Select(i =>
            new OrderItemResponse(
                i.Id,
                i.ProductId,
                i.ProductTitle,
                i.UnitPrice,
                i.Quantity,
                i.LineSubtotal
            )
        )]
    );
}
