using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.DTOs.Requests;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Application.Checkout;

public record CheckoutRecipient(string ShippingAddress, string RecipientName, string RecipientPhone) {
    public static CheckoutRecipient From(CheckoutRequest request) {
        var address = Required(request.ShippingAddress, "shippingAddress");
        var name = Required(request.RecipientName, "recipientName");
        var phone = Required(request.RecipientPhone, "recipientPhone");

        RequireAtMost(address, "shippingAddress", OrderEntity.ShippingAddressMaxLength);
        RequireAtMost(name, "recipientName", OrderEntity.RecipientNameMaxLength);
        RequireAtMost(phone, "recipientPhone", OrderEntity.RecipientPhoneMaxLength);
        RequireAtMost(request.VoucherCode, "voucherCode", OrderEntity.AppliedVoucherMaxLength);

        return new CheckoutRecipient(address, name, phone);
    }

    private static string Required(string? value, string field) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new RecipientMissingException(field);
        }

        return value;
    }

    private static void RequireAtMost(string? value, string field, int maxLength) {
        if (value is not null && value.Length > maxLength) {
            throw new FieldTooLongException(field, maxLength);
        }
    }
}
