using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Infrastructure.Background;

public class CourierDispatchSweeper(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<CourierDispatchSweeper> logger
) : BackgroundService {
    private const int DefaultBatchSize = 20;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<CourierDispatchSweeper> _logger = logger;

    private readonly int _batchSize =
        int.TryParse(configuration["KINETIX_DISPATCH_SWEEP_BATCH"], out var batch) && batch > 0
            ? batch
            : DefaultBatchSize;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await Task.Delay(TimeSpan.FromSeconds(40), stoppingToken);

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await SweepAsync(stoppingToken);
            } catch (Exception e) when (e is not OperationCanceledException) {
                _logger.LogError(
                    e, "a dispatch sweep failed; the next one will pick up the same rows"
                );
            }

            try {
                await Task.Delay(Interval, stoppingToken);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    public async Task SweepAsync(CancellationToken stoppingToken) {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICourierDispatcher>();

        var now = DateTime.UtcNow;

        var due = await dbContext.CourierDispatches
            .Where(d => d.NextAttemptAt != null && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(_batchSize)
            .ToListAsync(stoppingToken);

        if (due.Count == 0) {
            return;
        }

        _logger.LogInformation("{Count} packed order(s) are still owed a courier", due.Count);

        foreach (var dispatch in due) {
            if (stoppingToken.IsCancellationRequested) {
                return;
            }

            var order = await dbContext.Orders.FirstOrDefaultAsync(
                o => o.OrderNumber == dispatch.OrderNumber, stoppingToken
            );

            if (order is null) {
                dispatch.NextAttemptAt = null;
                dispatch.LastError = "no order carries this number";
                continue;
            }

            await dispatcher.AttemptAsync(dispatch, order);
        }

        await dbContext.SaveChangesAsync(stoppingToken);
    }
}
