namespace Kinetix.OrderService.Application.Exceptions;

public class RecipientMissingException(string field)
    : Exception($"{field} is required: a parcel needs a person to hand it to, not only a street") {
    public string Field { get; } = field;
}
