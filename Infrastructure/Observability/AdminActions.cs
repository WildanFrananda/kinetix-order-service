namespace Kinetix.OrderService.Infrastructure.Observability;

public static class AdminActions {
    public const string Complete = "order.complete";
    public const string ReturnReject = "order.return_reject";
    public const string ReturnAttentionList = "order.return_attention_list";
    public const string SagaAttentionList = "order.saga_attention_list";
    public const string SagaAttentionCount = "order.saga_attention_count";
    public const string OrderRead = "order.read";

    public static readonly IReadOnlyList<string> All = [
        Complete,
        ReturnReject,
        ReturnAttentionList,
        SagaAttentionList,
        SagaAttentionCount,
        OrderRead,
    ];
}
