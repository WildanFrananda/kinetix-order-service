using Kinetix.OrderService.Domain.Entities;

namespace Kinetix.OrderService.Application.Lifecycle;

public sealed record LifecyclePage(IReadOnlyList<OrderLifecycleEvent> Events, bool HasMore);
