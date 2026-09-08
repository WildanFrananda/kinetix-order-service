using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs.Requests;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/orders")]
public class OrderController(IOrderService orderService) : ControllerBase {
    private readonly IOrderService _orderService = orderService;

    private bool TryGetCallerPrincipal(out string customerPrincipalId) {
        customerPrincipalId = User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;
        return !string.IsNullOrWhiteSpace(customerPrincipalId);
    }


    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest request) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }

        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();

        try {
            var order = await _orderService.CheckoutAsync(customerPrincipalId, request, idempotencyKey);
            return CreatedAtAction(nameof(GetOrderById), new { orderId = order.Id }, order);
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
        } catch (ShippingNotServiceableException ex) {
            return UnprocessableEntity(new {
                error = "SHIPPING_NO_SERVICE",
                message = "no courier tier can carry this order, so it was not placed. Nothing has "
                        + "been charged or reserved.",
                rateCardReasons = ex.Reasons,
                basis = ShippingRateCardProbe.AvailabilityBasis,
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
                basis = ShippingRateCardProbe.AvailabilityBasis,
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
        } catch (CheckoutFailedException ex) {
            return Conflict(new {
                error = "CHECKOUT_ROLLED_BACK",
                orderNumber = ex.OrderNumber,
                message = ex.Reason,
            });
        } catch (InvalidOperationException ex) {
            return BadRequest(new { error = "CHECKOUT_FAILED", message = ex.Message });
        }
    }

    [HttpGet("my-orders")]
    public async Task<IActionResult> GetMyOrders([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 10) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }

        OrderStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<OrderStatus>(status, true, out var resultStatus)) {
            parsedStatus = resultStatus;
        }

        var result = await _orderService.GetCustomerOrdersAsync(customerPrincipalId, parsedStatus, page, pageSize);
        return Ok(result.Orders);
    }

    [HttpGet("{orderId:guid}")]
    public async Task<IActionResult> GetOrderById(Guid orderId) {
        var order = await _orderService.GetOrderByIdAsync(orderId);
        if (order == null) {
            return NotFound(new { error = "ORDER_NOT_FOUND", message = $"Order '{orderId}' not found" });
        }
        return Ok(order);
    }

    [HttpPut("{orderId:guid}/status")]
    public async Task<IActionResult> TransitionStatus(Guid orderId, [FromBody] UpdateOrderStatusRequest request) {
        if (!Enum.TryParse<OrderStatus>(request.Status, true, out var newStatus)) {
            return BadRequest(new { error = "INVALID_STATUS", message = $"Status '{request.Status}' is not valid" });
        }

        try {
            var order = await _orderService.TransitionOrderStatusAsync(orderId, newStatus);
            return Ok(order);
        } catch (KeyNotFoundException ex) {
            return NotFound(new { error = "ORDER_NOT_FOUND", message = ex.Message });
        } catch (InvalidOperationException ex) {
            return BadRequest(new { error = "INVALID_TRANSITION", message = ex.Message });
        }
    }
}
