namespace Kinetix.OrderService.Infrastructure.Observability;

public static class AdminActionOutcome {
    public const string Done = "done";
    public const string Refused = "refused";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Done, Refused, Failed];
}
