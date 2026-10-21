using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using MessageBox = System.Windows.MessageBox;

namespace AguiGroupChat.Desktop;

public partial class MainWindow : Window
{
    private readonly string _baseUrl;
    private System.Windows.Forms.NotifyIcon? _tray;

    public MainWindow(string baseUrl)
    {
        InitializeComponent();
        _baseUrl = baseUrl;
        Loaded += OnLoaded;
        Closed += (_, _) => { try { _tray?.Dispose(); } catch { /* 忽略 */ } };
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
            StatusText.Text = L10n.StatusLocalService(_baseUrl);
            WebView.Source = new Uri(_baseUrl);
        }
        catch (Exception ex)
        {
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

    /// <summary>接收前端消息：目前只处理 <c>{{ type: "notify", title, body }}</c>（1.0.301）。</summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "notify") return;
            var title = root.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
            var body = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
            ShowNotification(title, body);
        }
        catch { /* 非法消息忽略：不影响宿主 */ }
    }

    /// <summary>托盘气泡通知：首次使用时创建 NotifyIcon（点击气泡 / 双击图标回到窗口）。</summary>
    private void ShowNotification(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        EnsureTray();
        try
        {
            _tray!.ShowBalloonTip(6000, Truncate(title, 64), Truncate(body, 200), System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { /* 系统通知被禁用时静默 */ }
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

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "知聚",
            Visible = true,
        };
        _tray.BalloonTipClicked += (_, _) => BringToFront();
        _tray.DoubleClick += (_, _) => BringToFront();
    }

    private void BringToFront()
    {
        try
        {
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
