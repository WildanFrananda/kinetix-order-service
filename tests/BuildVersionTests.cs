using System.Text.RegularExpressions;
using Kinetix.OrderService.Infrastructure.Observability;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class BuildVersionTests {
    private const string LooksLikeAnIdentifier = "[0-9a-f]{24,}";

    [Fact]
    public void TheVersionLabelCarriesNoRunLongEnoughToBeMistakenForAnIdentifier() {
        Assert.DoesNotMatch(new Regex(LooksLikeAnIdentifier), BuildVersion.Of(null));
    }

    [Fact]
    public void TheCommitIsShortenedButStillNamed() {
        Assert.Equal(
            "1.4.0+53ce4cd78155",
            BuildVersion.Of("1.4.0+53ce4cd78155125eacccc39cbdec0dc88a92fa31")
        );
    }

    [Fact]
    public void AVersionGivenByTheBuildWins() {
        Assert.Equal("2026.09.1", BuildVersion.Of("  2026.09.1  "));
    }

    [Fact]
    public void AVersionWithNoCommitIsLeftAlone() {
        Assert.Equal("1.4.0", BuildVersion.Of("1.4.0"));
    }

    [Fact]
    public void BuildMetadataThatIsNotACommitIsNotTruncated() {
        Assert.Equal(
            "2026.09.1+build.20260909.3",
            BuildVersion.Of("2026.09.1+build.20260909.3")
        );
    }
}
