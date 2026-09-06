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

    private bool TryGetCallerPrincipal(out string customerPrincipalId) {
        customerPrincipalId = User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;
        return !string.IsNullOrWhiteSpace(customerPrincipalId);
    }

    [HttpGet]
    public async Task<IActionResult> GetCart() {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        var cart = await _cartService.GetCartAsync(customerPrincipalId);
        return Ok(cart);
    }

    [HttpPost("items")]
    public async Task<IActionResult> AddItem([FromBody] AddCartItemRequest request) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        var cart = await _cartService.AddItemAsync(customerPrincipalId, request);
        return Ok(cart);
    }

    [HttpPut("items/{productId}")]
    public async Task<IActionResult> UpdateItemQuantity(string productId, [FromBody] UpdateCartItemRequest request) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        var cart = await _cartService.UpdateItemQuantityAsync(customerPrincipalId, productId, request.Quantity);
        return Ok(cart);
    }

    [HttpDelete("items/{productId}")]
    public async Task<IActionResult> RemoveItem(string productId) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        var cart = await _cartService.RemoveItemAsync(customerPrincipalId, productId);
        return Ok(cart);
    }

    [HttpPost("voucher")]
    public async Task<IActionResult> ApplyVoucher([FromBody] ApplyCartVoucherRequest request) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        var cart = await _cartService.ApplyVoucherAsync(customerPrincipalId, request.VoucherCode);
        return Ok(cart);
    }

    [HttpDelete]
    public async Task<IActionResult> ClearCart() {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }
        await _cartService.ClearCartAsync(customerPrincipalId);
        return NoContent();
    }
}
