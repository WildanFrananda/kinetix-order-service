namespace Kinetix.OrderService.Application.Exceptions;

public class AddressPlacementUnavailableException(string detail, Exception? cause = null)
    : Exception($"the shipping address could not be looked up right now: {detail}", cause);
