using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.DTOs;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/cart")]
public class CartController(ICartService cartService) : ControllerBase {
    private readonly ICartService _cartService = cartService;

    private bool TryGetCustomerId(out long customerId) {
        var claim = User.FindFirst("uid")?.Value;
        if (long.TryParse(claim, out customerId) && customerId > 0) {
            return true;
        }
        customerId = 0;
        return false;
    }

    [HttpGet]
    public async Task<IActionResult> GetCart() {
        if (!TryGetCustomerId(out var customerId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        var cart = await _cartService.GetCartAsync(customerId);
        return Ok(cart);
    }

    [HttpPost("items")]
    public async Task<IActionResult> AddItem([FromBody] AddCartItemRequest request) {
        if (!TryGetCustomerId(out var customerId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        var cart = await _cartService.AddItemAsync(customerId, request);
        return Ok(cart);
    }

    [HttpPut("items/{productId}")]
    public async Task<IActionResult> UpdateItemQuantity(string productId, [FromBody] UpdateCartItemRequest request) {
        if (!TryGetCustomerId(out var customerId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        var cart = await _cartService.UpdateItemQuantityAsync(customerId, productId, request.Quantity);
        return Ok(cart);
    }

    [HttpDelete("items/{productId}")]
    public async Task<IActionResult> RemoveItem(string productId) {
        if (!TryGetCustomerId(out var customerId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        var cart = await _cartService.RemoveItemAsync(customerId, productId);
        return Ok(cart);
    }

    [HttpPost("voucher")]
    public async Task<IActionResult> ApplyVoucher([FromBody] ApplyCartVoucherRequest request) {
        if (!TryGetCustomerId(out var customerId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        var cart = await _cartService.ApplyVoucherAsync(customerId, request.VoucherCode);
        return Ok(cart);
    }

    [HttpDelete]
    public async Task<IActionResult> ClearCart() {
        if (!TryGetCustomerId(out var customerId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        await _cartService.ClearCartAsync(customerId);
        return NoContent();
    }
}
