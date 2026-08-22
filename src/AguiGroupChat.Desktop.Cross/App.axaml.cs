using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AguiGroupChat.Desktop.Cross;

public partial class App : Application
{
    /// <summary>共享宿主启动后写入的本地服务地址（UI 加载目标）。</summary>
    public static string BaseUrl { get; set; } = "http://127.0.0.1:5200";

    /// <summary>UI 线程未处理异常（WebView 适配器失败等）——供窗口状态栏提示。</summary>
    public static string? LastUiError { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
