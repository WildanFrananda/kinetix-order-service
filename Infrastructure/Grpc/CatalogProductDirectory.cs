using Catalog.V1;
using Grpc.Core;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Products;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class CatalogProductDirectory(
    CatalogService.CatalogServiceClient client,
    ILogger<CatalogProductDirectory> logger
) : IProductDirectory {
    private const decimal MinorPerMajor = 100m;
    private const string Currency = "IDR";

    private readonly CatalogService.CatalogServiceClient _client = client;
    private readonly ILogger<CatalogProductDirectory> _logger = logger;

    public async Task<CatalogProduct> GetProductAsync(string productId, CancellationToken cancellationToken = default) {
        GetProductResponse response;
        try {
            response = await _client.GetProductAsync(
                new GetProductRequest { Sku = productId }, cancellationToken: cancellationToken
            );
        } catch (RpcException e) {
            _logger.LogError(e, "catalog did not answer GetProduct for {ProductId}", productId);
            throw new CatalogUnavailableException(productId, e);
        }

        if (!response.Found || response.Product is null) {
            throw new ProductNotInCatalogException(productId);
        }

        var product = response.Product;

        if (product.Price is null) {
            throw new ProductHasNoUsablePriceException(productId, "the product carries no price");
        }
        if (!string.Equals(product.Price.Currency, Currency, StringComparison.Ordinal)) {
            throw new ProductHasNoUsablePriceException(
                productId, $"its price is in {product.Price.Currency}, and this service settles in {Currency}"
            );
        }
        if (string.IsNullOrWhiteSpace(product.MerchantPrincipalId)) {
            throw new ProductHasNoUsablePriceException(productId, "catalog names no merchant for it");
        }

        string? categoryId = string.IsNullOrWhiteSpace(product.CategorySlug) ? null : product.CategorySlug;

        return new CatalogProduct(
            ProductId: product.Sku,
            MerchantPrincipalId: product.MerchantPrincipalId,
            Title: product.Title,
            UnitPrice: product.Price.AmountMinor / MinorPerMajor,
            CategoryId: categoryId
        );
    }
}
