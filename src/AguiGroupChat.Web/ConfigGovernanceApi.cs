using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 配置治理（6.3）：管理员在线查看 / 调整运维参数并持久化，无需编辑 appsettings。
/// 只开放<b>运行时安全可改</b>的旋钮：会话 / 群上限 / 消息策略 / 工具开关 / 审批名单 / iframe 来源 / 链接代理。
/// 存储提供方（Storage）、模型提供方（Agents.Provider）、记忆提供方等需重启的项目只读（见 AdminApi GET /config），
/// 这里返回错误提示重启。
/// </summary>
public sealed class ConfigGovernanceState
{
    // 已保存的覆盖值（null = 沿用配置/默认；展开为可持久化的标量 / 列表）
    public int? MessageHistoryLimit { get; set; }
    public int? MaxGroupMembers { get; set; }
    public int? MessageWriteDebounceMs { get; set; }
    public int? MaxMessageChars { get; set; }
    public int? MessageRetentionDays { get; set; }
    public bool? RequireTokenOnRealTime { get; set; }
    public int? SessionTtlHours { get; set; }
    public bool? EnableTools { get; set; }
    public bool? EnableWebTools { get; set; }
    public bool? ThinkingMode { get; set; }
    public long? DailyTokenQuotaPerUser { get; set; }
    public long? DailyTokenQuotaPerGroup { get; set; }
    public List<string>? RequireApprovalToolNames { get; set; }
    public List<string>? AllowedFrameOrigins { get; set; }
    // 按任务复杂度自动选模型（6.7）：启用 + 各档模型名（留空 = 该档不换）
    public bool? ModelRoutingEnabled { get; set; }
    public string? ModelRoutingSimpleModel { get; set; }
    public string? ModelRoutingStandardModel { get; set; }
    public string? ModelRoutingHeavyModel { get; set; }
    // 记忆与向量化（6.3 + 2.4/2.6）：这些是“检索慢 / 超时”的主旋钮，均可运行时热改。
    // 均为**每次调用重新读取**的字段，因此保存即对后续写入 / 检索生效。
    public int? MemoryMaxQueryChars { get; set; }
    public int? MemoryMaxWriteChars { get; set; }
    public int? MemoryInteractiveEmbeddingWaitSeconds { get; set; }
    public int? MemoryBackgroundEmbeddingWaitSeconds { get; set; }
    public int? MemoryWriteEmbeddingCacheSize { get; set; }
    // 临时放额到期提醒（1.0.275）：提前多久提醒（小时）与扫描间隔（分钟）。
    // null = 用服务内置默认（24h / 15min）；服务每轮扫描重新读取，因此保存后**下一轮**生效。
    public int? QuotaReminderLeadHours { get; set; }
    public int? QuotaReminderScanMinutes { get; set; }
    // 投递失败站内告警（1.0.294）：客户端轮询的参数（轮询间隔 / 结算窗口 / 同订阅冷却）；
    // 均为前端读取后使用，服务端只负责保存与回读。null = 用前端内置默认（5min / 40s / 10min）。
    public bool? WebhookAlarmEnabled { get; set; }
    public int? WebhookAlarmPollMinutes { get; set; }
    public int? WebhookAlarmSettleSeconds { get; set; }
    public int? WebhookAlarmCooldownMinutes { get; set; }
}

