using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using MessageBox = System.Windows.MessageBox;

namespace AguiGroupChat.Desktop;

public partial class MainWindow : Window
{
    private readonly string _baseUrl;
    private readonly InstanceCoordinator _coordinator;
    private readonly DesktopSettings _settings = DesktopSettings.Load();

    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private System.Windows.Forms.ToolStripMenuItem? _openItem;
    private System.Windows.Forms.ToolStripMenuItem? _muteMenu;
    private System.Windows.Forms.ToolStripMenuItem? _mute30;
    private System.Windows.Forms.ToolStripMenuItem? _mute60;
    private System.Windows.Forms.ToolStripMenuItem? _mute120;
    private System.Windows.Forms.ToolStripMenuItem? _muteTomorrow;
    private System.Windows.Forms.ToolStripMenuItem? _unmuteItem;
    private System.Windows.Forms.ToolStripMenuItem? _closeToTrayItem;
    private System.Windows.Forms.ToolStripMenuItem? _startupItem;
    private System.Windows.Forms.ToolStripMenuItem? _restartItem;

    private System.Drawing.Icon? _baseIcon;
    private System.Drawing.Icon? _badgeIcon;
    private System.Drawing.Icon? _openThumbIcon;
    private System.Drawing.Icon? _muteThumbIcon;
    private int _unreadCount;
    private int _unreadMessages;
    private int _unreadNotifications;
    private string _progressState = "none";
    private ulong _progressValue;
    private ulong _progressTotal;
    private string _recentsSignature = "";
    private readonly TaskbarThumbnailBar _taskbar = new();
    private readonly JumpListManager _jumpList = new();

    private readonly bool _startHidden;
    private readonly string? _initialGroupId;

    private DateTime _mutedUntilUtc = DateTime.MinValue;
    private string? _mutePreset;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _restarting;

    private const string MutePreset30 = "30";
    private const string MutePreset60 = "60";
    private const string MutePreset120 = "120";
    private const string MutePresetTomorrow = "tomorrow";

