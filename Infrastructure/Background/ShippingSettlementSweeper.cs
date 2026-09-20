using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Infrastructure.Background;

public class ShippingSettlementSweeper(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ShippingSettlementSweeper> logger
) : BackgroundService {
    private const int DefaultBatchSize = 20;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly ILogger<ShippingSettlementSweeper> _logger = logger;

    private readonly int _batchSize =
        int.TryParse(configuration["KINETIX_SETTLEMENT_SWEEP_BATCH"], out var batch) && batch > 0
            ? batch
            : DefaultBatchSize;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await SweepAsync(stoppingToken);
            } catch (Exception e) when (e is not OperationCanceledException) {
                _logger.LogError(
                    e, "a settlement sweep failed; the next one will pick up the same rows"
                );
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
        var escrowClient = scope.ServiceProvider.GetRequiredService<IEscrowClient>();

        var now = DateTime.UtcNow;

        var due = await dbContext.ShippingSettlements
            .Where(s => s.SettledAt == null && s.NextAttemptAt != null && s.NextAttemptAt <= now)
            .OrderBy(s => s.NextAttemptAt)
            .Take(_batchSize)
            .ToListAsync(stoppingToken);

        if (due.Count == 0) {
            return;
        }

        _logger.LogInformation(
            "{Count} shipping fee(s) are owed to couriers and not yet settled", due.Count
        );

        foreach (var settlement in due) {
            if (stoppingToken.IsCancellationRequested) {
                return;
            }

            settlement.Attempts += 1;
            settlement.UpdatedAt = DateTime.UtcNow;

            try {
                var result = await escrowClient.SettleShippingFeeAsync(
                    settlement.OrderNumber, settlement.DriverPrincipalId
                );

                if (result.Success) {
                    settlement.SettledAt = DateTime.UtcNow;
                    settlement.NextAttemptAt = null;
                    settlement.LastError = null;

                    _logger.LogInformation(
                        "the shipping fee for {Order} settled on attempt {Attempt}",
                        settlement.OrderNumber, settlement.Attempts
                    );
                } else {
                    Defer(settlement, result.Detail ?? "payment refused the settlement");
                }
            } catch (Exception e) when (e is not OperationCanceledException) {
                Defer(settlement, e.Message);
            }
        }

        await dbContext.SaveChangesAsync(stoppingToken);
    }

    private void Defer(Domain.Entities.ShippingSettlement settlement, string reason) {
        var backoff = TimeSpan.FromMinutes(Math.Min(settlement.Attempts, MaxBackoff.TotalMinutes));

        settlement.LastError = reason.Length <= 500 ? reason : reason[..500];
        settlement.NextAttemptAt = DateTime.UtcNow.Add(backoff);

        _logger.LogWarning(
            "the shipping fee for {Order} is still owed after {Attempts} attempt(s): {Reason}. "
          + "Next attempt in {Backoff}.",
            settlement.OrderNumber, settlement.Attempts, settlement.LastError, backoff
        );
    }
}
