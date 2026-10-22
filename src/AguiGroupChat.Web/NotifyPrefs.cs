using System.Text.Json;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Web;

/// <summary>
/// 按用户的**通知偏好**（1.0.318）：目前只有「浮窗范围」——哪些通知类型弹系统通知 / 桌面浮窗。
/// 存在扩展区 <c>notifyPrefs</c> 里，多设备登录后同一账号口径一致；前端仍保留 <c>localStorage</c> 作本地缓存。
///
/// <para>语义：该用户没有记录 / 空列表 = 全部类型都弹（与历史行为一致）；否则只弹列表内的类型。</para>
/// </summary>
public sealed class NotifyPrefsState
{
    /// <summary>userId → 允许弹浮窗的类型集合。键不存在 = 从未设置（全部弹）；空列表 = 明确“都不弹”。</summary>
    public Dictionary<string, List<string>> ToastTypes { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>通知偏好接口（1.0.318）：<c>GET/PUT /ag-ui/notify-prefs</c>（需登录，只读写当前用户）。</summary>
public static class NotifyPrefsApi
{
    /// <summary>与前端通知中心的类型一致；未知值拒绝，避免配置漂移。</summary>
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "mention", "approval", "error", "message", "other",
    };

    public static void MapNotifyPrefsApi(this WebApplication app)
    {
        app.MapGet("/ag-ui/notify-prefs", (HttpContext ctx, NotifyPrefsState state) =>
        {
            var uid = WebIdentity.UserId(ctx);
            if (string.IsNullOrEmpty(uid)) return Results.Empty;
            // 键不存在（从未设置）→ null（前端按“全部弹”处理）；空数组 = 明确“都不弹”（1.0.318）
            var list = state.ToastTypes.TryGetValue(uid, out var v) ? v : null;
            return Results.Ok(new { toastTypes = list });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        app.MapPut("/ag-ui/notify-prefs", (NotifyPrefsHttpRequest req, HttpContext ctx,
            NotifyPrefsState state, ChangeHub changes) =>
        {
            var uid = WebIdentity.UserId(ctx)!;
            var cleaned = (req.ToastTypes ?? [])
                .Where((x) => !string.IsNullOrWhiteSpace(x))
                .Select((x) => x.Trim())
                .Where(KnownTypes.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // 空列表是有效值（“都不弹”），不能删键：删掉会退回“从未设置 = 全部弹”
            state.ToastTypes[uid] = cleaned;

            changes.Notify(); // 驱动持久化
            return Results.Ok(new { ok = true, toastTypes = cleaned });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());
    }

    /// <summary>注册到持久化扩展区「notifyPrefs」：重启后恢复；须在 InitializePersistence 前调用。</summary>
    public static void RegisterNotifyPrefsPersistence(this IServiceProvider services)
    {
        var state = services.GetRequiredService<NotifyPrefsState>();
        Func<object?> snapshot = () => state;
        Action<JsonElement> restore = element =>
        {
            var saved = element.Deserialize<NotifyPrefsState>(AguiJson.Options);
            if (saved is null) return;
            state.ToastTypes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (uid, list) in saved.ToastTypes ?? [])
            {
                // 保留空列表（“都不弹”）；只过滤未知类型
                state.ToastTypes[uid] = (list ?? []).Where(KnownTypes.Contains).Distinct(StringComparer.Ordinal).ToList();
            }
        };

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null) persistence.AddSection("notifyPrefs", snapshot, restore);
        else services.GetService<ISectionStore>()?.AddSection("notifyPrefs", snapshot, restore);
    }
}

/// <summary>保存通知偏好的请求体：空数组 = 恢复「全部类型都弹」。</summary>
public sealed record NotifyPrefsHttpRequest(IReadOnlyList<string>? ToastTypes);
