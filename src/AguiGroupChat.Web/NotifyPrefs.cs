using System.Text.Json;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Web;

/// <summary>单个用户的通知偏好（1.0.318 起存浮窗范围；1.0.319 扩展为规则）。</summary>
public sealed class NotifyUserPrefs
{
    /// <summary>允许弹浮窗的类型；<c>null</c> = 从未设置 / 全部弹；空数组 = 都不弹。</summary>
    public List<string>? ToastTypes { get; set; }

    /// <summary>这些知聚的通知不弹浮窗（仍进通知中心）。</summary>
    public List<string>? MuteGroups { get; set; }

    /// <summary>这些发送者（成员 id）的通知不弹浮窗。</summary>
    public List<string>? MuteSenders { get; set; }

    /// <summary>标题或正文包含任一关键词时不弹浮窗（不区分大小写）。</summary>
    public List<string>? MuteKeywords { get; set; }

    /// <summary>自定义模板（1.0.321）：一键套用的一组范围 + 规则。</summary>
    public List<NotifyPreset>? Presets { get; set; }
}

/// <summary>自定义通知模板（1.0.321）。</summary>
public sealed class NotifyPreset
{
    public string? Name { get; set; }
    public List<string>? ToastTypes { get; set; }
    public List<string>? MuteGroups { get; set; }
    public List<string>? MuteSenders { get; set; }
    public List<string>? MuteKeywords { get; set; }
}

