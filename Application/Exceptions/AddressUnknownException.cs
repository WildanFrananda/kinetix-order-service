namespace Kinetix.OrderService.Application.Exceptions;

public class AddressUnknownException(string principalId, Exception cause)
    : Exception($"identity did not answer where {principalId} is", cause) {
    public string PrincipalId { get; } = principalId;
}
