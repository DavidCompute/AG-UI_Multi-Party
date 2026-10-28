using System.Text.Json;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 一条 Webhook 订阅：把知聚里发生的事件以 HTTP POST 推送到外部系统的回调地址。
/// </summary>
public sealed class WebhookSubscription
{
    public string Id { get; set; } = "";

    /// <summary>订阅名（仅用于展示）。</summary>
    public string Name { get; set; } = "";

    /// <summary>回调地址（http / https 绝对 URL）。</summary>
    public string Url { get; set; } = "";

    /// <summary>HMAC-SHA256 签名密钥（<c>whsec_</c> 前缀）；落盘时经 <see cref="SecretVault"/> 加密，只在创建 / 轮换时明文返回一次。</summary>
    public string Secret { get; set; } = "";

    /// <summary>订阅的事件类型；空 = 除高频流式增量（CONTENT / REASONING / TYPING / READ）外的全部事件。</summary>
    public List<string> EventTypes { get; set; } = [];

    /// <summary>可选：仅推送该知聚（groupId）的事件；空 = 全部知聚。</summary>
    public string? GroupId { get; set; }

    public bool Enabled { get; set; } = true;

    public long CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    /// <summary>最近一次投递成功时间（仅展示）。</summary>
    public long? LastDeliveredAt { get; set; }
}

/// <summary>
/// Webhook 订阅配置状态（持久化扩展区「webhooks」的载体）。
/// 进程内保存并在变更时通知持久化；用锁保护列表的读写（管理 API 写、调度器读）。
/// </summary>
public sealed class WebhookState
{
    private readonly object _gate = new();
    private readonly List<WebhookSubscription> _subs = [];

    /// <summary>返回订阅快照（调用方可安全遍历而不持有锁）。</summary>
    public IReadOnlyList<WebhookSubscription> Snapshot()
    {
        lock (_gate) return _subs.ToList();
    }

    public WebhookSubscription? Get(string id)
    {
        lock (_gate) return _subs.FirstOrDefault(s => s.Id == id);
    }

    public void Add(WebhookSubscription sub)
    {
        lock (_gate) _subs.Add(sub);
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var i = _subs.FindIndex(s => s.Id == id);
            if (i < 0) return false;
            _subs.RemoveAt(i);
            return true;
        }
    }

    public void ReplaceAll(IEnumerable<WebhookSubscription> subs)
    {
        lock (_gate)
        {
            _subs.Clear();
            _subs.AddRange(subs);
        }
    }

    /// <summary>标记某订阅最近一次成功投递时间（供列表展示，节流由调用方决定）。</summary>
    public void MarkDelivered(string id, long atMs)
    {
        lock (_gate)
        {
            var s = _subs.FirstOrDefault(x => x.Id == id);
            if (s is not null) s.LastDeliveredAt = atMs;
        }
    }
}

/// <summary>
/// 出站 Webhook（对外集成）：管理员配置回调地址 + 事件订阅，知聚里的事件会以带 HMAC 签名的
/// HTTP POST 推送到外部系统（n8n / 自建服务 / 低代码平台…）。
///   GET    /ag-ui/admin/webhooks              —— 列出订阅（密钥脱敏）
///   POST   /ag-ui/admin/webhooks              —— 新建订阅（返回一次性明文密钥）
///   PUT    /ag-ui/admin/webhooks/{id}         —— 更新订阅（可轮换密钥）
///   DELETE /ag-ui/admin/webhooks/{id}         —— 删除订阅
///   POST   /ag-ui/admin/webhooks/{id}/test    —— 发送一条测试投递
///   GET    /ag-ui/admin/webhooks/events       —— 可选事件类型目录（供前端勾选）
///   GET    /ag-ui/admin/webhooks/deliveries   —— 最近投递记录
/// 全部仅系统管理员可用。
/// </summary>
public static class WebhookApi
{
    /// <summary>可选事件类型目录（集成价值较高的「粗粒度」事件；高频流式增量不在列）。</summary>
    public static readonly string[] EventCatalog =
    [
        EventTypes.GroupCreated, EventTypes.GroupUpdated, EventTypes.GroupDisbanded,
        EventTypes.GroupMemberJoined, EventTypes.GroupMemberLeft, EventTypes.GroupMemberUpdated,
        EventTypes.TextMessageStart, EventTypes.TextMessageEnd, EventTypes.TextMessageAttachments,
        EventTypes.GroupMessageRecalled,
        EventTypes.AgentInteractionRequest, EventTypes.AgentInteractionResolved,
        EventTypes.ToolCallStart, EventTypes.ToolCallResult,
        EventTypes.GroupTopicCreated, EventTypes.GroupTopicDeleted, EventTypes.GroupTopicCleared,
        EventTypes.RunError,
        EventTypes.QuotaThreshold, // 1.0.270：用量跨过 80% / 100% 的告警（个人 / 知聚两作用域）
        EventTypes.QuotaOverrideExpiring, // 1.0.274：临时放额即将到期（提前 24 小时，可配）
        EventTypes.QuotaOverrideExpired, // 1.0.275：临时放额已到期并回退全局
        EventTypes.ImageWritePending, // 1.0.281：待审批的技能图库写入
        EventTypes.ImageWriteResolved, // 1.0.282：待审批写入已处理（批准 / 拒绝 / 失败）
        EventTypes.ArtifactQualityAlert, // 1.0.345：技能产物质量连续低分
        EventTypes.ArtifactSkillSuspended, // 1.0.345：技能因产物质量被暂停挂载
        EventTypes.SkillLoopAlert, // 1.0.359：技能自愈反复失败 / 回滚
    ];

