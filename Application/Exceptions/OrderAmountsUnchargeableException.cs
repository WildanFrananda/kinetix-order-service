namespace Kinetix.OrderService.Application.Exceptions;

public class OrderAmountsUnchargeableException(string fault)
    : Exception($"the amounts on this order cannot be escrowed as they stand: {fault}") {

    public string Fault { get; } = fault;
}
