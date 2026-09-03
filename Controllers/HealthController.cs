using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Route("health")]
public class HealthController(OrderDbContext dbContext) : ControllerBase {
    [HttpGet]
    public IActionResult HealthCheck() {
        return Ok(new {
            status = "ok",
            service = "kinetix-order-service",
            runtime = ".NET 10.0 (C# 13)",
            timestamp = DateTime.UtcNow
        });
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready() {
        try {
            await dbContext.Database.ExecuteSqlRawAsync("SELECT 1");
        } catch (Exception) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                status = "unavailable",
                database = "unreachable"
            });
        }

        return Ok(new { status = "ok", database = "reachable" });
    }
}
