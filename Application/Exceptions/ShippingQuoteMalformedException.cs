namespace Kinetix.OrderService.Application.Exceptions;

public class ShippingQuoteMalformedException(string fault)
    : Exception($"matching's shipping answer cannot be read as a quote: {fault}") {

    public string Fault { get; } = fault;
}
