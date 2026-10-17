using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Application.Lifecycle;

public readonly record struct LifecycleActor(LifecycleActorKind Kind, string? PrincipalId) {
    public static LifecycleActor System { get; } = new(LifecycleActorKind.SYSTEM, null);

    public static LifecycleActor Buyer(string principalId) => new(LifecycleActorKind.BUYER, principalId);

    public static LifecycleActor Merchant(string principalId) => new(LifecycleActorKind.MERCHANT, principalId);

    public static LifecycleActor Driver(string principalId) => new(LifecycleActorKind.DRIVER, principalId);

    public static LifecycleActor Admin(string principalId) => new(LifecycleActorKind.ADMIN, principalId);
}
