namespace Kinetix.OrderService.Application.Products;

public record CatalogProduct(
    string ProductId,
    string MerchantPrincipalId,
    string Title,
    decimal UnitPrice,
    string? CategoryId
);
