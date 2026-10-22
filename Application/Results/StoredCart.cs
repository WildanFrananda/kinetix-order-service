using Kinetix.OrderService.Domain.Entities;

namespace Kinetix.OrderService.Application.Results;

public record StoredCart(CustomerCart Cart, string Version);
