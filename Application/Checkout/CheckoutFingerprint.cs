using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kinetix.OrderService.DTOs.Requests;

namespace Kinetix.OrderService.Application.Checkout;

public static class CheckoutFingerprint {
    public static string Of(CheckoutRequest request) {
        var canonical = JsonSerializer.Serialize(new[] {
            request.ShippingAddress,
            request.VoucherCode,
            request.ShippingServiceTier,
            request.RecipientName,
            request.RecipientPhone,
        });

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
