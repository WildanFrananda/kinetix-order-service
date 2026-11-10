using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Search;
using Kinetix.OrderService.DTOs.Responses;
using Kinetix.OrderService.Infrastructure.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Authorize(Policy = SagaAdminController.SagaAdminPolicy)]
[Route("api/v1/orders/search")]
public class OrderSearchController(IOrderSearch search, AdminAudit audit) : ControllerBase {
    public const string SearchTarget = "orders/search";

    private readonly IOrderSearch _search = search;
    private readonly AdminAudit _audit = audit;

    [HttpGet]
    public async Task<IActionResult> Search(CancellationToken cancellationToken) {
        var target = TargetOf(OrderSearchParameters.NamedIn(Request.Query));
        var parsing = OrderSearchParameters.Parse(Request.Query);

        if (parsing.Refusal is OrderSearchRefusal refusal) {
            _audit.Record(HttpContext, AdminActions.OrderSearch, target, AdminActionOutcome.Refused);
            return BadRequest(BodyOf(refusal));
        }

        var page = await _audit.AuditAsync(
            HttpContext,
            AdminActions.OrderSearch,
            target,
            () => _search.SearchAsync(parsing.Query!, cancellationToken),
            _ => AdminActionOutcome.Done
        );

        return Ok(new OrderSearchResponse(page.Orders, page.Next?.Encode()));
    }

    public static string TargetOf(IReadOnlyList<string> parameterNames) =>
        parameterNames.Count == 0 ? SearchTarget : $"{SearchTarget}?{string.Join('&', parameterNames)}";

    private static object BodyOf(OrderSearchRefusal refusal) =>
        refusal.Allowed is null
            ? new {
                error = refusal.Code,
                parameter = refusal.Parameter,
                message = refusal.Message,
            }
            : new {
                error = refusal.Code,
                parameter = refusal.Parameter,
                allowed = refusal.Allowed,
                message = refusal.Message,
            };
}
