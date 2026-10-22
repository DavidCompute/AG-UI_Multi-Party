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

    // 托盘图标（1.0.301）：右键菜单、静音与关闭行为
    public static string TrayText => IsZh() ? "知聚" : "KnowGath";
    public static string TrayOpen => IsZh() ? "打开知聚" : "Open KnowGath";
    public static string TrayOpenUnread(int count) => IsZh()
        ? $"打开知聚（{count} 条未读）"
        : $"Open KnowGath ({count} unread)";
    public static string TrayTextUnread(int count, int messages, int notifications) => IsZh()
        ? (notifications > 0
            ? $"知聚 · {count} 条未读（{messages} 消息 / {notifications} 通知）"
            : $"知聚 · {count} 条未读")
        : (notifications > 0
            ? $"KnowGath · {count} unread ({messages} messages / {notifications} notifications)"
            : $"KnowGath · {count} unread");
    public static string TrayStartup => IsZh() ? "开机时自动启动" : "Start with Windows";
    public static string TrayStartupOnTitle => IsZh() ? "已开启开机启动" : "Start with Windows enabled";
    public static string TrayStartupOnBody => IsZh()
        ? "下次登录 Windows 时知聚会静默启动到托盘（可在托盘菜单关闭）。"
        : "KnowGath will start silently in the tray at your next Windows sign-in (turn it off from the tray menu).";
    public static string TrayStartupOffTitle => IsZh() ? "已关闭开机启动" : "Start with Windows disabled";
    public static string TrayStartupOffBody => IsZh()
        ? "下次登录 Windows 时不会自动启动知聚。"
        : "KnowGath will no longer start automatically at sign-in.";
    public static string TrayStartupFailed(string message) => IsZh()
        ? $"无法修改开机启动设置：{message}"
        : $"Cannot change the Start with Windows setting: {message}";

    // 任务栏缩略图按钮（1.0.305）
    public static string TaskbarOpen => IsZh() ? "打开知聚" : "Open KnowGath";
    public static string TaskbarMute => IsZh() ? "静音 1 小时" : "Mute for 1 hour";
    public static string TaskbarUnmute => IsZh() ? "取消静音" : "Unmute";
    public static string TaskbarUnread(int count) => IsZh()
        ? $"未读 {count} 条"
        : $"{count} unread";
    public static string JumpListRecent => IsZh() ? "最近知聚" : "Recent circles";
    public static string JumpListNewGroup => IsZh() ? "新建知聚" : "New circle";
    public static string JumpListAdmin => IsZh() ? "打开管理控制台" : "Open admin console";
    public static string TrayRecent => IsZh() ? "最近知聚" : "Recent circles";
    public static string TrayNewGroup => IsZh() ? "新建知聚" : "New circle";
    public static string TrayAdmin => IsZh() ? "管理控制台" : "Admin console";
    public static string TrayNoRecent => IsZh() ? "暂无最近知聚" : "No recent circles";
    // 多窗口未读明细（1.0.311）
    public static string TrayWindows => IsZh() ? "未读窗口" : "Unread windows";
    public static string TrayWindowsEmpty => IsZh() ? "暂无未读" : "Nothing unread";
    public static string TrayWindowSelf(string label) => IsZh()
        ? $"● {label}（本窗口）"
        : $"● {label} (this window)";
    public static string TrayWindowOther(string label) => IsZh()
        ? $"○ {label}"
        : $"○ {label}";
    public static string TrayWindowUntitled => IsZh() ? "未选择知聚" : "No circle selected";
    // 可交互通知浮窗（1.0.314）
    public static string ToastOpen => IsZh() ? "打开" : "Open";
    public static string ToastDismiss => IsZh() ? "忽略" : "Dismiss";
    public static string TaskbarRunning(string label) => IsZh()
        ? $"正在执行：{label}"
        : $"Running: {label}";
    public static string TrayOpenDataDir => IsZh() ? "📂 打开数据目录" : "📂 Open data folder";
    public static string TrayOpenDataDirFailed(string message) => IsZh()
        ? $"无法打开数据目录：{message}"
        : $"Cannot open the data folder: {message}";
    public static string TrayRestartBackend => IsZh() ? "🔄 重启本地服务" : "🔄 Restart local service";
    public static string TrayRestartingTitle => IsZh() ? "正在重启本地服务…" : "Restarting local service…";
    public static string TrayRestartingBody => IsZh()
        ? "窗口会短暂断开，重启完成后自动重新加载。"
        : "The window disconnects briefly and reloads automatically when the restart finishes.";
    public static string TrayRestartDoneTitle => IsZh() ? "本地服务已重启" : "Local service restarted";
    public static string TrayRestartDoneBody => IsZh() ? "页面已重新加载。" : "The page has been reloaded.";
    public static string TrayRestartFailedTitle => IsZh() ? "本地服务重启失败" : "Local service restart failed";
    public static string TrayRestartFailedBody => IsZh()
        ? "请稍后在托盘菜单重试，或退出后重新启动应用。"
        : "Try again from the tray menu later, or quit and start the app again.";
    public static string TrayMuteMenu => IsZh() ? "🔕 静音" : "🔕 Mute";
    public static string TrayMuteMenuWithRemaining(string remaining) => IsZh()
        ? $"🔕 静音（{remaining}）"
        : $"🔕 Mute ({remaining})";
    public static string TrayMute30 => IsZh() ? "30 分钟" : "30 minutes";
    public static string TrayMute60 => IsZh() ? "1 小时" : "1 hour";
    public static string TrayMute120 => IsZh() ? "2 小时" : "2 hours";
    public static string TrayMuteTomorrow => IsZh() ? "到明天 08:00" : "Until tomorrow 08:00";
    public static string TrayUnmute => IsZh() ? "🔔 取消静音" : "🔔 Unmute";
    public static string TrayExit => IsZh() ? "退出" : "Exit";
    public static string TrayMutedTitle => IsZh() ? "已静音" : "Muted";
    public static string TrayMutedBody(string remaining) => IsZh()
        ? $"静音已生效（{remaining}）。期间不再弹出桌面通知（站内通知中心与未读徽标不受影响）。"
        : $"Muted — {remaining}. Desktop notifications stay off until then (the in-app notification center and unread badge keep working).";
    public static string TrayUnmutedTitle => IsZh() ? "已恢复桌面通知" : "Desktop notifications restored";
    public static string TrayUnmutedBody => IsZh()
        ? "之后的新消息与提醒会继续弹出气泡。"
        : "New messages and alerts will pop up as balloons again.";
    public static string TrayRemainingZero => IsZh() ? "即将结束" : "ending now";
    public static string TrayRemainingMinutes(int minutes) => IsZh()
        ? $"剩余 {minutes} 分钟"
        : $"{minutes} min left";
    public static string TrayRemainingHours(int hours) => IsZh()
        ? $"剩余 {hours} 小时"
        : $"{hours} h left";
    public static string TrayRemainingHoursMinutes(int hours, int minutes) => IsZh()
        ? $"剩余 {hours} 小时 {minutes} 分钟"
        : $"{hours} h {minutes} min left";
    public static string TrayCloseToTray => IsZh() ? "关闭窗口时最小化到托盘" : "Minimize to tray when closing";
    public static string TrayCloseToTrayOnTitle => IsZh() ? "已开启：关闭到托盘" : "Close to tray enabled";
    public static string TrayCloseToTrayOnBody => IsZh()
        ? "关闭窗口后应用继续在后台运行；从托盘菜单「退出」可完全退出。"
        : "The app keeps running after you close the window; use Exit in the tray menu to quit completely.";
    public static string TrayCloseToTrayOffTitle => IsZh() ? "已关闭：关窗即退出" : "Close to tray disabled";
    public static string TrayCloseToTrayOffBody => IsZh()
        ? "关闭窗口将直接退出应用。"
        : "Closing the window now quits the app.";
    public static string TrayMinimizedTitle => IsZh() ? "已最小化到托盘" : "Minimized to tray";
    public static string TrayMinimizedBody => IsZh()
        ? "知聚仍在后台运行；双击托盘图标或右键选「打开知聚」即可回到窗口。"
        : "KnowGath is still running in the background; double-click the tray icon or choose Open KnowGath to come back.";

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
