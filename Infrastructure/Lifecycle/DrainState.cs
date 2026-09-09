namespace Kinetix.OrderService.Infrastructure.Lifecycle;

public sealed class DrainState {
    private volatile bool _draining;
    public bool IsDraining => _draining;
    public void Begin() {
        _draining = true;
    }
}
