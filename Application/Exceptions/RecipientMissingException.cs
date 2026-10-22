namespace Kinetix.OrderService.Application.Exceptions;

public class RecipientMissingException(string field)
    : Exception($"{field} is required: a parcel needs an address and a person to hand it to") {
    public string Field { get; } = field;
}
