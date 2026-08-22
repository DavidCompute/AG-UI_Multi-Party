using AguiGroupChat.Desktop;
using Avalonia;
using Avalonia.Threading;

namespace AguiGroupChat.Desktop.Cross;

/// <summary>
/// 跨平台桌面版（Avalonia 12）：同一套进程内 Kestrel 宿主（<see cref="DesktopApp"/>），
/// UI 壳用 Avalonia + 官方 WebView 控件——Windows 走 WebView2、macOS 走 WKWebView、Linux 走 WebKitGTK。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 兜底：WebView 控件在受限会话 / 缺系统 WebView 组件时会在 UI 线程重抛异常——标记已处理并记录，
        // 进程保持运行（宿主继续服务，用户可用浏览器打开本地地址），不因内嵌窗口失败而整体退出。
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            App.LastUiError = e.Exception?.Message ?? "未知错误";
            Console.WriteLine($"[UI 未处理异常已兜底] {e.Exception?.Message}");
            e.Handled = true;
        };

        var (app, baseUrl) = DesktopApp.Start(args);
        App.BaseUrl = baseUrl;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // 限时停止本地宿主：Kestrel 优雅停机等待 WebSocket 长连接可能导致进程悬挂，5s 超时强制放行
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                app.StopAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch { /* 停止超时 / 异常：直接退出进程 */ }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
