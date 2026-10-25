using System.Text.Json;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Results;
using Kinetix.OrderService.Domain.Entities;
using StackExchange.Redis;

namespace Kinetix.OrderService.Infrastructure.Redis;

public sealed class RedisCartStore(IConnectionMultiplexer redis) : ICartStore {
    public const string KeyPrefix = "KinetixOrder:cart:principal:";

    private const string DataField = "data";
    private const string VersionField = "ver";

    private const string ReplaceIfUnchanged = """
        local current = redis.call('HGET', KEYS[1], 'ver')
        if (current or '') ~= ARGV[1] then
            return 0
        end
        redis.call('HSET', KEYS[1], 'data', ARGV[2], 'ver', tostring((tonumber(current) or 0) + 1))
        redis.call('PEXPIRE', KEYS[1], ARGV[3])
        return 1
        """;

    private const string DeleteIfUnchanged = """
        local current = redis.call('HGET', KEYS[1], 'ver')
        if (current or '') ~= ARGV[1] then
            return 0
        end
        redis.call('DEL', KEYS[1])
        return 1
        """;

    private readonly IConnectionMultiplexer _redis = redis;

    public async Task<StoredCart> ReadAsync(string customerPrincipalId) {
        RedisValue[] fields;
        try {
            fields = await _redis.GetDatabase().HashGetAsync(KeyOf(customerPrincipalId), [DataField, VersionField]);
        } catch (Exception e) when (e is RedisException or RedisTimeoutException) {
            throw new CartUnavailableException(e);
        }

        var version = fields[1].IsNull ? string.Empty : fields[1].ToString();
        var cart = fields[0].IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<CustomerCart>(fields[0].ToString());

        return new StoredCart(cart ?? new CustomerCart(customerPrincipalId), version);
    }

    public async Task<bool> TryReplaceAsync(
        string customerPrincipalId,
        CustomerCart cart,
        string expectedVersion,
        TimeSpan timeToLive
    ) {
        try {
            var written = await _redis.GetDatabase().ScriptEvaluateAsync(
                ReplaceIfUnchanged,
                [KeyOf(customerPrincipalId)],
                [expectedVersion, JsonSerializer.Serialize(cart), (long)timeToLive.TotalMilliseconds]
            );

            return (long)written == 1;
        } catch (Exception e) when (e is RedisException or RedisTimeoutException) {
            throw new CartUnavailableException(e);
        }
    }

    public async Task<bool> TryDeleteAsync(string customerPrincipalId, string expectedVersion) {
        try {
            var deleted = await _redis.GetDatabase().ScriptEvaluateAsync(
                DeleteIfUnchanged,
                [KeyOf(customerPrincipalId)],
                [expectedVersion]
            );

            return (long)deleted == 1;
        } catch (Exception e) when (e is RedisException or RedisTimeoutException) {
            throw new CartUnavailableException(e);
        }
    }

    public async Task DeleteAsync(string customerPrincipalId) {
        try {
            await _redis.GetDatabase().KeyDeleteAsync(KeyOf(customerPrincipalId));
        } catch (Exception e) when (e is RedisException or RedisTimeoutException) {
            throw new CartUnavailableException(e);
        }
    }

    private static RedisKey KeyOf(string customerPrincipalId) => $"{KeyPrefix}{customerPrincipalId}";
}
