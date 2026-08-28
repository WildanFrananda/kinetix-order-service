using Microsoft.AspNetCore.Mvc;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase {
    [HttpGet]
    public IActionResult HealthCheck() {
        return Ok(new {
            status = "ok",
            service = "kinetix-order-service",
            runtime = ".NET 10.0 (C# 13)",
            timestamp = DateTime.UtcNow
        });
    }
}
