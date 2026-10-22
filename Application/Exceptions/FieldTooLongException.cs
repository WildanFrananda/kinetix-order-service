namespace Kinetix.OrderService.Application.Exceptions;

public class FieldTooLongException(string field, int maxLength)
    : Exception($"{field} is longer than the {maxLength} characters an order can record") {
    public string Field { get; } = field;
    public int MaxLength { get; } = maxLength;
}
