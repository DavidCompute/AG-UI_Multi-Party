using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AguiGroupChat.Desktop.Cross;

public partial class MainWindow : Window
{
    private bool _browserOpened; // 已自动打开浏览器（只一次）

    public MainWindow()
    {
        InitializeComponent();
        SetWindowIcon();
        StatusText.Text = $"本地服务：{App.BaseUrl}（数据与记忆全部存储在本机）";
        // 内嵌 WebView：AttachedToVisualTree 后显式 Navigate（Source 属性在适配器就绪前设置会被忽略）；
        // 3 秒后复查：若 WebView 初始化失败（LastUiError 被 Dispatcher 兜底记录），自动降级为系统浏览器打开。
        AttachedToVisualTree += (_, _) => NavigateSafely();
        var check = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        check.Tick += (_, _) => { check.Stop(); EnsureFallback(); };
        check.Start();
    }

    /// <summary>加载 AG-UI 品牌图标作为窗口图标（输出目录 Assets/agui-icon-256.png，跨平台）。</summary>
    private void SetWindowIcon()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "agui-icon-256.png");
            if (System.IO.File.Exists(path))
                using (var fs = System.IO.File.OpenRead(path))
                    Icon = new WindowIcon(new Bitmap(fs));
        }
        catch { /* 图标缺失 / 加载失败不影响窗口启动 */ }
    }

    private void NavigateSafely()
    {
        try
        {
            Web.Navigate(new System.Uri(App.BaseUrl));
        }
        catch (Exception ex)
        {
            App.LastUiError ??= ex.Message;
        }
        EnsureFallback();
    }

    /// <summary>若内嵌 WebView 不可用（同步异常或 3 秒内异步失败），自动用系统浏览器打开本地地址。</summary>
    private void EnsureFallback()
    {
        if (string.IsNullOrEmpty(App.LastUiError))
        {
            StatusText.Text = $"本地服务：{App.BaseUrl}（数据与记忆全部存储在本机）";
            return;
        }
        StatusText.Text = $"内嵌窗口不可用（{App.LastUiError}）——已用浏览器打开 {App.BaseUrl}";
        OpenBrowserOnce();
    }

    private void OpenBrowserOnce()
    {
        if (_browserOpened) return;
        _browserOpened = true;
        try
        {
            Process.Start(new ProcessStartInfo(App.BaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开浏览器失败：{ex.Message}（请手动访问 {App.BaseUrl}）";
        }
    }

    private void OnReloadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        App.LastUiError = null;
        try
        {
            // 强制刷新：带时间戳重新导航（URL 变化绕开 HTTP 缓存；同源，登录态保留）
            var url = App.BaseUrl + (App.BaseUrl.Contains('?') ? "&" : "?") + "_t=" + Environment.TickCount64;
            Web.Navigate(new System.Uri(url));
            StatusText.Text = $"本地服务：{App.BaseUrl}（已强制刷新缓存）";
        }
        catch
        {
            NavigateSafely();
        }
    }

    private void OnOpenBrowser(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenBrowserOnce();
    }
}
