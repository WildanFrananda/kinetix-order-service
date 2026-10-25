using Kinetix.OrderService.Security;

namespace Kinetix.OrderService.Infrastructure.Observability;

public sealed class AdminAudit(KinetixMetrics metrics, ILogger<AdminAudit> logger) {
    public const string EventName = "admin_action";

    private readonly KinetixMetrics _metrics = metrics;
    private readonly ILogger<AdminAudit> _logger = logger;

    public async Task<T> AuditAsync<T>(
        HttpContext context,
        string action,
        string target,
        Func<Task<T>> act,
        Func<T, string> outcomeOf
    ) {
        T result;
        try {
            result = await act();
        } catch (Exception) {
            Record(context, action, target, AdminActionOutcome.Failed);
            throw;
        }

        Record(context, action, target, outcomeOf(result));
        return result;
    }

    public void Record(HttpContext context, string action, string target, string outcome) {
        _metrics.ObserveAdminAction(action, outcome);

        _logger.LogInformation(
            "{event}: {actor} {action} {target} {outcome} (request {request_id})",
            EventName,
            CallerPrincipal.Of(context.User),
            action,
            target,
            outcome,
            context.TraceIdentifier
        );
    }
}
