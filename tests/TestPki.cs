using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kinetix.OrderService.Security;

namespace Kinetix.OrderService.Tests;

public sealed class TestPki : IDisposable {
    private readonly ECDsa _authorityKey;
    private readonly X509Certificate2 _authority;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kinetix-test-pki-{Guid.NewGuid():N}");

    public TestPki(string authorityName = "Kinetix Test CA") {
        _authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={authorityName}", _authorityKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true)
        );
        _authority = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Directory.CreateDirectory(_root);
    }

    public X509Certificate2 Leaf(string service) =>
        Leaf(service, $"spiffe://kinetix.local/service/{service}");

    public X509Certificate2 Leaf(string service, string? spiffeId) {
        var (certificate, key) = Issue(service, spiffeId);
        using (key) {
            using var withKey = certificate.CopyWithPrivateKey(key);
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        }
    }

    public ServiceIdentity IdentityFor(string service) {
        var directory = Path.Combine(_root, service);
        Directory.CreateDirectory(directory);

        var (certificate, key) = Issue(service, $"spiffe://kinetix.local/service/{service}");
        using (key) {
            File.WriteAllText(Path.Combine(directory, "tls.crt"), certificate.ExportCertificatePem());
            File.WriteAllText(Path.Combine(directory, "tls.key"), key.ExportPkcs8PrivateKeyPem());
        }
        File.WriteAllText(Path.Combine(directory, "ca.pem"), _authority.ExportCertificatePem());

        return ServiceIdentity.Load(directory);
    }

    private (X509Certificate2 Certificate, ECDsa Key) Issue(string service, string? spiffeId) {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={service}", key, HashAlgorithmName.SHA256);

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName($"kinetix-{service}-service");
        if (spiffeId is not null) {
            names.AddUri(new Uri(spiffeId));
        }
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")],
            false
        ));

        var serial = RandomNumberGenerator.GetBytes(16);
        var certificate = request.Create(
            _authority,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddHours(12),
            serial
        );
        return (certificate, key);
    }

    public void Dispose() {
        _authority.Dispose();
        _authorityKey.Dispose();
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
