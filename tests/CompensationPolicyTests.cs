using Grpc.Core;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kinetix.OrderService.Tests;

public class CompensationPolicyTests {
    private static readonly string[] Settings = [
        "KINETIX_SAGA_MAX_COMPENSATION_ATTEMPTS",
        "KINETIX_SAGA_LEASE_SECONDS",
        "KINETIX_SAGA_BACKOFF_BASE_SECONDS",
        "KINETIX_SAGA_BACKOFF_MAX_SECONDS",
    ];

    private static readonly string?[] Unusable = [null, "", "  ", "0", "-3", "1.5", "six", " 6"];

    public static TheoryData<string, string?> UnusableSettings() {
        var data = new TheoryData<string, string?>();
        foreach (var setting in Settings) {
            foreach (var value in Unusable) {
                data.Add(setting, value);
            }
        }

        return data;
    }

    private static IConfiguration Configured(string? replaced = null, string? value = null) {
        var values = new Dictionary<string, string?> {
            ["KINETIX_SAGA_MAX_COMPENSATION_ATTEMPTS"] = "6",
            ["KINETIX_SAGA_LEASE_SECONDS"] = "60",
            ["KINETIX_SAGA_BACKOFF_BASE_SECONDS"] = "30",
            ["KINETIX_SAGA_BACKOFF_MAX_SECONDS"] = "1800",
        };

        if (replaced is not null) {
            values[replaced] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void ThePolicyIsReadFromItsFourSettings() {
        var policy = CompensationPolicy.FromConfiguration(Configured());

        Assert.Equal(6, policy.MaxAttempts);
        Assert.Equal(60, policy.LeaseSeconds);
        Assert.Equal(30, policy.BaseDelaySeconds);
        Assert.Equal(1800, policy.MaxDelaySeconds);
    }

    [Theory]
    [MemberData(nameof(UnusableSettings))]
    public void AMissingOrUnusableSettingStopsTheServiceStarting(string setting, string? value) {
        var failure = Assert.Throws<InvalidOperationException>(
            () => CompensationPolicy.FromConfiguration(Configured(setting, value))
        );

        Assert.Contains(setting, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    public void TheBackoffDoublesAndNeverExceedsTheRungItIsOn(int attempt, int rungSeconds) {
        var policy = CompensationPolicies.Standard();

        for (var i = 0; i < 50; i++) {
            var delay = policy.DelaySeconds(attempt);

            Assert.InRange(delay, rungSeconds / 2, rungSeconds);
        }
    }

    [Fact]
    public void TheBackoffIsCappedSoALongOutageDoesNotScheduleARetryNextWeek() {
        var policy = CompensationPolicies.Standard(baseDelaySeconds: 30, maxDelaySeconds: 600);

        Assert.InRange(policy.DelaySeconds(20), 300, 600);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.ResourceExhausted)]
    [InlineData(StatusCode.Aborted)]
    public void AnAnswerThatCouldChangeIsWorthAnotherRound(StatusCode statusCode) {
        var kind = CompensationPolicy.Classify(new RpcException(new Status(statusCode, "not now")));

        Assert.Equal(CompensationFailureKind.Transient, kind);
    }

    [Theory]
    [InlineData(StatusCode.FailedPrecondition)]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    public void AnAnswerNoRetryCanChangeGoesStraightToAPerson(StatusCode statusCode) {
        var kind = CompensationPolicy.Classify(new RpcException(new Status(statusCode, "no")));

        Assert.Equal(CompensationFailureKind.Terminal, kind);
    }

    [Fact]
    public void AnAlreadyReleasedHoldIsTerminalEvenThoughPaymentOnlySaysSoInProse() {
        var rpc = new RpcException(new Status(
            StatusCode.Unknown,
            "escrow for order ORD-1 was already released and cannot be refunded here"));

        Assert.Equal(CompensationFailureKind.Terminal, CompensationPolicy.Classify(rpc));

        var reworded = new RpcException(new Status(StatusCode.Unknown, "escrow already paid out"));
        Assert.Equal(CompensationFailureKind.Transient, CompensationPolicy.Classify(reworded));
    }
}
