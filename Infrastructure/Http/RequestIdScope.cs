namespace Kinetix.OrderService.Infrastructure.Http;

public sealed class RequestIdScope(string? previous) : IDisposable {
    private readonly string? _previous = previous;
    private bool _disposed;

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        RequestIdAccessor.Restore(_previous);
    }
}
