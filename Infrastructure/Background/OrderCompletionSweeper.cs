using Kinetix.OrderService.Application.Completion;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Infrastructure.Background;

public class OrderCompletionSweeper(
    IServiceScopeFactory scopeFactory,
    ReturnWindow returnWindow,
    IConfiguration configuration,
    ILogger<OrderCompletionSweeper> logger
) : BackgroundService {
    private const int DefaultBatchSize = 20;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ReturnWindow _returnWindow = returnWindow;
    private readonly ILogger<OrderCompletionSweeper> _logger = logger;

    private readonly int _batchSize =
        int.TryParse(configuration["KINETIX_COMPLETION_SWEEP_BATCH"], out var batch) && batch > 0
            ? batch
            : DefaultBatchSize;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await SweepAsync(stoppingToken);
            } catch (Exception e) when (e is not OperationCanceledException) {
                _logger.LogError(
                    e, "a completion sweep failed; the next one will pick up the same rows"
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
        await RefundReturnedGoodsAsync(stoppingToken);
        await CompleteDueOrdersAsync(stoppingToken);
        await ReleaseOwedEscrowAsync(stoppingToken);
    }

    private async Task RefundReturnedGoodsAsync(CancellationToken stoppingToken) {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var refunds = scope.ServiceProvider.GetRequiredService<IReturnRefunds>();

        var now = DateTime.UtcNow;
        var due = await dbContext.OrderReturns
            .Where(r => r.Status == ReturnStatus.GOODS_RECEIVED
                && r.NextRefundAttemptAt != null
                && r.NextRefundAttemptAt <= now)
            .OrderBy(r => r.NextRefundAttemptAt)
            .Take(_batchSize)
            .ToListAsync(stoppingToken);

        foreach (var record in due) {
            if (stoppingToken.IsCancellationRequested) {
                return;
            }

            await refunds.TryRefundAsync(record, stoppingToken);
        }
    }

    private async Task CompleteDueOrdersAsync(CancellationToken stoppingToken) {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var completion = scope.ServiceProvider.GetRequiredService<IOrderCompletion>();

        var deliveredBefore = DateTime.UtcNow - _returnWindow.Length;
        var due = await dbContext.Orders
            .AsNoTracking()
            .Where(o => o.Status == OrderStatus.DELIVERED
                && o.DeliveredAt != null
                && o.DeliveredAt <= deliveredBefore
                && !dbContext.OrderReturns.Any(r => r.OrderNumber == o.OrderNumber
                    && (r.Status == ReturnStatus.OPEN || r.Status == ReturnStatus.GOODS_RECEIVED)))
            .OrderBy(o => o.DeliveredAt)
            .Select(o => o.OrderNumber)
            .Take(_batchSize)
            .ToListAsync(stoppingToken);

        foreach (var orderNumber in due) {
            if (stoppingToken.IsCancellationRequested) {
                return;
            }

            await completion.CompleteAsync(orderNumber, waitForReturnWindow: true, stoppingToken);
        }
    }

    private async Task ReleaseOwedEscrowAsync(CancellationToken stoppingToken) {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var completion = scope.ServiceProvider.GetRequiredService<IOrderCompletion>();

        var now = DateTime.UtcNow;
        var due = await dbContext.EscrowReleases
            .Where(r => r.ReleasedAt == null && r.NextAttemptAt != null && r.NextAttemptAt <= now)
            .OrderBy(r => r.NextAttemptAt)
            .Take(_batchSize)
            .ToListAsync(stoppingToken);

        foreach (var release in due) {
            if (stoppingToken.IsCancellationRequested) {
                return;
            }

            await completion.TryReleaseAsync(release, stoppingToken);
        }
    }
}
