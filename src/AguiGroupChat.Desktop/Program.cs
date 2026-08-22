using System.Windows;
using Microsoft.Extensions.Hosting;

namespace AguiGroupChat.Desktop;

/// <summary>
/// 桌面版组合根（Windows）：支持多实例共享一个后端进程。
///   --backend 模式：独立后端进程（无 UI，固定 5200，注册 /ag-ui/shutdown 优雅停机端点），
///                  由第一个 UI 实例以子进程方式启动；最后一个 UI 实例关闭时通知其停机。
///   默认模式（UI）：探测 5200 → 未运行则启动 --backend 子进程并等待就绪 → 打开 WebView2 窗口；
///                  实例引用计数 +1（见 <see cref="InstanceCoordinator"/>），关窗时 -1，归零才停后端。
/// 数据落 SQLite（sqlite-vec），embedding 用 LLamaSharp；共享宿主逻辑见 <see cref="DesktopApp"/>。
/// </summary>
public static class Program
{
    /// <summary>多实例共享的后端固定端口（与 appsettings.json 的 Urls 一致）。</summary>
    private const int BackendPort = 5200;

    [STAThread]
    public static void Main(string[] args)
    {
        // ---- 后端进程角色：无 UI，固定 5200，阻塞等待停机信号 ----
        if (args.Contains("--backend"))
        {
            var (app, _) = DesktopApp.Start(args, preferredPort: BackendPort, backendMode: true);
            app.WaitForShutdown(); // /ag-ui/shutdown 触发 StopAsync 后返回 → Main 结束 → 进程退出
            return;
        }

        // ---- UI 实例角色 ----
        var baseUrl = $"http://127.0.0.1:{BackendPort}";
        var coordinator = new InstanceCoordinator();

        // 后端未运行 → 启动子进程并等待就绪（首次启动可能建库 / 下载 embedding 模型，最多等 60s）
        var startedBackend = false;
        if (!coordinator.Probe(baseUrl))
        {
            coordinator.StartBackend();
            startedBackend = true;
            if (!coordinator.WaitReady(baseUrl, timeoutSec: 60))
            {
                MessageBox.Show(
                    "本地服务启动失败（可能是端口 5200 被其他程序占用，或首次初始化超时）。\n" +
                    "请关闭占用端口的程序后重试。",
                    "AG-UI 桌面版", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        coordinator.AddInstance(resetIfStale: startedBackend);

        // 桌面窗口（WebView2 加载本地页面；共享同一后端，多实例各自独立窗口）
        var wpfApp = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow(baseUrl);

        // 关窗：实例计数 -1；最后一个实例 → 通知后端优雅停机。
        // 停机在后台线程执行，不阻塞 UI；Run 返回后短等待放行，进程必退。
        window.Closed += (_, _) =>
        {
            _ = Task.Run(() => coordinator.RemoveInstanceAndShutdownIfLast(baseUrl));
        };
        wpfApp.Run(window);
        Thread.Sleep(800); // 等后台停机请求发出（不阻塞关闭过程）
    }
}