/// <summary>配置治理写入请求体（每个字段可空，仅更新非空项）。</summary>
public sealed record ConfigGovernanceHttpRequest(
    int? MessageHistoryLimit,
    int? MaxGroupMembers,
    int? MessageWriteDebounceMs,
    int? MaxMessageChars,
    int? MessageRetentionDays,
    bool? RequireTokenOnRealTime,
    int? SessionTtlHours,
    bool? EnableTools,
    bool? EnableWebTools,
    bool? ThinkingMode,
    long? DailyTokenQuotaPerUser,
    long? DailyTokenQuotaPerGroup,
    List<string>? RequireApprovalToolNames,
    List<string>? AllowedFrameOrigins,
    bool? ModelRoutingEnabled,
    string? ModelRoutingSimpleModel,
    string? ModelRoutingStandardModel,
    string? ModelRoutingHeavyModel,
    // 记忆与向量化（6.3）：运行时热改的记忆效率旋钮
    int? MemoryMaxQueryChars,
    int? MemoryMaxWriteChars,
    int? MemoryInteractiveEmbeddingWaitSeconds,
    int? MemoryBackgroundEmbeddingWaitSeconds,
    int? MemoryWriteEmbeddingCacheSize,
    // 临时放额到期提醒（1.0.275）：提前量与扫描间隔
    int? QuotaReminderLeadHours,
    int? QuotaReminderScanMinutes,
    // 投递失败站内告警（1.0.294）：前端轮询参数
    bool? WebhookAlarmEnabled,
    int? WebhookAlarmPollMinutes,
    int? WebhookAlarmSettleSeconds,
    int? WebhookAlarmCooldownMinutes);

public static class ConfigGovernanceApi
{
    /// <summary>把治理状态应用到运行时单例（GroupChatOptions / AuthOptions / AgentOptions）。</summary>
    public static void Apply(ConfigGovernanceState s, GroupChatOptions groupChat, AuthOptions auth, AgentOptions agents)
    {
        if (s.MessageHistoryLimit is { } a1 && a1 is > 0 and <= 100_000) groupChat.MessageHistoryLimit = a1;
        if (s.MaxGroupMembers is { } a2 && a2 is > 0 and <= 5_000) groupChat.MaxGroupMembers = a2;
        if (s.MessageWriteDebounceMs is { } a3 && a3 >= 0) groupChat.MessageWriteDebounceMs = a3;
        if (s.MaxMessageChars is { } a4 && a4 is > 0 and <= 1_000_000) groupChat.MaxMessageChars = a4;
        if (s.MessageRetentionDays is { } a5 && a5 >= 0) groupChat.MessageRetentionDays = a5;

        if (s.RequireTokenOnRealTime is { } b1) auth.RequireTokenOnRealTime = b1;
        if (s.SessionTtlHours is { } b2 && b2 is > 0 and <= 24 * 30) auth.SessionTtlHours = b2;

        if (s.EnableTools is { } c1) agents.EnableTools = c1;
        if (s.EnableWebTools is { } c2) agents.EnableWebTools = c2;
        if (s.ThinkingMode is { } c4) agents.ThinkingMode = c4;
        if (s.DailyTokenQuotaPerUser is { } c5 && c5 >= 0) agents.DailyTokenQuotaPerUser = c5;
        if (s.DailyTokenQuotaPerGroup is { } c5g && c5g >= 0) agents.DailyTokenQuotaPerGroup = c5g;
        if (s.RequireApprovalToolNames is { } c6) agents.RequireApprovalToolNames = c6.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // 模型路由（6.7）：启用与各档模型名（ModelRouting 已初始化为非空，直接改属性即可）
        if (s.ModelRoutingEnabled is { } c7) agents.ModelRouting.Enabled = c7;
        if (s.ModelRoutingSimpleModel is { } c8) agents.ModelRouting.SimpleModel = c8.Trim();
        if (s.ModelRoutingStandardModel is { } c9) agents.ModelRouting.StandardModel = c9.Trim();
        if (s.ModelRoutingHeavyModel is { } c10) agents.ModelRouting.HeavyModel = c10.Trim();

        // 记忆与向量化（6.3）：每次调用重新读取，故保存即生效（无需重启）
        if (s.MemoryMaxQueryChars is { } m1 && m1 is > 0 and <= 10_000) agents.Memory.MaxQueryChars = m1;
        if (s.MemoryMaxWriteChars is { } m2 && m2 is > 0 and <= 10_000) agents.Memory.MaxWriteChars = m2;
        if (s.MemoryInteractiveEmbeddingWaitSeconds is { } m3 && m3 is > 0 and <= 600) agents.Memory.InteractiveEmbeddingWaitSeconds = m3;
        if (s.MemoryBackgroundEmbeddingWaitSeconds is { } m4 && m4 is > 0 and <= 3_600) agents.Memory.BackgroundEmbeddingWaitSeconds = m4;
        if (s.MemoryWriteEmbeddingCacheSize is { } m5 && m5 is >= 0 and <= 100_000) agents.Memory.WriteEmbeddingCacheSize = m5;

        if (s.AllowedFrameOrigins is { } d1)
            groupChat.AllowedFrameOrigins = d1.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
    }

