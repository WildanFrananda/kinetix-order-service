using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.DTOs;

namespace Kinetix.OrderService.Application.Services;

public class CartService(IDistributedCache cache) : ICartService {
    private readonly IDistributedCache _cache = cache;
    private readonly TimeSpan _cartTtl = TimeSpan.FromDays(14);

    private static string GetCartKey(string customerPrincipalId) => $"cart:principal:{customerPrincipalId}";

    public async Task<CustomerCart> GetCartAsync(string customerPrincipalId) {
        var key = GetCartKey(customerPrincipalId);
        var cachedJson = await _cache.GetStringAsync(key);

        if (string.IsNullOrEmpty(cachedJson)) {
            return new CustomerCart(customerPrincipalId);
        }

        var cart = JsonSerializer.Deserialize<CustomerCart>(cachedJson);
        return cart ?? new CustomerCart(customerPrincipalId);
    }

    public async Task<CustomerCart> AddItemAsync(string customerPrincipalId, AddCartItemRequest request) {
        var cart = await GetCartAsync(customerPrincipalId);
        var existingItem = cart.Items.FirstOrDefault(i => i.ProductId == request.ProductId);

        if (existingItem != null) {
            existingItem.Quantity += request.Quantity;
        } else {
            cart.Items.Add(new CartItem {
                ProductId = request.ProductId,
                ProductTitle = request.ProductTitle,
                UnitPrice = request.UnitPrice,
                Quantity = request.Quantity,
                CategoryId = request.CategoryId
            });
        }

        cart.UpdatedAt = DateTime.UtcNow;
        await SaveCartAsync(cart);
        return cart;
    }

    public async Task<CustomerCart> UpdateItemQuantityAsync(string customerPrincipalId, string productId, int quantity) {
        var cart = await GetCartAsync(customerPrincipalId);
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId);

        if (item != null) {
            if (quantity <= 0) {
                cart.Items.Remove(item);
            } else {
                item.Quantity = quantity;
            }

            cart.UpdatedAt = DateTime.UtcNow;
            await SaveCartAsync(cart);
        }

        return cart;
    }

    public async Task<CustomerCart> RemoveItemAsync(string customerPrincipalId, string productId) {
        var cart = await GetCartAsync(customerPrincipalId);
        cart.Items.RemoveAll(i => i.ProductId == productId);
        cart.UpdatedAt = DateTime.UtcNow;
        await SaveCartAsync(cart);
        return cart;
    }

    public async Task<CustomerCart> ApplyVoucherAsync(string customerPrincipalId, string voucherCode) {
        var cart = await GetCartAsync(customerPrincipalId);
        cart.AppliedVoucherCode = voucherCode;
        cart.UpdatedAt = DateTime.UtcNow;
        await SaveCartAsync(cart);
        return cart;
    }

    public async Task ClearCartAsync(string customerPrincipalId) {
        var key = GetCartKey(customerPrincipalId);
        await _cache.RemoveAsync(key);
    }

    private async Task SaveCartAsync(CustomerCart cart) {
        var key = GetCartKey(cart.CustomerPrincipalId);
        var json = JsonSerializer.Serialize(cart);
        var options = new DistributedCacheEntryOptions {
            AbsoluteExpirationRelativeToNow = _cartTtl
        };
        await _cache.SetStringAsync(key, json, options);
    }
}
