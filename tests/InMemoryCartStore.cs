using System.Globalization;
using System.Text.Json;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Domain.Entities;

namespace Kinetix.OrderService.Tests;

public sealed class InMemoryCartStore : ICartStore {
    private readonly Dictionary<string, (string Json, long Version)> _carts = new(StringComparer.Ordinal);

    public int Writes { get; private set; }

    public int ConflictsToInject { get; set; }

    public void Put(CustomerCart cart) {
        var version = _carts.TryGetValue(cart.CustomerPrincipalId, out var held) ? held.Version + 1 : 1;
        _carts[cart.CustomerPrincipalId] = (JsonSerializer.Serialize(cart), version);
    }

    public Task<StoredCart> ReadAsync(string customerPrincipalId) {
        if (!_carts.TryGetValue(customerPrincipalId, out var held)) {
            return Task.FromResult(new StoredCart(new CustomerCart(customerPrincipalId), string.Empty));
        }

        var cart = JsonSerializer.Deserialize<CustomerCart>(held.Json)!;
        return Task.FromResult(new StoredCart(cart, held.Version.ToString(CultureInfo.InvariantCulture)));
    }

    public Task<bool> TryReplaceAsync(
        string customerPrincipalId,
        CustomerCart cart,
        string expectedVersion,
        TimeSpan timeToLive
    ) {
        if (ConflictsToInject > 0) {
            ConflictsToInject -= 1;
            Put(ReadAsync(customerPrincipalId).Result.Cart);
            return Task.FromResult(false);
        }

        var current = _carts.TryGetValue(customerPrincipalId, out var held)
            ? held.Version.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

        if (current != expectedVersion) {
            return Task.FromResult(false);
        }

        Writes += 1;
        Put(cart);
        return Task.FromResult(true);
    }

    public Task DeleteAsync(string customerPrincipalId) {
        _carts.Remove(customerPrincipalId);
        return Task.CompletedTask;
    }
}
