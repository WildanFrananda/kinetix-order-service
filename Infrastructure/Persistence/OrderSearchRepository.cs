using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Application.Search;
using Kinetix.OrderService.DTOs.Responses;
using Microsoft.EntityFrameworkCore;

namespace Kinetix.OrderService.Infrastructure.Persistence;

public sealed class OrderSearchRepository(OrderDbContext dbContext) : IOrderSearchRepository {
    private readonly OrderDbContext _dbContext = dbContext;

    public async Task<IReadOnlyList<OrderSummaryResponse>> NewestFirstAsync(
        OrderSearchQuery query,
        int take,
        CancellationToken cancellationToken
    ) {
        var orders = _dbContext.Orders.AsNoTracking();

        if (query.Statuses.Count == 1) {
            var status = query.Statuses.Single();
            orders = orders.Where(o => o.Status == status);
        } else if (query.Statuses.Count > 1) {
            var statuses = query.Statuses.ToList();
            orders = orders.Where(o => statuses.Contains(o.Status));
        }

        if (query.CreatedFrom is DateTime createdFrom) {
            orders = orders.Where(o => o.CreatedAt >= createdFrom);
        }

        if (query.CreatedTo is DateTime createdTo) {
            orders = orders.Where(o => o.CreatedAt < createdTo);
        }

        if (query.MerchantPrincipalId is string merchant) {
            orders = orders.Where(o => o.MerchantPrincipalId == merchant);
        }

        if (query.CustomerPrincipalId is string customer) {
            orders = orders.Where(o => o.CustomerPrincipalId == customer);
        }

        if (query.OrderNumber is string orderNumber) {
            orders = orders.Where(o => o.OrderNumber == orderNumber);
        }

        if (query.Before is OrderSearchCursor before) {
            var beforeCreatedAt = before.CreatedAt;
            var beforeId = before.Id;

            orders = orders.Where(o => EF.Functions.LessThan(
                ValueTuple.Create(o.CreatedAt, o.Id),
                ValueTuple.Create(beforeCreatedAt, beforeId)
            ));
        }

        return await orders
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Take(take)
            .Select(o => new OrderSummaryResponse(
                o.Id,
                o.OrderNumber,
                o.Status.ToString(),
                o.CustomerPrincipalId,
                o.MerchantPrincipalId,
                o.FinalTotal,
                o.FinalShippingFee,
                o.Items.Count,
                o.CreatedAt,
                o.UpdatedAt,
                o.DeliveredAt,
                o.CompletedAt
            ))
            .ToListAsync(cancellationToken);
    }
}
