namespace Kinetix.OrderService.Security;

public sealed class PeerAllowList {
    private static readonly HashSet<string> SchemaServices = new(StringComparer.Ordinal) {
        "grpc.reflection.v1.ServerReflection",
        "grpc.reflection.v1alpha.ServerReflection",
    };

    private readonly Dictionary<string, HashSet<string>?> _grants;

    private PeerAllowList(Dictionary<string, HashSet<string>?> grants) {
        _grants = grants;
    }

    public int Count => _grants.Count;

    public static PeerAllowList Parse(string raw) {
        var grants = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);

        var entries = raw.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        foreach (var entry in entries) {
            var at = entry.IndexOf('@');
            if (at < 0) {
                grants[entry] = null;
                continue;
            }

            var peer = entry[..at];
            var scope = entry[(at + 1)..].TrimStart('/');

            if (peer.Length == 0 || scope.Length == 0) {
                throw new InvalidOperationException(
                    $"'{entry}' in KINETIX_GRPC_ALLOWED_PEERS names no peer or no scope; write peer, "
                  + "peer@package.Service or peer@package.Service/Method."
                );
            }

            if (grants.TryGetValue(peer, out var scopes)) {
                scopes?.Add(scope);
                continue;
            }

            grants[peer] = new HashSet<string>(StringComparer.Ordinal) { scope };
        }

        return new PeerAllowList(grants);
    }

    public bool Names(string peer) {
        return _grants.ContainsKey(peer);
    }

    public bool Permits(string peer, string method) {
        if (!_grants.TryGetValue(peer, out var scopes)) {
            return false;
        }

        if (scopes is null) {
            return true;
        }

        var called = method.TrimStart('/');
        var slash = called.IndexOf('/');
        var service = slash < 0 ? called : called[..slash];

        return scopes.Contains(called) || scopes.Contains(service) || SchemaServices.Contains(service);
    }
}
