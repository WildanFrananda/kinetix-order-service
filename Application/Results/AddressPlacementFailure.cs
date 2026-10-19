namespace Kinetix.OrderService.Application.Results;

public enum AddressPlacementFailure {
    NotFound,
    Ambiguous,
    OutOfServiceArea,
    Insufficient,
    TooVague
}
