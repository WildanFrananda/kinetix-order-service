using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.DTOs;

namespace Kinetix.OrderService.Application.Services;

public class CartService(IDistributedCache cache) : ICartService {
    private readonly IDistributedCache _cache = cache;
    private readonly TimeSpan _cartTtl = TimeSpan.FromDays(14);

    private static string GetCartKey(long customerId) => $"cart:customer:{customerId}";

    public async Task<CustomerCart> GetCartAsync(long customerId) {
        var key = GetCartKey(customerId);
        var cachedJson = await _cache.GetStringAsync(key);

        if (string.IsNullOrEmpty(cachedJson)) {
            return new CustomerCart(customerId);
        }

        var cart = JsonSerializer.Deserialize<CustomerCart>(cachedJson);
        return cart ?? new CustomerCart(customerId);
    }

    public async Task<CustomerCart> AddItemAsync(long customerId, AddCartItemRequest request) {
        var cart = await GetCartAsync(customerId);
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

    public async Task<CustomerCart> UpdateItemQuantityAsync(long customerId, string productId, int quantity) {
        var cart = await GetCartAsync(customerId);
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

    public async Task<CustomerCart> RemoveItemAsync(long customerId, string productId) {
        var cart = await GetCartAsync(customerId);
        cart.Items.RemoveAll(i => i.ProductId == productId);
        cart.UpdatedAt = DateTime.UtcNow;
        await SaveCartAsync(cart);
        return cart;
    }

    public async Task<CustomerCart> ApplyVoucherAsync(long customerId, string voucherCode) {
        var cart = await GetCartAsync(customerId);
        cart.AppliedVoucherCode = voucherCode;
        cart.UpdatedAt = DateTime.UtcNow;
        await SaveCartAsync(cart);
        return cart;
    }

    public async Task ClearCartAsync(long customerId) {
        var key = GetCartKey(customerId);
        await _cache.RemoveAsync(key);
    }

    private async Task SaveCartAsync(CustomerCart cart) {
        var key = GetCartKey(cart.CustomerId);
        var json = JsonSerializer.Serialize(cart);
        var options = new DistributedCacheEntryOptions {
            AbsoluteExpirationRelativeToNow = _cartTtl
        };
        await _cache.SetStringAsync(key, json, options);
    }
}
