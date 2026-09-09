using Kinetix.OrderService.Infrastructure.Lifecycle;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Controllers;

[ApiController]
[Route("health")]
public class HealthController(OrderDbContext dbContext, DrainState drain) : ControllerBase {
    [HttpGet]
    public IActionResult HealthCheck() {
        return Ok(new {
            status = "ok",
            service = "kinetix-order-service"
        });
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready() {
        if (drain.IsDraining) {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                status = "draining",
                message = "this instance received SIGTERM and is finishing its in-flight work"
            });
        }

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
