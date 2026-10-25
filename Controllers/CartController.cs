using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.DTOs.Requests;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Security;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/cart")]
public class CartController(ICartService cartService) : ControllerBase {
    private readonly ICartService _cartService = cartService;

    private bool TryGetCallerPrincipal(out string customerPrincipalId) {
        customerPrincipalId = CallerPrincipal.Of(User) ?? string.Empty;
        return customerPrincipalId.Length > 0;
    }

    [HttpGet]
    public async Task<IActionResult> GetCart() {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }

        return await Answer(() => _cartService.GetCartAsync(customerPrincipalId));
    }

    [HttpPost("items")]
    public async Task<IActionResult> AddItem([FromBody] AddCartItemRequest request) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }

        if (string.IsNullOrWhiteSpace(request.ProductId)) {
            return BadRequest(new {
                error = "PRODUCT_ID_REQUIRED",
                field = "productId",
                message = "name the product to add to the cart",
            });
        }

        return await Answer(() => _cartService.AddItemAsync(customerPrincipalId, request));
    }

    [HttpPut("items/{productId}")]
    public async Task<IActionResult> UpdateItemQuantity(string productId, [FromBody] UpdateCartItemRequest request) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }

        return await Answer(() =>
            _cartService.UpdateItemQuantityAsync(customerPrincipalId, productId, request.Quantity)
        );
    }

    [HttpDelete("items/{productId}")]
    public async Task<IActionResult> RemoveItem(string productId) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }

        return await Answer(() => _cartService.RemoveItemAsync(customerPrincipalId, productId));
    }

    [HttpPost("voucher")]
    public async Task<IActionResult> ApplyVoucher([FromBody] ApplyCartVoucherRequest request) {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }

        if (string.IsNullOrWhiteSpace(request.VoucherCode)) {
            return BadRequest(new {
                error = "VOUCHER_CODE_REQUIRED",
                message = "name the voucher to apply; DELETE /api/v1/cart/voucher takes one off",
            });
        }

        string voucherCode = request.VoucherCode;
        return await Answer(() => _cartService.ApplyVoucherAsync(customerPrincipalId, voucherCode));
    }

    [HttpDelete("voucher")]
    public async Task<IActionResult> RemoveVoucher() {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }

        return await Answer(() => _cartService.RemoveVoucherAsync(customerPrincipalId));
    }

    [HttpDelete]
    public async Task<IActionResult> ClearCart() {
        if (!TryGetCallerPrincipal(out var customerPrincipalId)) {
            return Unauthorized(new { error = "UNAUTHORIZED", message = "a verified access token is required" });
        }

        try {
            await _cartService.ClearCartAsync(customerPrincipalId);
        } catch (CartUnavailableException) {
            return CartUnavailable();
        }

        return NoContent();
    }

    private async Task<IActionResult> Answer(Func<Task<CustomerCart>> edit) {
        try {
            return Ok(await edit());
        } catch (ProductNotInCatalogException ex) {
            return NotFound(new { error = "PRODUCT_NOT_IN_CATALOG", productId = ex.ProductId, message = ex.Message });
        } catch (ProductHasNoUsablePriceException ex) {
            return UnprocessableEntity(new {
                error = "PRODUCT_HAS_NO_USABLE_PRICE",
                productId = ex.ProductId,
                message = ex.Message
            });
        } catch (ProductHasNoMerchantException ex) {
            return UnprocessableEntity(new {
                error = "PRODUCT_HAS_NO_MERCHANT",
                productId = ex.ProductId,
                message = ex.Message
            });
        } catch (OwnProductNotPurchasableException ex) {
            return UnprocessableEntity(new {
                error = "OWN_PRODUCT_NOT_PURCHASABLE",
                productId = ex.ProductId,
                message = "this product is sold by your own account, and a seller cannot buy its own product",
            });
        } catch (QuantityOutOfRangeException ex) {
            return BadRequest(new {
                error = "QUANTITY_OUT_OF_RANGE",
                productId = ex.ProductId,
                quantity = ex.Quantity,
                minQuantity = ex.Minimum,
                maxQuantity = ex.Maximum,
                message = ex.Message,
            });
        } catch (CartLineQuantityOutOfRangeException ex) {
            return UnprocessableEntity(new {
                error = "CART_LINE_QUANTITY_OUT_OF_RANGE",
                productId = ex.ProductId,
                quantity = ex.Quantity,
                minQuantity = ex.Minimum,
                maxQuantity = ex.Maximum,
                message = ex.Message,
            });
        } catch (FieldTooLongException ex) {
            return BadRequest(new {
                error = "FIELD_TOO_LONG",
                field = ex.Field,
                maxLength = ex.MaxLength,
                message = ex.Message,
            });
        } catch (CartBusyException ex) {
            return Conflict(new { error = "CART_BUSY", message = ex.Message + "; try again" });
        } catch (CatalogUnavailableException ex) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                error = "CATALOG_UNAVAILABLE",
                productId = ex.ProductId,
                message = ex.Message
            });
        } catch (CartUnavailableException) {
            return CartUnavailable();
        }
    }

    private ObjectResult CartUnavailable() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new {
            error = "CART_UNAVAILABLE",
            message = "your cart cannot be reached right now; nothing was changed. Try again shortly.",
        });
}
