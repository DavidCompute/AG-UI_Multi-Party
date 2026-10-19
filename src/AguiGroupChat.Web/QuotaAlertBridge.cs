using AguiGroupChat.Hub.Agents;

namespace AguiGroupChat.Web;

/// <summary>
/// 配额阈值 → 出站 Webhook 桥接（1.0.270）。
/// <para>
/// <see cref="AgentUsageService"/> 在每次记录用量后检测「当日用量首次跨过 80% / 100%」，这里把该回调
/// 转成一条 <c>QUOTA_THRESHOLD</c> 事件，走既有 <see cref="WebhookDispatcher"/>（订阅过滤 / HMAC 签名 / 重试 /
/// 投递记录全部复用）。Web 与桌面组合根都调用，行为一致。
/// </para>
/// <para>
/// 为什么不需要额外的去重：每个作用域每级每 UTC 日**只回调一次**（由服务端 `_thresholdNotified` 保证），
/// 所以不会因为每次模型调用都推送而刷屏。
/// </para>
/// </summary>
public static class QuotaAlertBridge
{
    /// <summary>把配额阈值回调接到 Webhook。未注册对应服务（精简宿主）时静默跳过。</summary>
    public static void WireQuotaAlerts(this IServiceProvider services)
    {
        var usage = services.GetService(typeof(AgentUsageService)) as AgentUsageService;
        var dispatcher = services.GetService(typeof(WebhookDispatcher)) as WebhookDispatcher;
        if (usage is null || dispatcher is null) return;

        usage.OnQuotaThreshold = evt =>
        {
            // 知聚作用域带上 groupId：订阅的「限定知聚」过滤才有意义；
            // 个人作用域传空串 → 只有未限定知聚的订阅会收到（个人超额不属于任何知聚）。
            var groupId = string.Equals(evt.Scope, "group", StringComparison.Ordinal) ? evt.ScopeId : "";
            dispatcher.OnEvent(groupId, evt);
        };
    }
}
