using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kinetix.OrderService.Infrastructure.Persistence;

public class OrderDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext> {
    public OrderDbContext CreateDbContext(string[] args) {
        var connectionString =
            Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=design-time-only;Database=kinetix_order_dev;Username=design;Password=design";

        var options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new OrderDbContext(options);
    }
}
