using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs;

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
