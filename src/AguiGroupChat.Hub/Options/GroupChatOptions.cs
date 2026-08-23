namespace AguiGroupChat.Hub.Options;

/// <summary>Hub 运行配置（appsettings.json 的 GroupChat 节点）。</summary>
public sealed class GroupChatOptions
{
    /// <summary>每个群保留的消息历史上限（内存 / JSON 快照模式：超限静默裁剪最旧消息，历史不可再翻页；
    /// 数据库模式不受此限，全量落库）。默认 1000：兼顾内存占用与历史可回溯长度。</summary>
    public int MessageHistoryLimit { get; set; } = 1000;

    /// <summary>快照（GROUP_STATE_SNAPSHOT）携带的最近消息条数。</summary>
    public int SnapshotMessageCount { get; set; } = 50;

    /// <summary>群成员数量上限（超出返回 GROUP_FULL）。</summary>
    public int MaxGroupMembers { get; set; } = 500;

    /// <summary>连接保活心跳间隔（秒）。SSE 心跳注释与 WebSocket ping 共用。</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 15;

    /// <summary>智能体流式内容的数据库写入防抖间隔（毫秒）：窗口内合并写入，消息结束时强制落库。0 = 每次增量立即写。</summary>
    public int MessageWriteDebounceMs { get; set; } = 1000;

    /// <summary>启动时是否写入示例群组与智能体（便于本地联调）。</summary>
    public bool SeedSampleData { get; set; }

    /// <summary>单条消息内容最大字符数（超出返回 BAD_REQUEST，防超大消息拖垮内存与扇出）。</summary>
    public int MaxMessageChars { get; set; } = 50_000;

    /// <summary>智能体触发调用的最大并发数（超出排队等待，防止语境智能体多 / 消息频繁时打爆模型与桥接服务）。</summary>
    public int MaxConcurrentAgentInvocations { get; set; } = 8;

    /// <summary>
    /// 消息保留天数（数据保留策略，默认 0 = 不清理）：后台服务每天检查一次，
    /// 删除超过该天数（按消息时间戳）的历史消息；群 / 成员 / 话题结构保留。
    /// 清理前请确认已做数据备份（管理员「数据备份」导出）。
    /// </summary>
    public int MessageRetentionDays { get; set; }

    /// <summary>
    /// 允许通过 <c>iframe</c> 嵌入本站的第三方来源（白标 / 嵌入，6.4），如 <c>["https://portal.example.com"]</c>。
    /// 对应 CSP <c>frame-ancestors</c> 与 X-Frame-Options；留空 = 禁止任何站点嵌入（默认，安全）。
    /// 注意：允许嵌入意味着该来源可阅读会话页面，请仅对可信站点开放。
    /// </summary>
    public List<string> AllowedFrameOrigins { get; set; } = [];
}
