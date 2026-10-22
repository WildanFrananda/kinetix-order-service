using Grpc.Core;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Infrastructure.Grpc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using CatalogProto = global::Catalog.V1;
using CommonProto = global::Common.V1;

namespace Kinetix.OrderService.Tests;

public class CatalogProductDirectoryTests {
    private static CatalogProductDirectory DirectoryFaulting(StatusCode code) {
        var catalog = new Mock<CatalogProto.CatalogService.CatalogServiceClient>();
        catalog.Setup(c => c.GetProductAsync(
            It.IsAny<CatalogProto.GetProductRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()
        )).Throws(new RpcException(new Status(code, "catalog's answer")));

        return new CatalogProductDirectory(catalog.Object, NullLogger<CatalogProductDirectory>.Instance);
    }

    [Theory]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task ARefusalCatalogGaveMeansThereIsNoSuchProduct(StatusCode code) {
        await Assert.ThrowsAsync<ProductNotInCatalogException>(() =>
            DirectoryFaulting(code).GetProductAsync("TSHIRT-BLK-M")
        );
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Internal)]
    public async Task AnAnswerCatalogCouldNotGiveIsAnOutage(StatusCode code) {
        await Assert.ThrowsAsync<CatalogUnavailableException>(() =>
            DirectoryFaulting(code).GetProductAsync("TSHIRT-BLK-M")
        );
    }

    [Fact]
    public async Task AProductCatalogNamesNoMerchantForIsRefusedUnderItsOwnName() {
        var catalog = new Mock<CatalogProto.CatalogService.CatalogServiceClient>();
        catalog.Setup(c => c.GetProductAsync(
            It.IsAny<CatalogProto.GetProductRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()
        )).Returns(new AsyncUnaryCall<CatalogProto.GetProductResponse>(
            Task.FromResult(new CatalogProto.GetProductResponse {
                Found = true,
                Product = new CatalogProto.Product {
                    Sku = "TSHIRT-BLK-M",
                    Title = "Kinetix Premium Shirt",
                    Price = new CommonProto.Money { AmountMinor = 15000000, Currency = "IDR" },
                },
            }),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { }
        ));

        var directory = new CatalogProductDirectory(catalog.Object, NullLogger<CatalogProductDirectory>.Instance);

        var refusal = await Assert.ThrowsAsync<ProductHasNoMerchantException>(() =>
            directory.GetProductAsync("TSHIRT-BLK-M")
        );

        Assert.Equal("TSHIRT-BLK-M", refusal.ProductId);
    }

    [Fact]
    public async Task AFaultBeforeCatalogIsReachedIsAnOutageToo() {
        var catalog = new Mock<CatalogProto.CatalogService.CatalogServiceClient>();
        catalog.Setup(c => c.GetProductAsync(
            It.IsAny<CatalogProto.GetProductRequest>(),
            It.IsAny<Metadata>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()
        )).Throws(new InvalidOperationException("the mesh handler has no client certificate"));
        var directory = new CatalogProductDirectory(catalog.Object, NullLogger<CatalogProductDirectory>.Instance);

        var outage = await Assert.ThrowsAsync<CatalogUnavailableException>(() =>
            directory.GetProductAsync("TSHIRT-BLK-M")
        );

        Assert.IsType<InvalidOperationException>(outage.InnerException);
    }
}
