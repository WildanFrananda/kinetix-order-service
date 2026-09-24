namespace Kinetix.OrderService.Application.Ports;

using Kinetix.OrderService.Application.Products;

public interface IProductDirectory {
    Task<CatalogProduct> GetProductAsync(string productId, CancellationToken cancellationToken = default);
}
