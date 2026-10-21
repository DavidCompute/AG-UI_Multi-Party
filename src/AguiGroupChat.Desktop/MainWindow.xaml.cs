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
    private int _unreadCount;

    private readonly bool _startHidden;

    private DateTime _mutedUntilUtc = DateTime.MinValue;
    private string? _mutePreset;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _restarting;

    private const string MutePreset30 = "30";
    private const string MutePreset60 = "60";
    private const string MutePreset120 = "120";
    private const string MutePresetTomorrow = "tomorrow";

    public MainWindow(string baseUrl, InstanceCoordinator coordinator, bool startHidden = false)
    {
        InitializeComponent();
        _baseUrl = baseUrl;
        _coordinator = coordinator;
        _startHidden = startHidden;
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
        };
        // 注销 / 关机时不能被「关闭到托盘」拦住
        System.Windows.Application.Current.SessionEnding += (_, _) => _exiting = true;
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
            StatusText.Text = L10n.StatusLocalService(_baseUrl);
            WebView.Source = new Uri(_baseUrl);
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
                    // 前端 updateDocTitle() 同步的总未读消息数（托盘 tooltip / 徽标，1.0.304）
                    var count = root.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : 0;
                    SetUnreadCount(count);
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

    /// <summary>前端上报的总未读消息数（1.0.304）：刷新托盘 tooltip / 徽标与菜单首项。</summary>
    private void SetUnreadCount(int count)
    {
        var next = Math.Clamp(count, 0, 9999);
        if (next == _unreadCount) return;
        _unreadCount = next;
        UpdateTrayVisuals();
        UpdateMenuState();
    }

    private void UpdateTrayVisuals()
    {
        if (_tray is null) return;
        try { _tray.Text = _unreadCount > 0 ? L10n.TrayTextUnread(_unreadCount) : L10n.TrayText; }
        catch { /* tooltip 超长 / 系统限制：忽略 */ }
        try
        {
            if (_unreadCount > 0)
            {
                var next = CreateBadgeIcon(_baseIcon ?? System.Drawing.SystemIcons.Application, _unreadCount);
                var old = _badgeIcon;
                _badgeIcon = next;
                _tray.Icon = next;
                old?.Dispose();
            }
            else if (_badgeIcon is not null)
            {
                _tray.Icon = _baseIcon ?? System.Drawing.SystemIcons.Application;
                _badgeIcon.Dispose();
                _badgeIcon = null;
            }
        }
        catch { /* 绘制徽标失败：沿用当前图标 */ }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

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
    }

    /// <summary>按当前静音 / 关闭行为刷新菜单文案与勾选状态。</summary>
    private void UpdateMenuState()
    {
        if (_mutedUntilUtc != DateTime.MinValue && DateTime.UtcNow >= _mutedUntilUtc)
        {
            _mutedUntilUtc = DateTime.MinValue;
            _mutePreset = null;
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