    public static void MapWebhookApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/admin/webhooks");

        // ---- 列出订阅（密钥脱敏：只回 hasSecret，不回明文）----
        root.MapGet("/", (WebhookState state) => Results.Ok(state.Snapshot()
            .Select(s => new
            {
                s.Id, s.Name, s.Url, s.EventTypes, s.GroupId, s.Enabled,
                s.CreatedAt, s.CreatedBy, s.LastDeliveredAt,
                hasSecret = !string.IsNullOrEmpty(s.Secret),
            })))
            .AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 可选事件目录 ----
        root.MapGet("/events", () => Results.Ok(EventCatalog))
            .AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 最近投递记录 ----
        root.MapGet("/deliveries", (WebhookDispatcher dispatcher) => Results.Ok(dispatcher.RecentDeliveries()))
            .AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 新建订阅 ----
        root.MapPost("/", (WebhookHttpRequest req, HttpContext ctx, AuthService auth,
            WebhookState state, ChangeHub changes, AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var (error, url) = ValidateUrl(req.Url);
            if (error is not null) return error;

            var name = (req.Name ?? "").Trim();
            if (name.Length > 60) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "订阅名过长（≤60 字符）"));

            var events = NormalizeEvents(req.EventTypes);
            if (events.Error is not null) return events.Error;

