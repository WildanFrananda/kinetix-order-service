using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Infrastructure.Background;

public class StuckSagaSweeper(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<StuckSagaSweeper> logger
) : BackgroundService {
    private const int DefaultBatchSize = 20;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private const int UnattendedGraceSeconds = 5 * 60;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<StuckSagaSweeper> _logger = logger;

    private readonly int _batchSize =
        int.TryParse(configuration["KINETIX_SAGA_SWEEP_BATCH"], out var batch) && batch > 0
            ? batch
            : DefaultBatchSize;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await SweepAsync(stoppingToken);
            } catch (Exception e) when (e is not OperationCanceledException) {
                _logger.LogError(e, "a sweep failed; the next one will pick up the same sagas");
            }

            try {
                await Task.Delay(Interval, stoppingToken);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken) {
        List<Guid> due;
        using (var scope = _scopeFactory.CreateScope()) {
            var leaseStore = scope.ServiceProvider.GetRequiredService<ISagaLeaseStore>();
            due = [.. await leaseStore.DueForSweepAsync(_batchSize, UnattendedGraceSeconds, stoppingToken)];
        }

        if (due.Count == 0) {
            return;
        }

        _logger.LogInformation(
            "{Count} checkout saga(s) are due to be unwound or retried", due.Count
        );

        foreach (var sagaId in due) {
            if (stoppingToken.IsCancellationRequested) {
                return;
            }

            using var sagaScope = _scopeFactory.CreateScope();
            var dbContext = sagaScope.ServiceProvider.GetRequiredService<OrderDbContext>();
            var runner = sagaScope.ServiceProvider.GetRequiredService<CheckoutSagaRunner>();

            var saga = await dbContext.CheckoutSagas
                .FirstOrDefaultAsync(s => s.Id == sagaId, stoppingToken);
            if (saga is null) {
                continue;
            }

            await runner.CompensateAsync(
                saga,
                saga.FailureReason ?? "the checkout that started this saga never finished",
                heldLease: null,
                stoppingToken
            );
        }
    }
}