/// <summary>
/// 按用户的**通知偏好**（1.0.318）：哪些类型弹系统通知 / 桌面浮窗；1.0.319 起再加
/// 「按知聚 / 按发送者 / 按关键词」的免打扰规则。存扩展区 <c>notifyPrefs</c>，多设备登录后口径一致；
/// 前端仍保留 <c>localStorage</c> 作本地缓存。
/// </summary>
public sealed class NotifyPrefsState
{
    /// <summary>userId → 偏好。</summary>
    public Dictionary<string, NotifyUserPrefs> Users { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>通知偏好接口（1.0.318/319）：<c>GET/PUT /ag-ui/notify-prefs</c>（需登录，只读写当前用户）。</summary>
public static class NotifyPrefsApi
{
    /// <summary>与前端通知中心的类型一致；未知值拒绝，避免配置漂移。</summary>
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "mention", "approval", "error", "message", "other",
    };

    private const int MaxListItems = 100;
    private const int MaxValueChars = 64;

    public static void MapNotifyPrefsApi(this WebApplication app)
    {
        app.MapGet("/ag-ui/notify-prefs", (HttpContext ctx, NotifyPrefsState state) =>
        {
            var uid = WebIdentity.UserId(ctx);
            if (string.IsNullOrEmpty(uid)) return Results.Empty;
            state.Users.TryGetValue(uid, out var prefs);
            return Results.Ok(new
            {
                // null = 从未设置（前端按“全部弹”处理）；空数组 = 明确“都不弹”
                toastTypes = prefs?.ToastTypes,
                muteGroups = prefs?.MuteGroups ?? [],
                muteSenders = prefs?.MuteSenders ?? [],
                muteKeywords = prefs?.MuteKeywords ?? [],
                presets = prefs?.Presets ?? [],
            });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        app.MapPut("/ag-ui/notify-prefs", (NotifyPrefsHttpRequest req, HttpContext ctx,
            NotifyPrefsState state, ChangeHub changes) =>
        {
            var uid = WebIdentity.UserId(ctx)!;
            var prefs = state.Users.TryGetValue(uid, out var existing) ? existing : new NotifyUserPrefs();

            // ToastTypes 显式传 null = 恢复“全部弹”；传数组（含空）= 按此执行
            if (req.ToastTypes is not null)
                prefs.ToastTypes = Clean(req.ToastTypes, KnownTypes.Contains);
            if (req.MuteGroups is not null)
                prefs.MuteGroups = Clean(req.MuteGroups);
            if (req.MuteSenders is not null)
                prefs.MuteSenders = Clean(req.MuteSenders);
            if (req.MuteKeywords is not null)
                prefs.MuteKeywords = Clean(req.MuteKeywords);
            if (req.Presets is not null)
                prefs.Presets = CleanPresets(req.Presets);

            state.Users[uid] = prefs;
            changes.Notify(); // 驱动持久化
            return Results.Ok(new
            {
                ok = true,
                toastTypes = prefs.ToastTypes,
                muteGroups = prefs.MuteGroups ?? [],
                muteSenders = prefs.MuteSenders ?? [],
                muteKeywords = prefs.MuteKeywords ?? [],
                presets = prefs.Presets ?? [],
            });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());
    }

    /// <summary>去空白 / 去重 / 截断长度 / 限条数；可选附加白名单过滤。</summary>
    private static List<string> Clean(IReadOnlyList<string>? values, Func<string, bool>? allow = null)
        => (values ?? [])
            .Where((x) => !string.IsNullOrWhiteSpace(x))
            .Select((x) => x.Trim())
            .Where((x) => x.Length <= MaxValueChars && (allow is null || allow(x)))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxListItems)
            .ToList();

    /// <summary>模板清理（1.0.321）：名称 ≤ 40 字、最多 10 个、各字段同样清洗。</summary>
    private static List<NotifyPreset> CleanPresets(IReadOnlyList<NotifyPreset>? presets)
        => (presets ?? [])
            .Where((p) => p is not null && !string.IsNullOrWhiteSpace(p.Name))
            .Select((p) => new NotifyPreset
            {
                Name = p.Name!.Trim().Length <= 40 ? p.Name!.Trim() : p.Name!.Trim()[..40],
                ToastTypes = p.ToastTypes is null ? null : Clean(p.ToastTypes, KnownTypes.Contains),
                MuteGroups = Clean(p.MuteGroups),
                MuteSenders = Clean(p.MuteSenders),
                MuteKeywords = Clean(p.MuteKeywords),
            })
            .Take(10)
            .ToList();

    /// <summary>注册到持久化扩展区「notifyPrefs」：重启后恢复；兼容 1.0.318 的「userId → 类型数组」旧格式。</summary>
    public static void RegisterNotifyPrefsPersistence(this IServiceProvider services)
    {
        var state = services.GetRequiredService<NotifyPrefsState>();
        Func<object?> snapshot = () => state;
        Action<JsonElement> restore = element => Restore(state, element);

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null) persistence.AddSection("notifyPrefs", snapshot, restore);
        else services.GetService<ISectionStore>()?.AddSection("notifyPrefs", snapshot, restore);
    }

    /// <summary>恢复（1.0.332 修复）：快照形如 <c>{"users":{…}}</c>，先解包再按 userId 循环；
    /// 也兼容根直接就是 userId map 的旧快照。</summary>
    public static void Restore(NotifyPrefsState state, JsonElement element)
    {
        state.Users = new Dictionary<string, NotifyUserPrefs>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) return;
        var map = element.TryGetProperty("users", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : element;
        foreach (var prop in map.EnumerateObject())
        {
            try
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    // 旧格式：直接是 toastTypes 数组
                    var list = prop.Value.Deserialize<List<string>>(AguiJson.Options) ?? [];
                    state.Users[prop.Name] = new NotifyUserPrefs { ToastTypes = Clean(list, KnownTypes.Contains) };
                    continue;
                }
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var prefs = prop.Value.Deserialize<NotifyUserPrefs>(AguiJson.Options);
                if (prefs is null) continue;
                state.Users[prop.Name] = new NotifyUserPrefs
                {
                    ToastTypes = prefs.ToastTypes is null ? null : Clean(prefs.ToastTypes, KnownTypes.Contains),
                    MuteGroups = Clean(prefs.MuteGroups),
                    MuteSenders = Clean(prefs.MuteSenders),
                    MuteKeywords = Clean(prefs.MuteKeywords),
                    Presets = CleanPresets(prefs.Presets),
                };
            }
            catch { /* 单条损坏：跳过 */ }
        }
    }
}

/// <summary>保存通知偏好的请求体；未传的字段 = 不改，<c>ToastTypes</c> 传空数组 = 都不弹。
/// （构造参数带默认值：否则 System.Text.Json 会把缺失字段当成必填而 400。）</summary>
public sealed record NotifyPrefsHttpRequest(
    IReadOnlyList<string>? ToastTypes = null,
    IReadOnlyList<string>? MuteGroups = null,
    IReadOnlyList<string>? MuteSenders = null,
    IReadOnlyList<string>? MuteKeywords = null,
    IReadOnlyList<NotifyPreset>? Presets = null);