            var secret = string.IsNullOrWhiteSpace(req.Secret) ? GenerateSecret() : req.Secret.Trim();
            if (secret.StartsWith("whsec_", StringComparison.Ordinal) is false && req.Secret is not null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "签名密钥需以 whsec_ 开头"));

            var sub = new WebhookSubscription
            {
                Id = "wh_" + IdGenerator.NewId(),
                Name = name,
                Url = url!,
                Secret = secret,
                EventTypes = events.Types,
                GroupId = string.IsNullOrWhiteSpace(req.GroupId) ? null : req.GroupId.Trim(),
                Enabled = req.Enabled ?? true,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CreatedBy = me,
            };
            state.Add(sub);
            changes.Notify();

            audit.Record("webhook.create", me, auth.GetUser(me)?.Username, targetType: "webhook", targetId: sub.Id,
                detail: $"新建 Webhook 订阅 {sub.Url}");
            // 明文密钥只在创建时返回一次（列表接口脱敏）
            return Results.Ok(new { sub.Id, sub.Name, sub.Url, sub.EventTypes, sub.GroupId, sub.Enabled, secret });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 更新订阅 ----
        root.MapPut("/{id}", (string id, WebhookHttpRequest req, HttpContext ctx, AuthService auth,
            WebhookState state, ChangeHub changes, AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var sub = state.Get(id);
            if (sub is null) return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "订阅不存在"));

            string? rotatedSecret = null;
            if (req.Url is not null)
            {
                var (error, url) = ValidateUrl(req.Url);
                if (error is not null) return error;
                sub.Url = url!;
            }
            if (req.Name is not null)
            {
                var name = req.Name.Trim();
                if (name.Length > 60) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "订阅名过长（≤60 字符）"));
                sub.Name = name;
            }
            if (req.EventTypes is not null)
            {
                var events = NormalizeEvents(req.EventTypes);
                if (events.Error is not null) return events.Error;
                sub.EventTypes = events.Types;
            }
            if (req.GroupId is not null)
                sub.GroupId = string.IsNullOrWhiteSpace(req.GroupId) ? null : req.GroupId.Trim();
            if (req.Enabled is not null)
                sub.Enabled = req.Enabled.Value;
            if (req.RotateSecret == true)
            {
                sub.Secret = GenerateSecret();
                rotatedSecret = sub.Secret;
            }
            changes.Notify();

            audit.Record("webhook.update", me, auth.GetUser(me)?.Username, targetType: "webhook", targetId: sub.Id,
                detail: $"更新 Webhook 订阅 {sub.Url}");
            return Results.Ok(new
            {
                sub.Id, sub.Name, sub.Url, sub.EventTypes, sub.GroupId, sub.Enabled,
                // 仅在本次轮换时返回新密钥
                secret = rotatedSecret,
            });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 删除订阅 ----
        root.MapDelete("/{id}", (string id, HttpContext ctx, AuthService auth,
            WebhookState state, ChangeHub changes, AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            if (!state.Remove(id)) return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "订阅不存在"));
            changes.Notify();
            audit.Record("webhook.delete", me, auth.GetUser(me)?.Username, targetType: "webhook", targetId: id,
                detail: "删除 Webhook 订阅");
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 测试投递 ----
        root.MapPost("/{id}/test", async (string id, WebhookState state, WebhookDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var sub = state.Get(id);
            if (sub is null) return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "订阅不存在"));
            var record = await dispatcher.SendTestAsync(sub, ct);
            return Results.Ok(record);
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());
    }

    /// <summary>URL 校验：必须是 http / https 绝对地址。</summary>
    private static (IResult? Error, string? Url) ValidateUrl(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return (Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "回调地址不能为空")), null);
        if (u.Length > 2000) return (Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "回调地址过长")), null);
        if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return (Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "回调地址需为 http / https 绝对 URL")), null);
        return (null, parsed.ToString());
    }

    /// <summary>事件类型规约：去掉空白 / 重复；非目录内类型直接拒绝（避免订阅到永不会发生的事件）。</summary>
    private static (IResult? Error, List<string> Types) NormalizeEvents(IReadOnlyList<string>? events)
    {
        if (events is null) return (null, []);
        var types = events.Select(e => (e ?? "").Trim()).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var unknown = types.Where(t => !EventCatalog.Contains(t, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            return (Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"未知事件类型：{string.Join(", ", unknown)}")), []);
        return (null, types);
    }

    /// <summary>生成签名密钥：<c>whsec_</c> + 32 字节 base64url。</summary>
    public static string GenerateSecret()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "whsec_" + b64;
    }

    /// <summary>注册 Webhook 订阅到持久化扩展区「webhooks」：重启后恢复。密钥落盘时经 SecretVault 加密。</summary>
    public static void RegisterWebhookPersistence(this IServiceProvider services)
    {
        var state = services.GetRequiredService<WebhookState>();
        var vault = services.GetService<SecretVault>(); // 静态加固（可选）：签名密钥落盘时加密

        Func<object?> snapshot = () =>
        {
            var subs = state.Snapshot().Select(s => new WebhookSubscription
            {
                Id = s.Id,
                Name = s.Name,
                Url = s.Url,
                // 仅加密敏感字段：Secret 落盘前加密，内存态保持明文供签名使用
                Secret = vault?.Encrypt(s.Secret) ?? s.Secret,
                EventTypes = s.EventTypes,
                GroupId = s.GroupId,
                Enabled = s.Enabled,
                CreatedAt = s.CreatedAt,
                CreatedBy = s.CreatedBy,
                LastDeliveredAt = s.LastDeliveredAt,
            }).ToList();
            return subs;
        };
        Action<JsonElement> restore = element =>
        {
            var saved = element.Deserialize<List<WebhookSubscription>>(AguiJson.Options) ?? [];
            foreach (var s in saved)
            {
                // 兼容旧版明文；新写入为加密值，解密回明文供签名使用
                s.Secret = vault?.Decrypt(s.Secret) ?? s.Secret;
            }
            state.ReplaceAll(saved);
        };

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
            persistence.AddSection("webhooks", snapshot, restore);
        else
            services.GetService<ISectionStore>()?.AddSection("webhooks", snapshot, restore);
    }
}

/// <summary>Webhook 订阅的新建 / 更新请求体（更新时字段为空表示不变更）。</summary>
public sealed record WebhookHttpRequest(
    string? Name,
    string? Url,
    string[]? EventTypes,
    string? GroupId,
    bool? Enabled,
    bool? RotateSecret,
    string? Secret);
