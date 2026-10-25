using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.DTOs.Requests;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Application.Services;

public class CartService(ICartStore store, IProductDirectory products) : ICartService {
    public const int MaxEditAttempts = 10;

    private static readonly TimeSpan CartTtl = TimeSpan.FromDays(14);

    private readonly ICartStore _store = store;
    private readonly IProductDirectory _products = products;

    public async Task<CustomerCart> GetCartAsync(string customerPrincipalId) {
        var stored = await _store.ReadAsync(customerPrincipalId);
        return stored.Cart;
    }

    public async Task<CustomerCart> AddItemAsync(string customerPrincipalId, AddCartItemRequest request) {
        RequireQuantity(request.ProductId, request.Quantity, CartItem.MinQuantity);

        var product = await _products.GetProductAsync(request.ProductId);
        if (string.Equals(product.MerchantPrincipalId, customerPrincipalId, StringComparison.Ordinal)) {
            throw new OwnProductNotPurchasableException(product.ProductId);
        }

        return await EditAsync(customerPrincipalId, cart => {
            var existingItem = cart.Items.FirstOrDefault(i => i.ProductId == product.ProductId);

            if (existingItem != null) {
                long quantity = (long)existingItem.Quantity + request.Quantity;
                if (quantity > CartItem.MaxQuantity) {
                    throw new CartLineQuantityOutOfRangeException(
                        product.ProductId,
                        quantity,
                        CartItem.MinQuantity,
                        CartItem.MaxQuantity
                    );
                }

                existingItem.Quantity = (int)quantity;
                existingItem.ProductTitle = product.Title;
                existingItem.UnitPrice = product.UnitPrice;
                existingItem.CategoryId = product.CategoryId;
                existingItem.MerchantPrincipalId = product.MerchantPrincipalId;
            } else {
                cart.Items.Add(new CartItem {
                    ProductId = product.ProductId,
                    ProductTitle = product.Title,
                    UnitPrice = product.UnitPrice,
                    Quantity = request.Quantity,
                    CategoryId = product.CategoryId,
                    MerchantPrincipalId = product.MerchantPrincipalId
                });
            }

            return true;
        });
    }

    public async Task<CustomerCart> UpdateItemQuantityAsync(
        string customerPrincipalId,
        string productId,
        int quantity
    ) {
        RequireQuantity(productId, quantity, 0);

        return await EditAsync(customerPrincipalId, cart => {
            var item = cart.Items.FirstOrDefault(i => i.ProductId == productId);
            if (item is null) {
                return false;
            }

            if (quantity == 0) {
                cart.Items.Remove(item);
            } else {
                item.Quantity = quantity;
            }

            return true;
        });
    }

    public Task<CustomerCart> RemoveItemAsync(string customerPrincipalId, string productId) =>
        EditAsync(customerPrincipalId, cart => cart.Items.RemoveAll(i => i.ProductId == productId) > 0);

    public Task<CustomerCart> ApplyVoucherAsync(string customerPrincipalId, string voucherCode) {
        if (voucherCode.Length > OrderEntity.AppliedVoucherMaxLength) {
            throw new FieldTooLongException("voucherCode", OrderEntity.AppliedVoucherMaxLength);
        }

        return EditAsync(customerPrincipalId, cart => {
            cart.AppliedVoucherCode = voucherCode;
            return true;
        });
    }

    public Task<CustomerCart> RemoveVoucherAsync(string customerPrincipalId) =>
        EditAsync(customerPrincipalId, cart => {
            if (cart.AppliedVoucherCode is null) {
                return false;
            }

            cart.AppliedVoucherCode = null;
            return true;
        });

    public async Task ClearCartAsync(string customerPrincipalId) {
        await _store.DeleteAsync(customerPrincipalId);
    }

    public async Task RemoveCheckedOutAsync(string customerPrincipalId, CustomerCart checkedOut) {
        for (var attempt = 1; attempt <= MaxEditAttempts; attempt++) {
            var stored = await _store.ReadAsync(customerPrincipalId);
            var remaining = Without(stored.Cart, checkedOut);

            var written = remaining.Items.Count == 0 && remaining.AppliedVoucherCode is null
                ? await _store.TryDeleteAsync(customerPrincipalId, stored.Version)
                : await _store.TryReplaceAsync(customerPrincipalId, remaining, stored.Version, CartTtl);

            if (written) {
                return;
            }
        }

        throw new CartBusyException(MaxEditAttempts);
    }

    private static CustomerCart Without(CustomerCart current, CustomerCart checkedOut) {
        foreach (var taken in checkedOut.Items) {
            var line = current.Items.FirstOrDefault(i => i.ProductId == taken.ProductId);
            if (line is null) {
                continue;
            }

            if (line.Quantity <= taken.Quantity) {
                current.Items.Remove(line);
            } else {
                line.Quantity -= taken.Quantity;
            }
        }

        if (string.Equals(current.AppliedVoucherCode, checkedOut.AppliedVoucherCode, StringComparison.Ordinal)) {
            current.AppliedVoucherCode = null;
        }

        current.UpdatedAt = DateTime.UtcNow;
        return current;
    }

    private static void RequireQuantity(string productId, int quantity, int minimum) {
        if (quantity < minimum || quantity > CartItem.MaxQuantity) {
            throw new QuantityOutOfRangeException(productId, quantity, minimum, CartItem.MaxQuantity);
        }
    }

    private async Task<CustomerCart> EditAsync(string customerPrincipalId, Func<CustomerCart, bool> edit) {
        for (var attempt = 1; attempt <= MaxEditAttempts; attempt++) {
            var stored = await _store.ReadAsync(customerPrincipalId);
            if (!edit(stored.Cart)) {
                return stored.Cart;
            }

            stored.Cart.UpdatedAt = DateTime.UtcNow;
            if (await _store.TryReplaceAsync(customerPrincipalId, stored.Cart, stored.Version, CartTtl)) {
                return stored.Cart;
            }
        }

        throw new CartBusyException(MaxEditAttempts);
    }
}
