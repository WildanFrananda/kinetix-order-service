using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Kinetix.OrderService.Security;

public sealed class JwksKeyProvider(IConfiguration config, IHttpClientFactory factory, ILogger<JwksKeyProvider> log) {
    private readonly string _url = config["IDENTITY_JWKS_URL"]
            ?? throw new InvalidOperationException("IDENTITY_JWKS_URL is required and has no default.");
    private readonly HttpClient _http = factory.CreateClient();
    private readonly ILogger<JwksKeyProvider> _log = log;
    private readonly Lock _gate = new();
    private Dictionary<string, SecurityKey> _keys = [];

    public async Task<int> RefreshAsync(CancellationToken ct = default) {
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

        lock (_gate) {
            _keys = fresh;
        }
        return fresh.Count;
    }

    public IEnumerable<SecurityKey> Resolve(string _token, SecurityToken _security, string kid, TokenValidationParameters _p) {
        lock (_gate) {
            if (_keys.TryGetValue(kid, out var known)) {
                return [known];
            }
        }

        try {
            RefreshAsync().GetAwaiter().GetResult();
        } catch (Exception e) {
            _log.LogWarning(e, "could not refresh identity's JWKS while resolving key {Kid}", kid);
        }

        lock (_gate) {
            return _keys.TryGetValue(kid, out var found) ? new[] { found } : Array.Empty<SecurityKey>();
        }
    }
}
