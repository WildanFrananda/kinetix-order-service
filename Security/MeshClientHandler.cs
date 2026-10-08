using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Kinetix.OrderService.Security;

public static class MeshClientHandler {
    public static SocketsHttpHandler For(ServiceIdentity identity, string expectedService, ILogger log) =>
        new() {
            SslOptions = new SslClientAuthenticationOptions {
                ClientCertificates = new X509Certificate2Collection(identity.Leaf),
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is X509Certificate2 server && Accepts(identity, server, expectedService, log),
            },
        };

    private static bool Accepts(ServiceIdentity identity, X509Certificate2 server, string expectedService, ILogger log) {
        if (identity.Identifies(server, expectedService)) {
            return true;
        }

        log.LogWarning(
            "refused the server certificate for {ExpectedService}: it names {PresentedId}",
            expectedService,
            SpiffePeer.IdOf(server) ?? "no SPIFFE identity"
        );
        return false;
    }
}
