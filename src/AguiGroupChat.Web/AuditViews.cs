using System.Text.Json;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Web;

/// <summary>一条审计筛选视图（1.0.328）：与管理控制台「工作区」页的筛选控件一一对应。</summary>
public sealed class AuditViewDefinition
{
    public string? Name { get; set; }
    public int Days { get; set; } = 7;
    public string? Q { get; set; }
    public string? GroupId { get; set; }
    public string? AgentId { get; set; }
    public string? Tool { get; set; }
    public string? Date { get; set; }
    public bool GroupByMsg { get; set; }
}

/// <summary>单个用户的筛选视图集合（1.0.329 起带默认视图）。</summary>
public sealed class AuditViewsUser
{
    public List<AuditViewDefinition>? Views { get; set; }

    /// <summary>默认视图名（1.0.329）：打开「工作区」页时自动套用；为空 = 不自动套用。</summary>
    public string? DefaultView { get; set; }
}

/// <summary>
/// 按用户的**审计筛选视图**（1.0.328）：存扩展区 <c>auditViews</c>，换设备登录后一致；
/// 前端仍保留 <c>localStorage</c> 作离线缓存。
/// </summary>
public sealed class AuditViewsState
{
    /// <summary>userId → 视图集合。</summary>
    public Dictionary<string, AuditViewsUser> Users { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>筛选视图接口（1.0.328；1.0.329 加默认视图）：<c>GET/PUT /ag-ui/audit-views</c>（需登录，只读写当前用户）。</summary>
public static class AuditViewsApi
{
    private const int MaxViews = 20;
    private const int MaxNameChars = 30;
    private const int MaxQueryChars = 200;
    private const int MaxIdChars = 128;

    private static readonly HashSet<string> KnownTools = new(StringComparer.Ordinal)
    {
        "", "command", "write", "read", "list", "other",
    };

    public static void MapAuditViewsApi(this WebApplication app)
    {
        app.MapGet("/ag-ui/audit-views", (HttpContext ctx, AuditViewsState state) =>
        {
            var uid = WebIdentity.UserId(ctx);
            if (string.IsNullOrEmpty(uid)) return Results.Empty;
            state.Users.TryGetValue(uid!, out var entry);
            return Results.Ok(new
            {
                views = entry?.Views ?? [],
                defaultView = entry?.DefaultView ?? "",
            });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        app.MapPut("/ag-ui/audit-views", (AuditViewsHttpRequest req, HttpContext ctx,
            AuditViewsState state, ChangeHub changes) =>
        {
            var uid = WebIdentity.UserId(ctx)!;
            var views = Clean(req.Views);
            var defaultView = CleanDefault(req.DefaultView, views);
            state.Users[uid] = new AuditViewsUser { Views = views, DefaultView = defaultView };
            changes.Notify(); // 驱动持久化
            return Results.Ok(new { ok = true, views, defaultView });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());
    }

    /// <summary>清洗：名称必填、天数 1–90、工具白名单、日期 yyyy-MM-dd、上限 20 条（同名后者覆盖）。</summary>
    public static List<AuditViewDefinition> Clean(IReadOnlyList<AuditViewDefinition>? views)
    {
        var result = new List<AuditViewDefinition>();
        foreach (var v in views ?? [])
        {
            var name = v?.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (name!.Length > MaxNameChars) name = name[..MaxNameChars];
            var tool = (v!.Tool ?? "").Trim();
            if (!KnownTools.Contains(tool)) tool = "";
            var date = (v.Date ?? "").Trim();
            if (date.Length > 0 && !DateTime.TryParseExact(date, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _)) date = "";
            var item = new AuditViewDefinition
            {
                Name = name,
                Days = Math.Clamp(v.Days <= 0 ? 7 : v.Days, 1, 90),
                Q = Clip(v.Q, MaxQueryChars),
                GroupId = Clip(v.GroupId, MaxIdChars),
                AgentId = Clip(v.AgentId, MaxIdChars),
                Tool = tool,
                Date = date,
                GroupByMsg = v.GroupByMsg,
            };
            var idx = result.FindIndex((x) => string.Equals(x.Name, name, StringComparison.Ordinal));
            if (idx >= 0) result[idx] = item;
            else result.Add(item);
        }
        return result.Take(MaxViews).ToList();
    }

    /// <summary>默认视图清洗（1.0.329）：名称必须存在于当前视图列表，否则清空。</summary>
    public static string CleanDefault(string? name, IReadOnlyList<AuditViewDefinition> views)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "";
        return views.Any((v) => string.Equals(v.Name, trimmed, StringComparison.Ordinal)) ? trimmed : "";
    }

    private static string Clip(string? value, int max)
    {
        var s = (value ?? "").Trim();
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>注册到持久化扩展区「auditViews」：重启后恢复；兼容 1.0.328 的「userId → 数组」旧格式。</summary>
    public static void RegisterAuditViewsPersistence(this IServiceProvider services)
    {
        var state = services.GetRequiredService<AuditViewsState>();
        Func<object?> snapshot = () => state;
        Action<JsonElement> restore = element =>
        {
            state.Users = new Dictionary<string, AuditViewsUser>(StringComparer.Ordinal);
            if (element.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in element.EnumerateObject())
            {
                try
                {
                    // 旧格式（1.0.328）：直接是视图数组
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var old = prop.Value.Deserialize<List<AuditViewDefinition>>(AguiJson.Options);
                        state.Users[prop.Name] = new AuditViewsUser { Views = Clean(old), DefaultView = "" };
                        continue;
                    }
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    var entry = prop.Value.Deserialize<AuditViewsUser>(AguiJson.Options);
                    if (entry is null) continue;
                    var views = Clean(entry.Views);
                    state.Users[prop.Name] = new AuditViewsUser
                    {
                        Views = views,
                        DefaultView = CleanDefault(entry.DefaultView, views),
                    };
                }
                catch { /* 单条损坏：跳过 */ }
            }
        };

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null) persistence.AddSection("auditViews", snapshot, restore);
        else services.GetService<ISectionStore>()?.AddSection("auditViews", snapshot, restore);
    }
}

/// <summary>保存筛选视图的请求体（1.0.328；1.0.329 加 defaultView）：整体替换当前用户的视图列表。
/// （构造参数带默认值：缺失字段被 System.Text.Json 当成必填会 400。）</summary>
public sealed record AuditViewsHttpRequest(
    IReadOnlyList<AuditViewDefinition>? Views = null,
    string? DefaultView = null);
