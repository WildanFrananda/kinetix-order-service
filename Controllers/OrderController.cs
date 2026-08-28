using Microsoft.AspNetCore.Mvc;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.DTOs;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Route("api/v1/orders")]
public class OrderController(IOrderService orderService) : ControllerBase {
    private readonly IOrderService _orderService = orderService;

    private bool TryGetCustomerId(out long customerId) {
        var customerIdHeader = Request.Headers["X-User-Id"].FirstOrDefault();
        if (long.TryParse(customerIdHeader, out customerId) && customerId > 0) {
            return true;
        }
        customerId = 0;
        return false;
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest request) {
        if (!TryGetCustomerId(out var customerId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "X-User-Id header is required" });
        }

        var idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();

        try {
            var order = await _orderService.CheckoutAsync(customerId, request, idempotencyKey);
            return CreatedAtAction(nameof(GetOrderById), new { orderId = order.Id }, order);
        } catch (InvalidOperationException ex) {
            return BadRequest(new { error = "CHECKOUT_FAILED", message = ex.Message });
        }
    }

    [HttpGet("my-orders")]
    public async Task<IActionResult> GetMyOrders([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 10) {
        if (!TryGetCustomerId(out var customerId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "X-User-Id header is required" });
        }

        OrderStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<OrderStatus>(status, true, out var resultStatus)) {
            parsedStatus = resultStatus;
        }

        var orders = await _orderService.GetCustomerOrdersAsync(customerId, parsedStatus, page, pageSize);
        return Ok(orders);
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
