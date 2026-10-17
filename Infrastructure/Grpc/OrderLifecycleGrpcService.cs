using Common.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using OrderProto = global::Order.V1;

namespace Kinetix.OrderService.Infrastructure.Grpc;

public class OrderLifecycleGrpcService(
    IOrderLifecycleFeed feed
) : OrderProto.OrderLifecycleService.OrderLifecycleServiceBase {
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private readonly IOrderLifecycleFeed _feed = feed;

    public override async Task<OrderProto.LifecycleEventsSinceResponse> LifecycleEventsSince(
        OrderProto.LifecycleEventsSinceRequest request,
        ServerCallContext context
    ) {
        int limit = request.Limit switch {
            <= 0 => DefaultLimit,
            > MaxLimit => MaxLimit,
            _ => request.Limit
        };

        ulong afterTransaction = request.After?.TransactionId ?? 0;
        ulong afterSequence = request.After?.Sequence ?? 0;

        if (afterSequence > long.MaxValue) {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "the cursor's sequence is beyond any row"));
        }

        var page = await _feed.ReadAfterAsync(afterTransaction, (long)afterSequence, limit, context.CancellationToken);

        var response = new OrderProto.LifecycleEventsSinceResponse { HasMore = page.HasMore };

        foreach (var row in page.Events) {
            response.Events.Add(ToWire(row));
        }

        response.Next = page.Events.Count > 0
            ? Position(page.Events[^1])
            : new LogCursor { TransactionId = afterTransaction, Sequence = afterSequence };

        return response;
    }

    private static LogCursor Position(OrderLifecycleEvent row) {
        return new LogCursor { TransactionId = row.TransactionId, Sequence = (ulong)row.Sequence };
    }

    private static OrderProto.LifecycleEvent ToWire(OrderLifecycleEvent row) {
        return new OrderProto.LifecycleEvent {
            Position = Position(row),
            OrderNumber = row.OrderNumber,
            Kind = KindOf(row.Kind),
            FromStatus = OrderWireStatus.Of(row.FromStatus),
            ToStatus = OrderWireStatus.Of(row.ToStatus),
            ReturnNumber = row.ReturnNumber ?? string.Empty,
            ReturnStatus = OrderWireStatus.Of(row.ReturnStatus),
            ActorKind = ActorOf(row.ActorKind),
            ActorPrincipalId = row.ActorPrincipalId ?? string.Empty,
            BuyerPrincipalId = row.BuyerPrincipalId,
            MerchantPrincipalId = row.MerchantPrincipalId,
            OccurredAt = Timestamp.FromDateTime(DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc)),
        };
    }

    private static OrderProto.LifecycleEventKind KindOf(LifecycleEventKind kind) {
        return kind switch {
            LifecycleEventKind.STATUS_CHANGED => OrderProto.LifecycleEventKind.StatusChanged,
            LifecycleEventKind.RETURN_OPENED => OrderProto.LifecycleEventKind.ReturnOpened,
            LifecycleEventKind.RETURN_GOODS_RECEIVED => OrderProto.LifecycleEventKind.ReturnGoodsReceived,
            LifecycleEventKind.RETURN_RESOLVED => OrderProto.LifecycleEventKind.ReturnResolved,
            LifecycleEventKind.RETURN_REJECTED => OrderProto.LifecycleEventKind.ReturnRejected,
            LifecycleEventKind.BASELINE => OrderProto.LifecycleEventKind.Baseline,
            _ => OrderProto.LifecycleEventKind.Unspecified
        };
    }

    private static OrderProto.LifecycleActorKind ActorOf(LifecycleActorKind kind) {
        return kind switch {
            LifecycleActorKind.BUYER => OrderProto.LifecycleActorKind.Buyer,
            LifecycleActorKind.MERCHANT => OrderProto.LifecycleActorKind.Merchant,
            LifecycleActorKind.DRIVER => OrderProto.LifecycleActorKind.Driver,
            LifecycleActorKind.ADMIN => OrderProto.LifecycleActorKind.Admin,
            LifecycleActorKind.SYSTEM => OrderProto.LifecycleActorKind.System,
            _ => OrderProto.LifecycleActorKind.Unspecified
        };
    }
}
