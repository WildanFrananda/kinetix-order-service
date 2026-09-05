using System.Security.Cryptography.X509Certificates;

namespace Kinetix.OrderService.Security;

public sealed class ServiceIdentity {
    public X509Certificate2 Leaf { get; }
    public X509Certificate2Collection TrustedRoots { get; }

    private ServiceIdentity(X509Certificate2 leaf, X509Certificate2Collection roots) {
        Leaf = leaf;
        TrustedRoots = roots;
    }

    public static ServiceIdentity Load(string? directory = null) {
        var dir = directory
            ?? Environment.GetEnvironmentVariable("KINETIX_PKI_DIR")
            ?? "/pki";

        var certPath = Path.Combine(dir, "tls.crt");
        var keyPath = Path.Combine(dir, "tls.key");
        var caPath = Path.Combine(dir, "ca.pem");

        foreach (var (name, path) in new[] { ("tls.crt", certPath), ("tls.key", keyPath), ("ca.pem", caPath) }) {
            if (!File.Exists(path)) {
                throw new InvalidOperationException(
                    $"{name} is missing from {dir}. The service PKI is mounted there; issue it with " +
                    "kinetix-infrastructure/bin/kinetix-pki issue.");
            }
        }

        var leaf = X509Certificate2.CreateFromPemFile(certPath, keyPath);

        leaf = X509CertificateLoader.LoadPkcs12(leaf.Export(X509ContentType.Pkcs12), null);

        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(caPath);
        if (roots.Count == 0) {
            throw new InvalidOperationException($"{caPath} contains no certificates");
        }

        return new ServiceIdentity(leaf, roots);
    }

    public bool IsIssuedByOurCa(X509Certificate2 peer) {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(TrustedRoots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(peer);
    }
}
