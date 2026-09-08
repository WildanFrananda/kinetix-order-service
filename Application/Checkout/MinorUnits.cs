namespace Kinetix.OrderService.Application.Checkout;

public static class MinorUnits {
    public const decimal PerMajor = 100m;

    public static bool IsWhole(decimal amount) =>
        amount * PerMajor == decimal.Truncate(amount * PerMajor);
}
