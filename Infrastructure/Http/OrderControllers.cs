namespace Kinetix.OrderService.Infrastructure.Http;

public static class OrderControllers {
    public static IMvcBuilder AddOrderControllers(this IServiceCollection services) =>
        services.AddControllers().AddJsonOptions(options => options.AllowInputFormatterExceptionMessages = false);
}
