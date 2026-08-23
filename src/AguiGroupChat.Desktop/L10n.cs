using System.Globalization;

namespace AguiGroupChat.Desktop;

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
    public static string Home => IsZh() ? "🏠 首页" : "🏠 Home";
    public static string ForceReload => IsZh() ? "🔄 强制刷新" : "🔄 Force Reload";
    public static string ReloadTip => IsZh()
        ? "清缓存后重载页面（保留登录状态）；也可用 F5 / Ctrl+R"
        : "Reload after clearing cache (keeps you signed in); also F5 / Ctrl+R";

    // C# 运行态文案（含格式化占位）
    public static string StatusLocalService(string url) => IsZh()
        ? $"本地服务：{url}（数据与记忆全部存储在本机）"
        : $"Local service: {url} (all data & memory stored on this device)";
    public static string StatusWebviewReloaded(string url) => IsZh()
        ? $"本地服务：{url}（已强制刷新缓存）"
        : $"Local service: {url} (cache forcibly refreshed)";
    public static string WebviewInitFailed(string message) => IsZh()
        ? $"WebView2 初始化失败：{message}"
        : $"WebView2 initialization failed: {message}";

    public static string MessageBoxTitle => IsZh() ? "AG-UI 桌面版" : "AG-UI Desktop";
    public static string WebviewRuntimeBody(string message) => IsZh()
        ? "无法初始化 WebView2（Chromium Edge WebView2 Runtime）。\n" +
          "请安装 Microsoft Edge WebView2 Runtime（Windows 10/11 通常已内置）。\n\n" + message
        : "Cannot initialize WebView2 (Chromium Edge WebView2 Runtime).\n" +
          "Please install Microsoft Edge WebView2 Runtime (usually built into Windows 10/11).\n\n" + message;
}
