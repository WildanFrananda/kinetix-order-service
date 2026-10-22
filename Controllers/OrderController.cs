using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Requests;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Security;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/orders")]
public class OrderController(IOrderService orderService) : ControllerBase {
    private readonly IOrderService _orderService = orderService;

    public const int MaxPageSize = 100;

    private bool TryGetCallerPrincipal(out string customerPrincipalId) {
        customerPrincipalId = CallerPrincipal.Of(User) ?? string.Empty;
        return customerPrincipalId.Length > 0;
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest request) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }

        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();

        try {
            var order = await _orderService.CheckoutAsync(customerPrincipalId, request, idempotencyKey);
            return Created(Url.Action(nameof(GetOrderById), new { orderId = order.Id }), order);
        } catch (RecipientMissingException ex) {
            return BadRequest(new { error = "RECIPIENT_REQUIRED", field = ex.Field, message = ex.Message });
        } catch (FieldTooLongException ex) {
            return BadRequest(new {
                error = "FIELD_TOO_LONG",
                field = ex.Field,
                maxLength = ex.MaxLength,
                message = ex.Message,
            });
        } catch (IdempotencyKeyReusedException ex) {
            return UnprocessableEntity(new {
                error = "IDEMPOTENCY_KEY_REUSED",
                orderNumber = ex.OrderNumber,
                message = "this Idempotency-Key was already used for a checkout with a different request. "
                        + "Send a new key for a new checkout, or repeat the original request exactly.",
            });
        } catch (CheckoutInProgressException ex) {
            return Conflict(new {
                error = "CHECKOUT_IN_PROGRESS",
                orderNumber = ex.OrderNumber,
                message = "the checkout this Idempotency-Key started has not finished yet. Repeat the same "
                        + "request with the same key shortly to learn how it ended.",
            });
        } catch (PricingRefusedException ex) {
            return UnprocessableEntity(new {
                error = "PRICING_REFUSED",
                reason = ex.Reason,
                message = "this order cannot be priced as it stands, so it was not placed. Nothing has "
                        + "been charged or reserved.",
            });
        } catch (OrderTotalNotChargeableException) {
            return UnprocessableEntity(new {
                error = "ORDER_TOTAL_NOT_CHARGEABLE",
                message = "this order comes to nothing, and an amount of zero cannot be charged or held, so it "
                        + "was not placed. Nothing has been charged or reserved.",
            });
        } catch (OwnProductNotPurchasableException ex) {
            return UnprocessableEntity(new {
                error = "OWN_PRODUCT_NOT_PURCHASABLE",
                productId = ex.ProductId,
                message = "this cart holds a product you sell yourself, and a seller cannot buy its own "
                        + "product. Remove it from the cart and check out again.",
            });
        } catch (CartLineQuantityOutOfRangeException ex) {
            return UnprocessableEntity(new {
                error = "CART_LINE_QUANTITY_OUT_OF_RANGE",
                productId = ex.ProductId,
                quantity = ex.Quantity,
                minQuantity = ex.Minimum,
                maxQuantity = ex.Maximum,
                message = ex.Message + ". Change the quantity in the cart and check out again.",
            });
        } catch (CartUnavailableException) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                error = "CART_UNAVAILABLE",
                message = "your cart cannot be read right now, so this order was not placed. Nothing has "
                        + "been charged or reserved — please try again shortly.",
            });
        } catch (PricingUnavailableException) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                error = "PRICING_UNAVAILABLE",
                message = "prices cannot be confirmed right now, so this order was not placed. "
                        + "Nothing has been charged or reserved — please try again shortly.",
            });
        } catch (ShippingUnavailableException) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                error = "SHIPPING_UNAVAILABLE",
                message = "shipping cannot be quoted right now, so this order was not placed. "
                        + "Nothing has been charged or reserved — please try again shortly.",
            });
        } catch (ShippingQuoteMalformedException) {
            return StatusCode(StatusCodes.Status502BadGateway, new {
                error = "SHIPPING_QUOTE_MALFORMED",
                message = "shipping could not be quoted for this order — the courier service "
                        + "answered with something this service cannot read — so this order was "
                        + "not placed. Nothing has been charged or reserved. This is a fault on "
                        + "our side and it has been logged; trying again is unlikely to help.",
            });
        } catch (ShippingTierNotEstablishedException ex) {
            return UnprocessableEntity(new {
                error = "SHIPPING_TIER_NOT_ESTABLISHED",
                message = $"'{ex.RequestedTier}' could not be confirmed for this order, so it was "
                        + "not placed. Nothing has been charged or reserved. Choose one of the "
                        + "tiers listed, or leave the tier out to take the cheapest available.",
                requestedTier = ex.RequestedTier,
                availableTiers = ex.AvailableTiers,
                rateCardReason = ex.RateCardReason,
            });
        } catch (ShippingTierUnknownException ex) {
            return BadRequest(new {
                error = "SHIPPING_TIER_UNKNOWN",
                message = $"'{ex.RequestedTier}' is not a service tier the courier service offers.",
                requestedTier = ex.RequestedTier,
                availableTiers = ex.AvailableTiers,
            });
        } catch (ShippingFeeContradictedException) {
            return StatusCode(StatusCodes.Status502BadGateway, new {
                error = "SHIPPING_FEE_CONTRADICTED",
                message = "the shipping fee could not be confirmed against the courier quote, so "
                        + "this order was not placed. Nothing has been charged or reserved.",
            });
        } catch (OrderAmountsUnchargeableException) {
            return StatusCode(StatusCodes.Status502BadGateway, new {
                error = "ORDER_AMOUNTS_UNCHARGEABLE",
                message = "the amounts on this order do not add up to something that can be "
                        + "charged, so it was not placed. Nothing has been charged or reserved. "
                        + "This is a fault on our side and it has been logged; trying again is "
                        + "unlikely to help.",
            });
        } catch (CheckoutFailedException ex) {
            return CheckoutNotPlaced(ex);
        } catch (EmptyCartException ex) {
            return BadRequest(new { error = "CART_EMPTY", message = ex.Message });
        } catch (ProductHasNoMerchantException ex) {
            return UnprocessableEntity(new {
                error = "PRODUCT_HAS_NO_MERCHANT",
                productId = ex.ProductId,
                message = ex.Message
            });
        } catch (MerchantMayNotSellException ex) {
            return Conflict(new { error = "MERCHANT_NOT_TRADING", message = ex.Message });
        } catch (MerchantStandingUnknownException ex) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                error = "IDENTITY_UNAVAILABLE",
                message = ex.Message + ". Nothing has been charged or reserved; try again shortly.",
            });
        } catch (CartSpansTwoMerchantsException ex) {
            return BadRequest(new {
                error = "CART_SPANS_TWO_MERCHANTS",
                merchantCount = ex.MerchantCount,
                message = ex.Message,
            });
        } catch (ProductNotInCatalogException ex) {
            return NotFound(new { error = "PRODUCT_NOT_IN_CATALOG", productId = ex.ProductId, message = ex.Message });
        } catch (ProductHasNoUsablePriceException ex) {
            return UnprocessableEntity(new {
                error = "PRODUCT_HAS_NO_USABLE_PRICE",
                productId = ex.ProductId,
                message = ex.Message
            });
        } catch (CatalogUnavailableException ex) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                error = "CATALOG_UNAVAILABLE",
                productId = ex.ProductId,
                message = ex.Message
            });
        } catch (ProductWeightUnknownException ex) {
            return UnprocessableEntity(new {
                error = "PRODUCT_WEIGHT_UNKNOWN",
                productIds = ex.ProductIds,
                message = "a product in this cart has no weight, so its shipping cannot be priced and "
                        + "this order was not placed. Nothing has been charged or reserved. The "
                        + "merchant has to set the product's weight.",
            });
        } catch (MerchantStoreUnplacedException) {
            return UnprocessableEntity(new {
                error = "MERCHANT_STORE_NOT_ON_MAP",
                message = "the merchant's store address has no point on the map, so the distance to "
                        + "you cannot be measured and this order was not placed. Nothing has been "
                        + "charged or reserved.",
            });
        } catch (ShippingAddressUnplacedException ex) {
            return UnprocessableEntity(new {
                error = "SHIPPING_ADDRESS_NOT_PLACED",
                reason = PlacementReason(ex.Failure),
                message = PlacementMessage(ex.Failure)
                        + " This order was not placed, and nothing has been charged or reserved.",
            });
        } catch (AddressPlacementUnavailableException) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                error = "ADDRESS_LOOKUP_UNAVAILABLE",
                message = "your address cannot be looked up right now, so this order was not placed. "
                        + "Nothing has been charged or reserved — please try again shortly.",
            });
        } catch (AddressUnknownException) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                error = "IDENTITY_UNAVAILABLE",
                message = "the merchant's store address cannot be looked up right now, so this order "
                        + "was not placed. Nothing has been charged or reserved — please try again shortly.",
            });
        } catch (ShippingNotServiceableException ex) {
            return UnprocessableEntity(new {
                error = "SHIPPING_NOT_SERVICEABLE",
                reasons = ex.Reasons,
                message = "no courier service can carry this parcel to this address, so this order was "
                        + "not placed. Nothing has been charged or reserved.",
            });
        }
    }

    private ObjectResult CheckoutNotPlaced(CheckoutFailedException failure) {
        var code = failure.RolledBack ? "CHECKOUT_ROLLED_BACK" : "CHECKOUT_ROLLBACK_PENDING";
        var held = failure.RolledBack
            ? "Everything it had reserved or charged has been given back."
            : "Some of what it had reserved or charged is still being given back; that is retried "
              + "automatically, and staff are alerted if it cannot be.";

        if (failure.Replayed) {
            return Conflict(new {
                error = code,
                orderNumber = failure.OrderNumber,
                rolledBack = failure.RolledBack,
                replayed = true,
                reason = failure.Reason,
                message = $"the checkout this Idempotency-Key started was not placed. {held} A new attempt "
                        + "needs a new Idempotency-Key.",
            });
        }

        if (failure.DependencyUnavailable) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                error = "CHECKOUT_DEPENDENCY_UNAVAILABLE",
                orderNumber = failure.OrderNumber,
                rolledBack = failure.RolledBack,
                reason = failure.Reason,
                message = $"a service this checkout depends on did not answer, so it was not placed. {held} "
                        + "Try again shortly with a new Idempotency-Key.",
            });
        }

        return Conflict(new {
            error = code,
            orderNumber = failure.OrderNumber,
            rolledBack = failure.RolledBack,
            reason = failure.Reason,
            message = $"this checkout was refused, so it was not placed. {held}",
        });
    }

    private static string PlacementReason(AddressPlacementFailure failure) => failure switch {
        AddressPlacementFailure.NotFound => "NOT_FOUND",
        AddressPlacementFailure.Ambiguous => "AMBIGUOUS",
        AddressPlacementFailure.OutOfServiceArea => "OUT_OF_SERVICE_AREA",
        AddressPlacementFailure.Insufficient => "INSUFFICIENT_ADDRESS",
        AddressPlacementFailure.TooVague => "TOO_VAGUE",
        _ => throw new ArgumentOutOfRangeException(
            nameof(failure),
            failure,
            "no reason code for this placement failure"
        )
    };

    private static string PlacementMessage(AddressPlacementFailure failure) => failure switch {
        AddressPlacementFailure.NotFound => "Your shipping address could not be found on the map.",
        AddressPlacementFailure.Ambiguous =>
            "Your shipping address matches more than one place; add the area or postal code.",
        AddressPlacementFailure.OutOfServiceArea => "Your shipping address is outside the area we deliver to.",
        AddressPlacementFailure.Insufficient => "Your shipping address is incomplete; give the street, area and city.",
        AddressPlacementFailure.TooVague => "Your shipping address is too general to deliver to; give the street.",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "no message for this placement failure")
    };

    [HttpGet("my-orders")]
    public async Task<IActionResult> GetMyOrders(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10
    ) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }

        if (page < 1) {
            return BadRequest(new { error = "PAGE_OUT_OF_RANGE", message = "page counts from 1" });
        }

        if (pageSize < 1 || pageSize > MaxPageSize) {
            return BadRequest(new {
                error = "PAGE_SIZE_OUT_OF_RANGE",
                maxPageSize = MaxPageSize,
                message = $"pageSize is from 1 to {MaxPageSize}",
            });
        }

        OrderStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(status)) {
            var name = Enum.GetNames<OrderStatus>()
                .FirstOrDefault(known => string.Equals(known, status, StringComparison.OrdinalIgnoreCase));

            if (name is null) {
                return BadRequest(new {
                    error = "STATUS_UNKNOWN",
                    allowedStatuses = Enum.GetNames<OrderStatus>(),
                    message = $"'{status}' is not an order status",
                });
            }

            parsedStatus = Enum.Parse<OrderStatus>(name);
        }

        var result = await _orderService.GetCustomerOrdersAsync(customerPrincipalId, parsedStatus, page, pageSize);
        return Ok(new {
            items = result.Orders,
            page,
            pageSize,
            totalCount = result.TotalCount,
        });
    }

    [HttpGet("{orderId:guid}")]
    public async Task<IActionResult> GetOrderById(Guid orderId) {
        var order = await _orderService.GetOrderByIdAsync(orderId);
        if (order == null || !MayRead(order)) {
            return NotFound(new { error = "ORDER_NOT_FOUND", message = $"Order '{orderId}' not found" });
        }

        return Ok(order);
    }

    private bool MayRead(OrderResponse order) =>
        (TryGetCallerPrincipal(out var caller) && order.CustomerPrincipalId == caller) || StaffRoles.Includes(User);
}
