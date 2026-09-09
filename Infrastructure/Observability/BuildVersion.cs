using System.Reflection;

namespace Kinetix.OrderService.Infrastructure.Observability;

public static class BuildVersion {
    public const string Unknown = "unknown";

    private const int ShaLabelLength = 12;

    public static string Of(string? configured) {
        var version = string.IsNullOrWhiteSpace(configured)
            ? InformationalVersion()
            : configured.Trim();

        if (string.IsNullOrWhiteSpace(version)) {
            return Unknown;
        }

        return WithShortenedCommit(version);
    }

    private static string? InformationalVersion() {
        return typeof(BuildVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
    }

    private static string WithShortenedCommit(string version) {
        var plus = version.IndexOf('+');
        if (plus < 0) {
            return version;
        }

        var revision = version[(plus + 1)..];
        if (revision.Length <= ShaLabelLength || !revision.All(Uri.IsHexDigit)) {
            return version;
        }

        return string.Concat(version[..(plus + 1)], revision[..ShaLabelLength]);
    }
}
