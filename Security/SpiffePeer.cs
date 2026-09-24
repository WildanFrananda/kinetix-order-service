using System.Security.Cryptography.X509Certificates;

namespace Kinetix.OrderService.Security;

public static class SpiffePeer {
    private const string DefaultTrustDomain = "kinetix.local";

    private const string SubjectAltNameOid = "2.5.29.17";

    public static IReadOnlyList<string> TrustDomains { get; } = ReadTrustDomains();

    public static string TrustDomain => TrustDomains[0];

    private static IReadOnlyList<string> ReadTrustDomains() {
        var configured = (Environment.GetEnvironmentVariable("KINETIX_TRUST_DOMAIN") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return configured.Length == 0 ? [DefaultTrustDomain] : configured;
    }

    public static string? IdOf(X509Certificate2 certificate) {
        foreach (var ext in certificate.Extensions) {
            if (ext.Oid?.Value != SubjectAltNameOid) {
                continue;
            }

            var text = ext.Format(true);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
                var trimmed = line.Trim().TrimEnd(',');
                var marker = trimmed.IndexOf("spiffe://", StringComparison.Ordinal);
                if (marker >= 0) {
                    return trimmed[marker..].Trim();
                }
            }
        }
        return null;
    }

    public static string? ServiceOf(X509Certificate2 certificate) {
        string? id = IdOf(certificate);

        foreach (var domain in TrustDomains) {
            string? named = ServiceOf(id, domain);
            if (named is not null) {
                return named;
            }
        }

        return null;
    }

    public static string? ServiceOf(string? id, string domain) {
        var prefix = $"spiffe://{domain}/service/";
        return id is not null && id.StartsWith(prefix, StringComparison.Ordinal)
            ? id[prefix.Length..]
            : null;
    }
}