    public static void MapConfigGovernanceApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/admin");

        // 配置治理：保存运行时安全可改的旋钮（仅管理员）
        root.MapPost("/config", (ConfigGovernanceHttpRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions,
            GroupChatOptions groupChat, AgentOptions agents, IServiceProvider sp,
            ConfigGovernanceState state, ChangeHub changes, AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;

            // 校验边界（非法值返回 400 而不是静默忽略）
            if (req.MessageHistoryLimit is { } mh && mh is not (> 0 and <= 100_000)) return BadReq("messageHistoryLimit 需在 1..100000");
            if (req.MaxGroupMembers is { } mg && mg is not (> 0 and <= 5_000)) return BadReq("maxGroupMembers 需在 1..5000");
            if (req.MaxMessageChars is { } mc && mc is not (> 0 and <= 1_000_000)) return BadReq("maxMessageChars 需在 1..1000000");
            if (req.SessionTtlHours is { } st && st is not (> 0 and <= 720)) return BadReq("sessionTtlHours 需在 1..720");
            if (req.MessageRetentionDays is < 0) return BadReq("messageRetentionDays 不能为负");
            if (req.DailyTokenQuotaPerUser is < 0) return BadReq("dailyTokenQuotaPerUser 不能为负");
            if (req.DailyTokenQuotaPerGroup is < 0) return BadReq("dailyTokenQuotaPerGroup 不能为负");
            if (req.ModelRoutingSimpleModel is { Length: > 200 } || req.ModelRoutingStandardModel is { Length: > 200 } || req.ModelRoutingHeavyModel is { Length: > 200 })
                return BadReq("模型名过长（≤200 字符）");
            if (req.MemoryMaxQueryChars is { } q1 && q1 is not (> 0 and <= 10_000)) return BadReq("memoryMaxQueryChars 需在 1..10000");
            if (req.MemoryMaxWriteChars is { } q2 && q2 is not (> 0 and <= 10_000)) return BadReq("memoryMaxWriteChars 需在 1..10000");
            if (req.MemoryInteractiveEmbeddingWaitSeconds is { } q3 && q3 is not (> 0 and <= 600)) return BadReq("memoryInteractiveEmbeddingWaitSeconds 需在 1..600");
            if (req.MemoryBackgroundEmbeddingWaitSeconds is { } q4 && q4 is not (> 0 and <= 3_600)) return BadReq("memoryBackgroundEmbeddingWaitSeconds 需在 1..3600");
            if (req.MemoryWriteEmbeddingCacheSize is < 0) return BadReq("memoryWriteEmbeddingCacheSize 不能为负");
            if (req.QuotaReminderLeadHours is { } qr1 && qr1 is not (> 0 and <= 720)) return BadReq("quotaReminderLeadHours 需在 1..720");
            if (req.QuotaReminderScanMinutes is { } qr2 && qr2 is not (> 0 and <= 1440)) return BadReq("quotaReminderScanMinutes 需在 1..1440");
            if (req.WebhookAlarmPollMinutes is { } wa1 && wa1 is not (> 0 and <= 1440)) return BadReq("webhookAlarmPollMinutes 需在 1..1440");
            if (req.WebhookAlarmSettleSeconds is { } wa2 && wa2 is not (>= 5 and <= 3600)) return BadReq("webhookAlarmSettleSeconds 需在 5..3600");
            if (req.WebhookAlarmCooldownMinutes is { } wa3 && wa3 is not (> 0 and <= 1440)) return BadReq("webhookAlarmCooldownMinutes 需在 1..1440");

            // 更新持久化状态（保留未传/为空字段的旧值；数组用 ??= 保留旧列表）
            state.MessageHistoryLimit = req.MessageHistoryLimit ?? state.MessageHistoryLimit;
            state.MaxGroupMembers = req.MaxGroupMembers ?? state.MaxGroupMembers;
            state.MessageWriteDebounceMs = req.MessageWriteDebounceMs ?? state.MessageWriteDebounceMs;
            state.MaxMessageChars = req.MaxMessageChars ?? state.MaxMessageChars;
            state.MessageRetentionDays = req.MessageRetentionDays ?? state.MessageRetentionDays;
            state.RequireTokenOnRealTime = req.RequireTokenOnRealTime ?? state.RequireTokenOnRealTime;
            state.SessionTtlHours = req.SessionTtlHours ?? state.SessionTtlHours;
            state.EnableTools = req.EnableTools ?? state.EnableTools;
            state.EnableWebTools = req.EnableWebTools ?? state.EnableWebTools;
            state.ThinkingMode = req.ThinkingMode ?? state.ThinkingMode;
            state.DailyTokenQuotaPerUser = req.DailyTokenQuotaPerUser ?? state.DailyTokenQuotaPerUser;
            state.DailyTokenQuotaPerGroup = req.DailyTokenQuotaPerGroup ?? state.DailyTokenQuotaPerGroup;
            if (req.RequireApprovalToolNames is not null) state.RequireApprovalToolNames = req.RequireApprovalToolNames;
            if (req.AllowedFrameOrigins is not null) state.AllowedFrameOrigins = req.AllowedFrameOrigins;
            state.ModelRoutingEnabled = req.ModelRoutingEnabled ?? state.ModelRoutingEnabled;
            if (req.ModelRoutingSimpleModel is not null) state.ModelRoutingSimpleModel = req.ModelRoutingSimpleModel;
            if (req.ModelRoutingStandardModel is not null) state.ModelRoutingStandardModel = req.ModelRoutingStandardModel;
            if (req.ModelRoutingHeavyModel is not null) state.ModelRoutingHeavyModel = req.ModelRoutingHeavyModel;
            state.MemoryMaxQueryChars = req.MemoryMaxQueryChars ?? state.MemoryMaxQueryChars;
            state.MemoryMaxWriteChars = req.MemoryMaxWriteChars ?? state.MemoryMaxWriteChars;
            state.MemoryInteractiveEmbeddingWaitSeconds = req.MemoryInteractiveEmbeddingWaitSeconds ?? state.MemoryInteractiveEmbeddingWaitSeconds;
            state.MemoryBackgroundEmbeddingWaitSeconds = req.MemoryBackgroundEmbeddingWaitSeconds ?? state.MemoryBackgroundEmbeddingWaitSeconds;
            state.MemoryWriteEmbeddingCacheSize = req.MemoryWriteEmbeddingCacheSize ?? state.MemoryWriteEmbeddingCacheSize;
            state.QuotaReminderLeadHours = req.QuotaReminderLeadHours ?? state.QuotaReminderLeadHours;
            state.QuotaReminderScanMinutes = req.QuotaReminderScanMinutes ?? state.QuotaReminderScanMinutes;
            state.WebhookAlarmEnabled = req.WebhookAlarmEnabled ?? state.WebhookAlarmEnabled;
            state.WebhookAlarmPollMinutes = req.WebhookAlarmPollMinutes ?? state.WebhookAlarmPollMinutes;
            state.WebhookAlarmSettleSeconds = req.WebhookAlarmSettleSeconds ?? state.WebhookAlarmSettleSeconds;
            state.WebhookAlarmCooldownMinutes = req.WebhookAlarmCooldownMinutes ?? state.WebhookAlarmCooldownMinutes;

            Apply(state, groupChat, authOptions, agents);
            changes.Notify(); // 驱动持久化
            // 提醒间隔可能刚被改：按新值重新安排下一轮（提前量在每轮扫描时自然读取）
            sp.GetService<QuotaExpiryReminderService>()?.Reschedule();

            audit.Record("config.update", me, auth.GetUser(me)?.Username,
                detail: "更新运行配置（会话/群/消息/工具/审批/嵌入）");
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // 单字段便捷读取：配置治理状态当前值（含此前持久化的覆盖）
        root.MapGet("/config/governance", (HttpContext ctx, ConfigGovernanceState state) =>
        {
            return Results.Ok(state);
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        static IResult BadReq(string msg) => Results.BadRequest(new AguiError(ErrorCodes.BadRequest, msg));
    }

    /// <summary>注册配置治理覆盖值到持久化扩展区「configGovernance」：重启后自动应用覆盖。</summary>
    public static void RegisterConfigGovernancePersistence(this IServiceProvider services)
    {
        var state = services.GetRequiredService<ConfigGovernanceState>();
        Func<object?> snapshot = () => state;
        Action<JsonElement> restore = element =>
        {
            var saved = element.Deserialize<ConfigGovernanceState>(AguiJson.Options);
            if (saved is null) return;
            // 逐字段复制（保持引用一致的单例）
            state.MessageHistoryLimit = saved.MessageHistoryLimit;
            state.MaxGroupMembers = saved.MaxGroupMembers;
            state.MessageWriteDebounceMs = saved.MessageWriteDebounceMs;
            state.MaxMessageChars = saved.MaxMessageChars;
            state.MessageRetentionDays = saved.MessageRetentionDays;
            state.RequireTokenOnRealTime = saved.RequireTokenOnRealTime;
            state.SessionTtlHours = saved.SessionTtlHours;
            state.EnableTools = saved.EnableTools;
            state.EnableWebTools = saved.EnableWebTools;
            state.ThinkingMode = saved.ThinkingMode;
            state.DailyTokenQuotaPerUser = saved.DailyTokenQuotaPerUser;
            state.DailyTokenQuotaPerGroup = saved.DailyTokenQuotaPerGroup;
            state.RequireApprovalToolNames = saved.RequireApprovalToolNames;
            state.AllowedFrameOrigins = saved.AllowedFrameOrigins;
            state.ModelRoutingEnabled = saved.ModelRoutingEnabled;
            state.ModelRoutingSimpleModel = saved.ModelRoutingSimpleModel;
            state.ModelRoutingStandardModel = saved.ModelRoutingStandardModel;
            state.ModelRoutingHeavyModel = saved.ModelRoutingHeavyModel;
            state.MemoryMaxQueryChars = saved.MemoryMaxQueryChars;
            state.MemoryMaxWriteChars = saved.MemoryMaxWriteChars;
            state.MemoryInteractiveEmbeddingWaitSeconds = saved.MemoryInteractiveEmbeddingWaitSeconds;
            state.MemoryBackgroundEmbeddingWaitSeconds = saved.MemoryBackgroundEmbeddingWaitSeconds;
            state.MemoryWriteEmbeddingCacheSize = saved.MemoryWriteEmbeddingCacheSize;
            state.QuotaReminderLeadHours = saved.QuotaReminderLeadHours;
            state.QuotaReminderScanMinutes = saved.QuotaReminderScanMinutes;
            state.WebhookAlarmEnabled = saved.WebhookAlarmEnabled;
            state.WebhookAlarmPollMinutes = saved.WebhookAlarmPollMinutes;
            state.WebhookAlarmSettleSeconds = saved.WebhookAlarmSettleSeconds;
            state.WebhookAlarmCooldownMinutes = saved.WebhookAlarmCooldownMinutes;
            // 把覆盖应用到运行时单例（重启后重新加载配置时一并覆盖）
            var groupChat = services.GetService<GroupChatOptions>();
            var auth = services.GetService<AuthOptions>();
            var agents = services.GetService<AgentOptions>();
            if (groupChat is not null && auth is not null && agents is not null)
                Apply(state, groupChat, auth, agents);
        };

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
            persistence.AddSection("configGovernance", snapshot, restore);
        else
            services.GetService<ISectionStore>()?.AddSection("configGovernance", snapshot, restore);
    }
}