    public MainWindow(string baseUrl, InstanceCoordinator coordinator, bool startHidden = false, string? initialGroupId = null)
    {
        InitializeComponent();
        _baseUrl = baseUrl;
        _coordinator = coordinator;
        _startHidden = startHidden;
        _initialGroupId = initialGroupId;
        if (startHidden)
        {
            // 开机自启：先以透明窗口加载，Loaded 后隐藏到托盘（避免登录时闪一下窗口）
            Opacity = 0;
            ShowInTaskbar = false;
            // WebView 初始化可能稍慢：先建好托盘，保证隐藏后仍有入口
            EnsureTray();
        }
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            try { _tray?.Dispose(); } catch { /* 忽略 */ }
            try { _trayMenu?.Dispose(); } catch { /* 忽略 */ }
            try { _badgeIcon?.Dispose(); } catch { /* 忽略 */ }
            try { _openThumbIcon?.Dispose(); } catch { /* 忽略 */ }
            try { _muteThumbIcon?.Dispose(); } catch { /* 忽略 */ }
            try { _taskbar.Dispose(); } catch { /* 忽略 */ }
        };
        // 注销 / 关机时不能被「关闭到托盘」拦住
        System.Windows.Application.Current.SessionEnding += (_, _) => _exiting = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
            _taskbar.Initialize(hwnd);
        }
        catch { /* 远程桌面 / 老系统：降级为仅托盘 */ }
    }

    /// <summary>任务栏缩略图工具栏的按钮点击（THBN_CLICKED）。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_COMMAND = 0x0111;
        const int THBN_CLICKED = 0x1800;
        if (msg == WM_COMMAND)
        {
            var v = wParam.ToInt64();
            var id = (int)(v & 0xFFFF);
            var code = (int)((v >> 16) & 0xFFFF);
            if (code == THBN_CLICKED)
            {
                if (id == TaskbarThumbnailBar.OpenButtonId) { BringToFront(); handled = true; }
                else if (id == TaskbarThumbnailBar.MuteButtonId) { ToggleQuickMute(); handled = true; }
            }
        }
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            // 桌面版系统通知桥（1.0.301）：前端 postMessage → 托盘气泡；WebView2 的 Notification 权限
            // 在宿主里未必可用（用户也无处改“站点设置”），因此由宿主兜底。
            WebView.CoreWebView2.WebMessageReceived += OnWebMessage;
            EnsureTray();
            InitTaskbarButtons();
            StatusText.Text = L10n.StatusLocalService(_baseUrl);
            var startUrl = _baseUrl;
            if (!string.IsNullOrWhiteSpace(_initialGroupId))
                startUrl += "/?group=" + Uri.EscapeDataString(_initialGroupId!);
            WebView.Source = new Uri(startUrl);
            if (_startHidden)
            {
                Hide();
                Opacity = 1;
                ShowInTaskbar = true;
            }
        }
        catch (Exception ex)
        {
            BringToFront();
            StatusText.Text = L10n.WebviewInitFailed(ex.Message);
            MessageBox.Show(this,
                L10n.WebviewRuntimeBody(ex.Message), L10n.MessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnHomeClicked(object sender, RoutedEventArgs e)
        => NavigateTo(_baseUrl);

    private async void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2 is not null)
        {
            // 强制刷新：清 HTTP 缓存后重载（保留登录态——不动 sessionStorage / Cookie）
            try
            {
                await WebView.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.CacheStorage);
            }
            catch { /* 清缓存失败不影响重载 */ }
            WebView.CoreWebView2.Reload();
            StatusText.Text = L10n.StatusWebviewReloaded(_baseUrl);
        }
        else
        {
            NavigateTo(_baseUrl);
        }
    }

    private void NavigateTo(string url)
    {
        if (WebView.CoreWebView2 is not null)
            WebView.CoreWebView2.Navigate(url);
        else
            WebView.Source = new Uri(url);
    }

    /// <summary>
    /// 关闭窗口：启用「关闭到托盘」时只隐藏窗口（后台继续运行），
    /// 从托盘菜单「退出」或系统注销时才真正退出。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            EnsureTray(); // 极端情况（WebView 初始化失败前关窗）也要先有托盘入口
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                ShowTrayBalloon(L10n.TrayMinimizedTitle, L10n.TrayMinimizedBody);
            }
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>接收前端消息：目前只处理 <c>{{ type: "notify", title, body }}</c>（1.0.301）。</summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            switch (type.GetString())
            {
                case "notify":
                    var title = root.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
                    var body = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
                    ShowNotification(title, body);
                    break;
                case "unread":
                    // 前端同步：总未读 = 未读消息 + 未读通知（排除 message 类），1.0.305
                    var count = root.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : 0;
                    var messages = root.TryGetProperty("messages", out var mEl) && mEl.TryGetInt32(out var mc) ? mc : count;
                    var notifications = root.TryGetProperty("notifications", out var nEl) && nEl.TryGetInt32(out var nc) ? nc : 0;
                    SetUnread(count, messages, notifications);
                    break;
                case "progress":
                    // 长任务进度（1.0.306）：计划 done/total → 任务栏百分比
                    var pState = root.TryGetProperty("state", out var st) ? (st.GetString() ?? "none") : "none";
                    var pValue = root.TryGetProperty("value", out var vEl) && vEl.TryGetInt64(out var v) ? (ulong)Math.Max(0, v) : 0UL;
                    var pTotal = root.TryGetProperty("total", out var tEl) && tEl.TryGetInt64(out var tt) ? (ulong)Math.Max(0, tt) : 0UL;
                    SetTaskbarProgress(pState, pValue, pTotal);
                    break;
                case "recents":
                    // 跳转列表最近知聚（1.0.306）
                    var recents = new List<(string Id, string Name)>();
                    if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            var id = item.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                            var name = item.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? id) : id;
                            if (!string.IsNullOrWhiteSpace(id)) recents.Add((id, name));
                        }
                    }
                    UpdateJumpList(recents);
                    break;
            }
        }
        catch { /* 非法消息忽略：不影响宿主 */ }
    }

    /// <summary>托盘气泡通知：首次使用时创建 NotifyIcon（点击气泡 / 双击图标回到窗口）。</summary>
    private void ShowNotification(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        EnsureTray();
        UpdateMenuState();
        if (IsTrayMuted) return; // 托盘菜单里的「静音」（只影响气泡）
        ShowTrayBalloon(title, body);
    }

    private bool IsTrayMuted => DateTime.UtcNow < _mutedUntilUtc;

    private void ShowTrayBalloon(string title, string body)
    {
        try
        {
            _tray!.ShowBalloonTip(6000, Truncate(title, 64), Truncate(body, 200), System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { /* 系统通知被禁用时静默 */ }
    }

    /// <summary>前端上报的未读数（1.0.304）：刷新托盘 tooltip / 徽标与菜单首项（1.0.305 起含通知中心）。</summary>
    private void SetUnread(int count, int messages, int notifications)
    {
        var t = Math.Clamp(count, 0, 9999);
        var m = Math.Clamp(messages, 0, 9999);
        var n = Math.Clamp(notifications, 0, 9999);
        if (t == _unreadCount && m == _unreadMessages && n == _unreadNotifications) return;
        _unreadCount = t;
        _unreadMessages = m;
        _unreadNotifications = n;
        UpdateTrayVisuals();
        UpdateMenuState();
    }

    private void UpdateTrayVisuals()
    {
        if (_tray is null) return;
        try { _tray.Text = _unreadCount > 0 ? L10n.TrayTextUnread(_unreadCount, _unreadMessages, _unreadNotifications) : L10n.TrayText; }
        catch { /* tooltip 超长 / 系统限制：忽略 */ }
        System.Drawing.Icon? retired = null;
        try
        {
            if (_unreadCount > 0)
            {
                var next = CreateBadgeIcon(_baseIcon ?? System.Drawing.SystemIcons.Application, _unreadCount);
                retired = _badgeIcon;
                _badgeIcon = next;
                _tray.Icon = next;
            }
            else if (_badgeIcon is not null)
            {
                _tray.Icon = _baseIcon ?? System.Drawing.SystemIcons.Application;
                retired = _badgeIcon;
                _badgeIcon = null;
            }
        }
        catch { /* 绘制徽标失败：沿用当前图标 */ }
        // 先让任务栏换到新图标，再释放旧图标（避免 shell 仍引用已释放的 HICON）
        UpdateTaskbarOverlay();
        try { retired?.Dispose(); } catch { /* 忽略 */ }
    }

    /// <summary>初始化 / 更新任务栏缩略图按钮（首次 Add，之后 Update）。</summary>
    private void InitTaskbarButtons()
    {
        try
        {
            if (_openThumbIcon is null)
                _openThumbIcon = new System.Drawing.Icon(_baseIcon ?? System.Drawing.SystemIcons.Application, 16, 16);
            _muteThumbIcon ??= CreateMuteThumbIcon(IsTrayMuted);
            _taskbar.SetButtons(_openThumbIcon, L10n.TaskbarOpen, _muteThumbIcon,
                IsTrayMuted ? L10n.TaskbarUnmute : L10n.TaskbarMute);
            UpdateTaskbarOverlay();
            _taskbar.SetProgress(_progressState, _progressValue, _progressTotal);
        }
        catch { /* 任务栏不支持缩略图按钮 */ }
    }

    /// <summary>静音状态变化时只刷新静音按钮的图标 / 提示。</summary>
    private void UpdateTaskbarButtons()
    {
        if (_openThumbIcon is null) return;
        var old = _muteThumbIcon;
        _muteThumbIcon = CreateMuteThumbIcon(IsTrayMuted);
        try
        {
            _taskbar.SetButtons(_openThumbIcon, L10n.TaskbarOpen, _muteThumbIcon,
                IsTrayMuted ? L10n.TaskbarUnmute : L10n.TaskbarMute);
        }
        catch { /* 忽略 */ }
        finally { old?.Dispose(); }
    }

    private void UpdateTaskbarOverlay()
    {
        try
        {
            _taskbar.SetOverlay(_unreadCount > 0 ? _badgeIcon : null,
                _unreadCount > 0 ? L10n.TaskbarUnread(_unreadCount) : "");
        }
        catch { /* 忽略 */ }
    }

    /// <summary>任务栏进度（1.0.306）：相同值不重复调用 shell。</summary>
    private void SetTaskbarProgress(string state, ulong completed, ulong total)
    {
        state = string.IsNullOrWhiteSpace(state) ? "none" : state;
        if (string.Equals(_progressState, state, StringComparison.Ordinal)
            && _progressValue == completed && _progressTotal == total) return;
        _progressState = state;
        _progressValue = completed;
        _progressTotal = total;
        try { _taskbar.SetProgress(state, completed, total); } catch { /* 忽略 */ }
    }

    /// <summary>跳转列表「最近知聚」（1.0.306）：内容未变不重建。</summary>
    private void UpdateJumpList(List<(string Id, string Name)> items)
    {
        var signature = string.Join("\n", items.Select((i) => i.Id + "|" + i.Name));
        if (signature == _recentsSignature) return;
        if (_jumpList.SetRecentGroups(items)) _recentsSignature = signature; // 失败不缓存，下次重试
    }

    /// <summary>在应用图标右下角画未读红点（>9 缩小字号，>99 显示 99+）。</summary>
    private static System.Drawing.Icon CreateBadgeIcon(System.Drawing.Icon baseIcon, int count)
    {
        const int size = 32;
        using var bmp = new System.Drawing.Bitmap(size, size);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.DrawIcon(baseIcon, new System.Drawing.Rectangle(0, 0, size, size));

            var text = count > 99 ? "99+" : count.ToString();
            var badgeSize = count > 9 ? 18 : 16;
            var rect = new System.Drawing.Rectangle(size - badgeSize, size - badgeSize, badgeSize, badgeSize);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(224, 36, 36));
            g.FillEllipse(brush, rect);
            using var font = new System.Drawing.Font("Segoe UI",
                count > 99 ? 6.5f : count > 9 ? 8f : 9.5f,
                System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            using var format = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
            };
            g.DrawString(text, font, System.Drawing.Brushes.White, rect, format);
        }
        return BitmapToIcon(bmp);
    }

    /// <summary>任务栏静音按钮的快捷切换（未静音 → 静音 1 小时；已静音 → 取消）。</summary>
    private void ToggleQuickMute()
    {
        if (IsTrayMuted) ApplyMute(duration: null, preset: null);
        else ApplyMute(TimeSpan.FromHours(1), MutePreset60);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static System.Drawing.Icon BitmapToIcon(System.Drawing.Bitmap bmp)
    {
        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = System.Drawing.Icon.FromHandle(hIcon);
            return (System.Drawing.Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>任务栏静音按钮图标：深色圆底 + 白色喇叭；静音时加红色斜线。</summary>
    private static System.Drawing.Icon CreateMuteThumbIcon(bool muted)
    {
        const int size = 16;
        using var bmp = new System.Drawing.Bitmap(size, size);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using (var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(210, 32, 32, 32)))
                g.FillEllipse(bg, 0, 0, size - 1, size - 1);
            using (var white = new System.Drawing.SolidBrush(System.Drawing.Color.White))
            {
                var pts = new[]
                {
                    new System.Drawing.PointF(3f, 6f), new System.Drawing.PointF(5.5f, 6f),
                    new System.Drawing.PointF(8.5f, 3f), new System.Drawing.PointF(8.5f, 13f),
                    new System.Drawing.PointF(5.5f, 10f), new System.Drawing.PointF(3f, 10f),
                };
                g.FillPolygon(white, pts);
            }
            if (muted)
            {
                using var red = new System.Drawing.Pen(System.Drawing.Color.FromArgb(235, 70, 70), 2f);
                g.DrawLine(red, 1.5f, 1.5f, 14.5f, 14.5f);
            }
            else
            {
                using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 1.2f);
                g.DrawArc(pen, 7.5f, 5f, 6f, 7f, -55f, 110f);
            }
        }
        return BitmapToIcon(bmp);
    }

    /// <summary>静音：只抑制宿主气泡，站内通知中心不受影响；传 null 表示取消静音。</summary>
    private void ApplyMute(TimeSpan? duration, string? preset)
    {
        EnsureTray();
        if (duration is null)
        {
            _mutedUntilUtc = DateTime.MinValue;
            _mutePreset = null;
            ShowTrayBalloon(L10n.TrayUnmutedTitle, L10n.TrayUnmutedBody);
        }
        else
        {
            _mutedUntilUtc = DateTime.UtcNow.Add(duration.Value);
            _mutePreset = preset;
            ShowTrayBalloon(L10n.TrayMutedTitle, L10n.TrayMutedBody(FormatRemaining()));
        }
        UpdateMenuState();
        UpdateTaskbarButtons();
    }

    /// <summary>「到明天 08:00」：本地时间，避免整夜被打扰。</summary>
    private static TimeSpan UntilTomorrowMorning()
    {
        var target = DateTime.Today.AddDays(1).AddHours(8);
        var span = target - DateTime.Now;
        return span <= TimeSpan.Zero ? TimeSpan.FromHours(8) : span;
    }

    private string FormatRemaining()
    {
        var span = _mutedUntilUtc - DateTime.UtcNow;
        if (span <= TimeSpan.Zero) return L10n.TrayRemainingZero;
        var minutes = (int)Math.Ceiling(span.TotalMinutes);
        if (minutes >= 60)
        {
            var h = minutes / 60;
            var m = minutes % 60;
            return m == 0 ? L10n.TrayRemainingHours(h) : L10n.TrayRemainingHoursMinutes(h, m);
        }
        return L10n.TrayRemainingMinutes(minutes);
    }

    private void OpenDataDirectory()
    {
        try
        {
            // 与 SQLite 连接串（Data Source=data/agui.sqlite）同一基准：进程工作目录。
            var dir = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "data"));
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            BringToFront();
            MessageBox.Show(this, L10n.TrayOpenDataDirFailed(ex.Message), L10n.MessageBoxTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>重启本地后端：优雅停机 → 等端口释放 → 以「保留实例计数」模式重启 → 重新加载页面。</summary>
    private async void RestartBackend()
    {
        if (_restarting) return;
        _restarting = true;
        if (_restartItem is not null) _restartItem.Enabled = false;
        ShowTrayBalloon(L10n.TrayRestartingTitle, L10n.TrayRestartingBody);
        bool ok;
        try
        {
            ok = await Task.Run(() =>
            {
                _coordinator.ShutdownBackend(_baseUrl);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline && _coordinator.Probe(_baseUrl, 500)) Thread.Sleep(200);
                if (_coordinator.Probe(_baseUrl, 500)) return false; // 旧服务未停下：不叠加新进程
                _coordinator.StartBackend(preserveInstanceCount: true);
                return _coordinator.WaitReady(_baseUrl, timeoutSec: 90);
            });
        }
        catch
        {
            ok = false;
        }
        _restarting = false;
        if (_restartItem is not null) _restartItem.Enabled = true;
        if (ok)
        {
            NavigateTo(_baseUrl);
            StatusText.Text = L10n.StatusLocalService(_baseUrl);
            ShowTrayBalloon(L10n.TrayRestartDoneTitle, L10n.TrayRestartDoneBody);
        }
        else
        {
            BringToFront();
            ShowTrayBalloon(L10n.TrayRestartFailedTitle, L10n.TrayRestartFailedBody);
            MessageBox.Show(this, L10n.TrayRestartFailedBody, L10n.MessageBoxTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ToggleCloseToTray()
    {
        _settings.CloseToTray = !_settings.CloseToTray;
        _settings.Save();
        UpdateMenuState();
        if (_settings.CloseToTray)
            ShowTrayBalloon(L10n.TrayCloseToTrayOnTitle, L10n.TrayCloseToTrayOnBody);
        else
            ShowTrayBalloon(L10n.TrayCloseToTrayOffTitle, L10n.TrayCloseToTrayOffBody);
    }

    /// <summary>开机自启动开关（HKCU Run，带 --autostart 静默进托盘）。</summary>
    private void ToggleStartup()
    {
        var enable = !StartupRegistration.IsEnabled();
        if (!StartupRegistration.SetEnabled(enable, out var error))
        {
            BringToFront();
            MessageBox.Show(this, L10n.TrayStartupFailed(error ?? "unknown"), L10n.MessageBoxTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateMenuState();
        if (enable)
            ShowTrayBalloon(L10n.TrayStartupOnTitle, L10n.TrayStartupOnBody);
        else
            ShowTrayBalloon(L10n.TrayStartupOffTitle, L10n.TrayStartupOffBody);
    }

    private void EnsureTray()
    {
        if (_tray is not null) return;
        var icon = System.Drawing.SystemIcons.Application;
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exe!);
                if (extracted is not null) icon = extracted;
            }
        }
        catch { /* 取不到就用系统图标 */ }
        _baseIcon = icon;

        _openItem = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayOpen);
        _openItem.Font = new System.Drawing.Font(_openItem.Font, System.Drawing.FontStyle.Bold);
        _openItem.Click += (_, _) => BringToFront();

        var openDataItem = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayOpenDataDir);
        openDataItem.Click += (_, _) => OpenDataDirectory();

        _restartItem = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayRestartBackend);
        _restartItem.Click += (_, _) => RestartBackend();

        _muteMenu = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayMuteMenu);
        _mute30 = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayMute30);
        _mute30.Click += (_, _) => ApplyMute(TimeSpan.FromMinutes(30), MutePreset30);
        _mute60 = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayMute60);
        _mute60.Click += (_, _) => ApplyMute(TimeSpan.FromMinutes(60), MutePreset60);
        _mute120 = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayMute120);
        _mute120.Click += (_, _) => ApplyMute(TimeSpan.FromMinutes(120), MutePreset120);
        _muteTomorrow = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayMuteTomorrow);
        _muteTomorrow.Click += (_, _) => ApplyMute(UntilTomorrowMorning(), MutePresetTomorrow);
        _unmuteItem = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayUnmute);
        _unmuteItem.Click += (_, _) => ApplyMute(duration: null, preset: null);
        _muteMenu.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[]
        {
            _mute30, _mute60, _mute120, _muteTomorrow,
            new System.Windows.Forms.ToolStripSeparator(), _unmuteItem,
        });

        _closeToTrayItem = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayCloseToTray);
        _closeToTrayItem.Click += (_, _) => ToggleCloseToTray();
        _startupItem = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayStartup);
        _startupItem.Click += (_, _) => ToggleStartup();

        var exitItem = new System.Windows.Forms.ToolStripMenuItem(L10n.TrayExit);
        exitItem.Click += (_, _) => ExitFromTray();

        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayMenu.Items.Add(_openItem);
        _trayMenu.Items.Add(openDataItem);
        _trayMenu.Items.Add(_restartItem);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayMenu.Items.Add(_muteMenu);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayMenu.Items.Add(_closeToTrayItem);
        _trayMenu.Items.Add(_startupItem);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);
        _trayMenu.Opening += (_, _) =>
        {
            // 多窗口共享同一偏好文件：打开菜单时重读，其他窗口改过的设置也能立即反映
            _settings.CloseToTray = DesktopSettings.Load().CloseToTray;
            UpdateMenuState();
        };

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _baseIcon,
            Text = L10n.TrayText,
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        _tray.BalloonTipClicked += (_, _) => BringToFront();
        _tray.DoubleClick += (_, _) => BringToFront();
        UpdateTrayVisuals();
        UpdateMenuState();
        InitTaskbarButtons();
    }

    /// <summary>按当前静音 / 关闭行为刷新菜单文案与勾选状态。</summary>
    private void UpdateMenuState()
    {
        if (_mutedUntilUtc != DateTime.MinValue && DateTime.UtcNow >= _mutedUntilUtc)
        {
            _mutedUntilUtc = DateTime.MinValue;
            _mutePreset = null;
            UpdateTaskbarButtons(); // 静音到期：把任务栏按钮恢复为「静音」
        }
        if (_muteMenu is not null)
            _muteMenu.Text = IsTrayMuted ? L10n.TrayMuteMenuWithRemaining(FormatRemaining()) : L10n.TrayMuteMenu;
        if (_mute30 is not null) _mute30.Checked = IsTrayMuted && _mutePreset == MutePreset30;
        if (_mute60 is not null) _mute60.Checked = IsTrayMuted && _mutePreset == MutePreset60;
        if (_mute120 is not null) _mute120.Checked = IsTrayMuted && _mutePreset == MutePreset120;
        if (_muteTomorrow is not null) _muteTomorrow.Checked = IsTrayMuted && _mutePreset == MutePresetTomorrow;
        if (_unmuteItem is not null) _unmuteItem.Enabled = IsTrayMuted;
        if (_closeToTrayItem is not null) _closeToTrayItem.Checked = _settings.CloseToTray;
        if (_startupItem is not null) _startupItem.Checked = StartupRegistration.IsEnabled();
        if (_openItem is not null)
            _openItem.Text = _unreadCount > 0 ? L10n.TrayOpenUnread(_unreadCount) : L10n.TrayOpen;
    }

    private void ExitFromTray()
    {
        _exiting = true;
        try { _tray?.Dispose(); } catch { /* 忽略 */ }
        Close(); // ShutdownMode.OnMainWindowClose → 整个桌面应用退出
    }

    private void BringToFront()
    {
        try
        {
            Opacity = 1;
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            Activate();
        }
        catch { /* 忽略 */ }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var oneLine = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}
