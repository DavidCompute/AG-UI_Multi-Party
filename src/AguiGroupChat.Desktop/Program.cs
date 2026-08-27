using System.Windows;
using AguiGroupChat.Hub.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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
            StartBackendSelfWatchdog(app); // 兜底自监视停机（见下），托管线程阻塞到进程退出
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

    /// <summary>
    /// 后端兜底自监视停机：当没有 UI 实例在用它时，即使正常停机链路（计数归零 → HTTP shutdown）
    /// 因异常（如 UI 被任务管理器强制结束、计数文件陈旧、HTTP 停机请求失败）而失效，也能自行退出，
    /// 避免“所有桌面实例退出后后台进程还在”。
    /// 判据：实例计数为 0 且无活动实时连接（WS/SSE）——持续 <see cref="SelfStopGraceCoreSec"/> 即停机；
    /// 若计数文件残留为正但长时间无活动连接（强制结束 UI 的场景），持续 <see cref="SelfStopGraceIdleSec"/> 也停机。
    /// </summary>
    private static void StartBackendSelfWatchdog(WebApplication app)
    {
        const int periodSec = 5;
        const int SelfStopGraceCoreSec = 15;   // 计数为 0 且无连接：短宽限（容忍首实例启动瞬间）后停机
        const int SelfStopGraceIdleSec = 120;  // 计数残留为正但长时间无连接：长宽限后停机（容忍登出/待机窗口）
        var connections = app.Services.GetRequiredService<ConnectionManager>();
        var cts = new CancellationTokenSource();
        app.Lifetime.ApplicationStopping.Register(cts.Cancel);
        _ = Task.Run(async () =>
        {
            var zeroSince = DateTime.UtcNow;
            var idleSince = DateTime.UtcNow;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(periodSec), cts.Token);
                    if (cts.IsCancellationRequested) break;
                    var count = InstanceCoordinator.ReadInstanceCount();
                    var conn = connections.ConnectionCount;
                    if (count <= 0 && conn <= 0)
                    {
                        if ((DateTime.UtcNow - zeroSince).TotalSeconds >= SelfStopGraceCoreSec)
                        {
                            ShutdownBackendSelf(app);
                            break;
                        }
                    }
                    else if (conn <= 0)
                    {
                        if ((DateTime.UtcNow - idleSince).TotalSeconds >= SelfStopGraceIdleSec)
                        {
                            ShutdownBackendSelf(app);
                            break;
                        }
                    }
                    else
                    {
                        zeroSince = DateTime.UtcNow;
                        idleSince = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException) { /* 应用正在停止：正常 */ }
        });
    }

    private static void ShutdownBackendSelf(WebApplication app)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            app.StopAsync(cts.Token).GetAwaiter().GetResult();
            // LLamaSharp 等 native 线程可能阻止托管进程自然退出：显式收尾（保证持久化落盘完成）
            Environment.Exit(0);
        }
        catch (Exception)
        {
            Environment.Exit(1);
        }
    }
}
