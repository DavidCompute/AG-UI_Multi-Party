using System.Globalization;

namespace AguiGroupChat.Desktop.Cross;

/// <summary>
/// 桌面壳 UI 文案的轻量本地化（跟随系统文化，中/英）。
/// 仅覆盖外壳层的少量固定文案；WebView 中加载的 Web 前端自带其 i18n 运行时。
/// </summary>
public static class L10n
{
    private static bool IsZh()
        => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    // XAML 静态文案（供 {x:Static} 绑定）
    public static string Title => IsZh() ? "AG-UI 群聊桌面版" : "AG-UI Group Chat Desktop";
    public static string StatusStarting => IsZh() ? "正在启动本地服务…" : "Starting local service…";
    public static string OpenBrowser => IsZh() ? "🌐 浏览器打开" : "🌐 Open in Browser";
    public static string ForceReload => IsZh() ? "🔄 强制刷新" : "🔄 Force Reload";
    public static string ReloadTip => IsZh()
        ? "带时间戳重新加载，绕开缓存（保留登录状态）"
        : "Reload with a timestamp to bypass the cache (keeps you signed in)";

    // C# 运行态文案
    public static string StatusLocalService(string url) => IsZh()
        ? $"本地服务：{url}（数据与记忆全部存储在本机）"
        : $"Local service: {url} (all data & memory stored on this device)";
    public static string StatusWebviewReloaded(string url) => IsZh()
        ? $"本地服务：{url}（已强制刷新缓存）"
        : $"Local service: {url} (cache forcibly refreshed)";
    public static string StatusEmbedFallback(string error, string url) => IsZh()
        ? $"内嵌窗口不可用（{error}）——已用浏览器打开 {url}"
        : $"Embedded window unavailable ({error}) — opened {url} in the browser";
    public static string StatusBrowserFailed(string error, string url) => IsZh()
        ? $"打开浏览器失败：{error}（请手动访问 {url}）"
        : $"Failed to open browser: {error} (please visit {url} manually)";
}
