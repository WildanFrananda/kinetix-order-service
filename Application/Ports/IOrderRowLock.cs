namespace Kinetix.OrderService.Application.Ports;

public interface IOrderRowLock {
    Task LockAsync(string orderNumber, CancellationToken cancellationToken);
}
