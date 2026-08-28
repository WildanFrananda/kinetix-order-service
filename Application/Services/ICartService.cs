using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.DTOs;

namespace Kinetix.OrderService.Application.Services;

public interface ICartService {
    Task<CustomerCart> GetCartAsync(long customerId);
    Task<CustomerCart> AddItemAsync(long customerId, AddCartItemRequest request);
    Task<CustomerCart> UpdateItemQuantityAsync(long customerId, string productId, int quantity);
    Task<CustomerCart> RemoveItemAsync(long customerId, string productId);
    Task<CustomerCart> ApplyVoucherAsync(long customerId, string voucherCode);
    Task ClearCartAsync(long customerId);
}
