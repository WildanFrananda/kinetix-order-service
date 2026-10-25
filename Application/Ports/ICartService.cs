using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.DTOs.Requests;

namespace Kinetix.OrderService.Application.Ports;

public interface ICartService {
    Task<CustomerCart> GetCartAsync(string customerPrincipalId);
    Task<CustomerCart> AddItemAsync(string customerPrincipalId, AddCartItemRequest request);
    Task<CustomerCart> UpdateItemQuantityAsync(string customerPrincipalId, string productId, int quantity);
    Task<CustomerCart> RemoveItemAsync(string customerPrincipalId, string productId);
    Task<CustomerCart> ApplyVoucherAsync(string customerPrincipalId, string voucherCode);
    Task<CustomerCart> RemoveVoucherAsync(string customerPrincipalId);
    Task ClearCartAsync(string customerPrincipalId);
    Task RemoveCheckedOutAsync(string customerPrincipalId, CustomerCart checkedOut);
}
