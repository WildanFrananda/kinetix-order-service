using Kinetix.OrderService.Application.Services;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Infrastructure.Background;

public class StuckSagaSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<StuckSagaSweeper> logger
) : BackgroundService {

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<StuckSagaSweeper> _logger = logger;

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
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<CheckoutSagaRunner>();

        var cutoff = DateTime.UtcNow - Abandoned;

        var abandoned = await dbContext.CheckoutSagas
            .Include(s => s.Steps)
            .Where(s => s.State == SagaState.Running || s.State == SagaState.Compensating)
            .Where(s => s.UpdatedAt < cutoff)
            .OrderBy(s => s.UpdatedAt)
            .Take(20)
            .ToListAsync(stoppingToken);

        if (abandoned.Count == 0) {
            return;
        }

        _logger.LogWarning(
            "{Count} checkout saga(s) have been in flight since before {Cutoff:u}; unwinding them",
            abandoned.Count, cutoff);

        foreach (var saga in abandoned) {
            if (stoppingToken.IsCancellationRequested) {
                return;
            }

            await runner.Compensate(
                saga, saga.FailureReason ?? "the checkout that started this saga never finished");
        }
    }
}
