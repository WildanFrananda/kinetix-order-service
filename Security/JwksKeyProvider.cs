using System.Text.Json;
using Kinetix.OrderService.Infrastructure.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Kinetix.OrderService.Security;

public sealed class JwksKeyProvider(
    IConfiguration config,
    IHttpClientFactory factory,
    TimeProvider clock,
    ILogger<JwksKeyProvider> log
) {
    public static readonly TimeSpan UnknownKeyRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly string _url = RequiredSetting.Text(config, "IDENTITY_JWKS_URL");
    private readonly HttpClient _http = factory.CreateClient();
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<JwksKeyProvider> _log = log;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private Dictionary<string, SecurityKey> _keys = [];
    private DateTimeOffset? _lastRefresh;
    private bool _lastRefreshFailed;

    public async Task<int> RefreshAsync(CancellationToken ct = default) {
        lock (_gate) {
            _lastRefresh = _clock.GetUtcNow();
        }

        Dictionary<string, SecurityKey> fresh;
        try {
            fresh = await FetchAsync(ct);
        } catch (Exception) {
            lock (_gate) {
                _lastRefreshFailed = true;
            }

            throw;
        }

        lock (_gate) {
            _keys = fresh;
            _lastRefreshFailed = false;
        }

        return fresh.Count;
    }

    public bool CannotVouchFor(string? kid) {
        if (kid is null) {
            return false;
        }

        lock (_gate) {
            return _lastRefreshFailed && !_keys.ContainsKey(kid);
        }
    }

    public IEnumerable<SecurityKey> Resolve(
        string _token,
        SecurityToken _security,
        string kid,
        TokenValidationParameters _p
    ) {
        if (kid is null) {
            return [];
        }

        lock (_gate) {
            if (_keys.TryGetValue(kid, out var known)) {
                return [known];
            }
        }

        RefreshForUnknownKey(kid);

        lock (_gate) {
            return _keys.TryGetValue(kid, out var found) ? new[] { found } : Array.Empty<SecurityKey>();
        }
    }

    private void RefreshForUnknownKey(string kid) {
        _refreshing.Wait();

        try {
            lock (_gate) {
                if (_keys.ContainsKey(kid)) {
                    return;
                }

                if (_lastRefresh is { } last && _clock.GetUtcNow() - last < UnknownKeyRefreshInterval) {
                    return;
                }
            }

            RefreshAsync().GetAwaiter().GetResult();
        } catch (Exception e) {
            _log.LogWarning(e, "could not refresh identity's JWKS while resolving key {Kid}", kid);
        } finally {
            _refreshing.Release();
        }
    }

    private async Task<Dictionary<string, SecurityKey>> FetchAsync(CancellationToken ct) {
        var body = await _http.GetStringAsync(_url, ct);
        using var doc = JsonDocument.Parse(body);

        var fresh = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
        foreach (var jwk in doc.RootElement.GetProperty("keys").EnumerateArray()) {
            if (jwk.TryGetProperty("alg", out var alg) && alg.GetString() != "RS256") {
                continue;
            }

            var kid = jwk.GetProperty("kid").GetString();
            if (kid is null) {
                continue;
            }

            var key = new JsonWebKey {
                Kty = "RSA",
                Kid = kid,
                N = jwk.GetProperty("n").GetString(),
                E = jwk.GetProperty("e").GetString(),
                Alg = "RS256",
            };
            fresh[kid] = key;
        }

        if (fresh.Count == 0) {
            throw new InvalidOperationException($"{_url} published no usable RS256 keys.");
        }

        return fresh;
    }
}
