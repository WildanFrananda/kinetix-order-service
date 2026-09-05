using System.Security.Cryptography.X509Certificates;

namespace Kinetix.OrderService.Security;

public static class SpiffePeer {
    public const string TrustDomain = "kinetix.local";

    private const string SubjectAltNameOid = "2.5.29.17";

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
        var id = IdOf(certificate);
        var prefix = $"spiffe://{TrustDomain}/service/";
        return id is not null && id.StartsWith(prefix, StringComparison.Ordinal)
            ? id[prefix.Length..]
            : null;
    }
}
