using System.Text.Json;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 白标 / 品牌化（6.4）：应用名 + Logo + 品牌主色，供登录页与顶栏展示、以及 iframe 嵌入时统一品牌观感。
///   GET  /ag-ui/settings/branding —— 公开：返回品牌配置（登录前 / 嵌入页据此渲染）
///   POST /ag-ui/settings/branding —— 管理员：保存品牌配置（持久化到扩展区「branding」）
/// 主题由前端以 CSS 变量注入（覆盖 --accent / --accent-text / --agent），无需改后端模板。
/// </summary>
public sealed class BrandingState
{
    /// <summary>是否显式保存过（未保存时前端用默认「知聚(KnowGath)」）。</summary>
    public bool IsConfigured { get; set; }

    /// <summary>产品名（登录页 / 顶栏显示）。</summary>
    public string AppName { get; set; } = "";

    /// <summary>Logo URL（可为本站 /ag-ui/files/... 上传地址或外部图片，前端过 scheme 白名单）。</summary>
    public string? LogoUrl { get; set; }

    /// <summary>品牌主色（hex，如 #4f8cff）。前端解析为亮 / 暗两套 --accent 并派生 --accent-text / --agent。</summary>
    public string PrimaryColor { get; set; } = "";

    /// <summary>强制深色模式（白标常用：嵌入 / 门户统一观感时默认深色且不可切换）。空 = 跟随用户选择。</summary>
    public bool? ForceDark { get; set; }

    /// <summary>嵌入页横幅文案（可选，展示在登录卡片顶部）。</summary>
    public string? Tagline { get; set; }
}

public static class BrandingApi
{
    public static void MapBrandingApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/settings");

        // ---- 查询品牌配置：公开（登录前 / 嵌入页渲染均需）----
        root.MapGet("/branding", (BrandingState branding) =>
            Results.Ok(new
            {
                configured = branding.IsConfigured,
                appName = string.IsNullOrWhiteSpace(branding.AppName) ? "知聚(KnowGath)" : branding.AppName,
                logoUrl = branding.LogoUrl,
                primaryColor = branding.PrimaryColor,
                forceDark = branding.ForceDark,
                tagline = branding.Tagline,
            }));

        // ---- 保存品牌配置：仅系统管理员（品牌信息影响全站展示）----
        root.MapPost("/branding", (BrandingHttpRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions,
            BrandingState branding, ChangeHub changes, AuditLogService audit) =>
        {
            var (meId, error) = WebIdentity.RequireAdmin(ctx, auth, authOptions);
            if (error is not null) return error;
            var me = meId!; // RequireAdmin 保证 error 为空时 meId 非空

            var appName = (req.AppName ?? "").Trim();
            if (appName.Length > 40) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "应用名过长（≤40 字符）"));

            var primary = (req.PrimaryColor ?? "").Trim().TrimStart('#');
            if (primary.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(primary, @"^[0-9a-fA-F]{6}$"))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "品牌主色需为 6 位 hex，如 #4f8cff"));

            // logoUrl 过 scheme 白名单：只允许站内相对路径 / https / data:image（防 javascript: 等）
            var logo = string.IsNullOrWhiteSpace(req.LogoUrl) ? null : req.LogoUrl.Trim();
            if (logo is not null && !IsSafeLogoUrl(logo))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "Logo URL 仅允许站内路径、https 或 data:image"));

            branding.IsConfigured = true;
            branding.AppName = appName;
            branding.LogoUrl = logo;
            branding.PrimaryColor = primary.Length > 0 ? "#" + primary.ToLowerInvariant() : "";
            branding.ForceDark = req.ForceDark;
            branding.Tagline = string.IsNullOrWhiteSpace(req.Tagline) ? null : req.Tagline.Trim();
            changes.Notify(); // 驱动持久化

            audit.Record("settings.branding", me, auth.GetUser(me)?.Username, detail: "修改白标品牌配置");
            return Results.Ok(new { ok = true, appName = branding.AppName, primaryColor = branding.PrimaryColor });
        });
    }

    /// <summary>Logo URL scheme 白名单：站内相对路径、https、data:image/（防 javascript: 存储型 XSS）。</summary>
    private static bool IsSafeLogoUrl(string url)
    {
        var u = url.Trim();
        if (u.StartsWith("/") && !u.StartsWith("//")) return true;
        return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>注册白标配置到持久化扩展区「branding」：重启后恢复自定义品牌。须在 InitializePersistence 前调用。</summary>
    public static void RegisterBrandingPersistence(this IServiceProvider services)
    {
        var branding = services.GetRequiredService<BrandingState>();
        Func<object?> snapshot = () => branding;
        Action<JsonElement> restore = element =>
        {
            var saved = element.Deserialize<BrandingState>(AguiJson.Options);
            if (saved is null) return;
            branding.IsConfigured = saved.IsConfigured;
            branding.AppName = saved.AppName;
            branding.LogoUrl = saved.LogoUrl;
            branding.PrimaryColor = saved.PrimaryColor;
            branding.ForceDark = saved.ForceDark;
            branding.Tagline = saved.Tagline;
        };

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
        {
            persistence.AddSection("branding", snapshot, restore);
        }
        else
        {
            services.GetService<ISectionStore>()?.AddSection("branding", snapshot, restore);
        }
    }
}

/// <summary>白标品牌配置保存请求体（AppName 空 = 重置为默认名；PrimaryColor 空 = 默认主色）。</summary>
public sealed record BrandingHttpRequest(string? AppName, string? LogoUrl, string? PrimaryColor, bool? ForceDark, string? Tagline);
