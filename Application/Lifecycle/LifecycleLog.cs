using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using Kinetix.OrderService.Infrastructure.Persistence;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Application.Lifecycle;

public static class LifecycleLog {
    public static void RecordPlaced(this OrderDbContext dbContext, OrderEntity order, LifecycleActor actor) {
        dbContext.OrderLifecycleEvents.Add(StatusEvent(order, null, actor, order.CreatedAt));
    }

    public static void ChangeStatus(
        this OrderDbContext dbContext,
        OrderEntity order,
        OrderStatus to,
        LifecycleActor actor,
        DateTime occurredAt
    ) {
        var from = order.Status;
        if (from == to) {
            return;
        }

        order.Status = to;
        order.UpdatedAt = occurredAt;

        dbContext.OrderLifecycleEvents.Add(StatusEvent(order, from, actor, occurredAt));
    }

    public static void RecordReturnOpened(
        this OrderDbContext dbContext,
        OrderReturn record,
        string buyerPrincipalId,
        LifecycleActor actor
    ) {
        dbContext.OrderLifecycleEvents.Add(ReturnEvent(record, buyerPrincipalId, actor, record.OpenedAt));
    }

    public static void ChangeReturnStatus(
        this OrderDbContext dbContext,
        OrderReturn record,
        ReturnStatus to,
        string buyerPrincipalId,
        LifecycleActor actor,
        DateTime occurredAt
    ) {
        record.Status = to;
        record.UpdatedAt = occurredAt;

        dbContext.OrderLifecycleEvents.Add(ReturnEvent(record, buyerPrincipalId, actor, occurredAt));
    }

    private static OrderLifecycleEvent StatusEvent(
        OrderEntity order,
        OrderStatus? from,
        LifecycleActor actor,
        DateTime occurredAt
    ) {
        return new OrderLifecycleEvent {
            OrderNumber = order.OrderNumber,
            Kind = LifecycleEventKind.STATUS_CHANGED,
            FromStatus = from,
            ToStatus = order.Status,
            ActorKind = actor.Kind,
            ActorPrincipalId = actor.PrincipalId,
            BuyerPrincipalId = order.CustomerPrincipalId,
            MerchantPrincipalId = order.MerchantPrincipalId,
            OccurredAt = occurredAt,
        };
    }

    private static OrderLifecycleEvent ReturnEvent(
        OrderReturn record,
        string buyerPrincipalId,
        LifecycleActor actor,
        DateTime occurredAt
    ) {
        return new OrderLifecycleEvent {
            OrderNumber = record.OrderNumber,
            Kind = KindOf(record.Status),
            ReturnNumber = record.ReturnNumber,
            ReturnStatus = record.Status,
            ActorKind = actor.Kind,
            ActorPrincipalId = actor.PrincipalId,
            BuyerPrincipalId = buyerPrincipalId,
            MerchantPrincipalId = record.MerchantPrincipalId,
            OccurredAt = occurredAt,
        };
    }

    private static LifecycleEventKind KindOf(ReturnStatus status) {
        return status switch {
            ReturnStatus.OPEN => LifecycleEventKind.RETURN_OPENED,
            ReturnStatus.GOODS_RECEIVED => LifecycleEventKind.RETURN_GOODS_RECEIVED,
            ReturnStatus.RESOLVED => LifecycleEventKind.RETURN_RESOLVED,
            ReturnStatus.REJECTED => LifecycleEventKind.RETURN_REJECTED,
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "a return status with no lifecycle event"
            ),
        };
    }
}
