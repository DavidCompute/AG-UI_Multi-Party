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

    // 托盘图标（1.0.302）：右键菜单与静音反馈
    public static string TrayText => IsZh() ? "知聚" : "KnowGath";
    public static string TrayOpen => IsZh() ? "打开知聚" : "Open KnowGath";
    public static string TrayMuteOneHour => IsZh() ? "🔕 静音 1 小时" : "🔕 Mute for 1 hour";
    public static string TrayUnmute => IsZh() ? "🔔 取消静音" : "🔔 Unmute";
    public static string TrayExit => IsZh() ? "退出" : "Exit";
    public static string TrayMutedTitle => IsZh() ? "已静音 1 小时" : "Muted for 1 hour";
    public static string TrayMutedBody => IsZh()
        ? "未来 1 小时内不再弹出桌面通知（站内通知中心与未读徽标不受影响）。"
        : "Desktop notifications are suppressed for the next hour (the in-app notification center and unread badge keep working).";
    public static string TrayUnmutedTitle => IsZh() ? "已恢复桌面通知" : "Desktop notifications restored";
    public static string TrayUnmutedBody => IsZh()
        ? "之后的新消息与提醒会继续弹出气泡。"
        : "New messages and alerts will pop up as balloons again.";

    /// <summary>首次启动一次性提示：本机未装 LibreOffice（办公文档在线查看 / PPT 播放依赖它）。</summary>
    public static string DocPreviewHintBody => IsZh()
        ? "本机未检测到 LibreOffice，办公文档（PPT / Word / Excel）的在线查看与播放将不可用——\n" +
          "附件仍可下载后用本地应用打开。\n\n" +
          "如需启用：安装 LibreOffice（https://www.libreoffice.org），\n" +
          "或设置环境变量 AGUI_SOFFICE 指向 soffice.exe 后重启本应用。\n\n" +
          "安装后可在「管理员 → 系统状态 → 文档在线转换」确认是否就绪。此提示仅显示一次。"
        : "LibreOffice was not found on this machine, so online view and playback of office documents (PPT / Word / Excel) will be unavailable —\n" +
          "attachments can still be downloaded and opened with a local app.\n\n" +
          "To enable it: install LibreOffice (https://www.libreoffice.org),\n" +
          "or set the AGUI_SOFFICE environment variable to soffice.exe and restart this app.\n\n" +
          "After installing, check \"Admin → System status → Document conversion\". This notice is shown only once.";
    public static string WebviewRuntimeBody(string message) => IsZh()
        ? "无法初始化 WebView2（Chromium Edge WebView2 Runtime）。\n" +
          "请安装 Microsoft Edge WebView2 Runtime（Windows 10/11 通常已内置）。\n\n" + message
        : "Cannot initialize WebView2 (Chromium Edge WebView2 Runtime).\n" +
          "Please install Microsoft Edge WebView2 Runtime (usually built into Windows 10/11).\n\n" + message;
}
