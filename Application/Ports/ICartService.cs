using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.DTOs;

namespace Kinetix.OrderService.Application.Services;

public interface ICartService {
    Task<CustomerCart> GetCartAsync(string customerPrincipalId);
    Task<CustomerCart> AddItemAsync(string customerPrincipalId, AddCartItemRequest request);
    Task<CustomerCart> UpdateItemQuantityAsync(string customerPrincipalId, string productId, int quantity);
    Task<CustomerCart> RemoveItemAsync(string customerPrincipalId, string productId);
    Task<CustomerCart> ApplyVoucherAsync(string customerPrincipalId, string voucherCode);
    Task ClearCartAsync(string customerPrincipalId);
}
