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
    public bool? WorkToolsEnabled { get; set; }
    public bool? ThinkingMode { get; set; }
    public long? DailyTokenQuotaPerUser { get; set; }
    public List<string>? RequireApprovalToolNames { get; set; }
    public List<string>? AllowedFrameOrigins { get; set; }
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
    bool? WorkToolsEnabled,
    bool? ThinkingMode,
    long? DailyTokenQuotaPerUser,
    List<string>? RequireApprovalToolNames,
    List<string>? AllowedFrameOrigins);

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
        if (s.WorkToolsEnabled is { } c3) agents.WorkToolsEnabled = c3;
        if (s.ThinkingMode is { } c4) agents.ThinkingMode = c4;
        if (s.DailyTokenQuotaPerUser is { } c5 && c5 >= 0) agents.DailyTokenQuotaPerUser = c5;
        if (s.RequireApprovalToolNames is { } c6) agents.RequireApprovalToolNames = c6.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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
            var (meId, error) = WebIdentity.RequireAdmin(ctx, auth, authOptions);
            if (error is not null) return error;

            // 校验边界（非法值返回 400 而不是静默忽略）
            if (req.MessageHistoryLimit is { } mh && mh is not (> 0 and <= 100_000)) return BadReq("messageHistoryLimit 需在 1..100000");
            if (req.MaxGroupMembers is { } mg && mg is not (> 0 and <= 5_000)) return BadReq("maxGroupMembers 需在 1..5000");
            if (req.MaxMessageChars is { } mc && mc is not (> 0 and <= 1_000_000)) return BadReq("maxMessageChars 需在 1..1000000");
            if (req.SessionTtlHours is { } st && st is not (> 0 and <= 720)) return BadReq("sessionTtlHours 需在 1..720");
            if (req.MessageRetentionDays is < 0) return BadReq("messageRetentionDays 不能为负");
            if (req.DailyTokenQuotaPerUser is < 0) return BadReq("dailyTokenQuotaPerUser 不能为负");

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
            state.WorkToolsEnabled = req.WorkToolsEnabled ?? state.WorkToolsEnabled;
            state.ThinkingMode = req.ThinkingMode ?? state.ThinkingMode;
            state.DailyTokenQuotaPerUser = req.DailyTokenQuotaPerUser ?? state.DailyTokenQuotaPerUser;
            if (req.RequireApprovalToolNames is not null) state.RequireApprovalToolNames = req.RequireApprovalToolNames;
            if (req.AllowedFrameOrigins is not null) state.AllowedFrameOrigins = req.AllowedFrameOrigins;

            Apply(state, groupChat, authOptions, agents);
            changes.Notify(); // 驱动持久化

            audit.Record("config.update", meId, auth.GetUser(meId)?.Username,
                detail: "更新运行配置（会话/群/消息/工具/审批/嵌入）");
            return Results.Ok(new { ok = true });
        });

        // 单字段便捷读取：配置治理状态当前值（含此前持久化的覆盖）
        root.MapGet("/config/governance", (HttpContext ctx, AuthService auth, AuthOptions authOptions, ConfigGovernanceState state) =>
        {
            var (_, error) = WebIdentity.RequireAdmin(ctx, auth, authOptions);
            if (error is not null) return error;
            return Results.Ok(state);
        });

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
            state.WorkToolsEnabled = saved.WorkToolsEnabled;
            state.ThinkingMode = saved.ThinkingMode;
            state.DailyTokenQuotaPerUser = saved.DailyTokenQuotaPerUser;
            state.RequireApprovalToolNames = saved.RequireApprovalToolNames;
            state.AllowedFrameOrigins = saved.AllowedFrameOrigins;
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
