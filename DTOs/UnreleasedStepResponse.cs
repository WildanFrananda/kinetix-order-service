namespace Kinetix.OrderService.DTOs;

public record UnreleasedStepResponse(
    string Name,
    string Reference,
    int Quantity,
    string MerchantPrincipalId,
    string? ProductId,
    string State,
    int CompensationAttempts,
    string? LastFailureCode,
    string? Detail,
    string Remedy
);
