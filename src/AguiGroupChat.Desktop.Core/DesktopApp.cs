using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Transport;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AguiGroupChat.Desktop;

/// <summary>
/// 桌面版共享宿主（跨平台）：进程内 Kestrel 复用 Hub + MSAGENT 网关 + 全部 HTTP API + 静态前端，
/// 返回本地地址供各 UI 壳（WPF / Avalonia）加载。数据落 SQLite（sqlite-vec 语义记忆），
/// embedding 默认 LLamaSharp 本地模型（models/embedding.gguf）。
/// </summary>
public static class DesktopApp
{
    /// <summary>
    /// 启动本地服务并返回 (宿主, 本地地址)。UI 壳负责展示与退出时停止宿主。
    /// <paramref name="backendMode"/>=true 时为「后端独立进程」模式：固定监听 5200，注册优雅停机端点
    /// （POST /ag-ui/shutdown?secret=xxx，供最后一个 UI 实例关闭时调用），并阻塞等待停机信号。
    /// 多实例架构：UI 实例（默认模式）探测 5200 → 未运行则启动 <c>--backend</c> 子进程 → WebView 指向它；
    /// 每个 UI 实例关闭时实例计数 -1，归零才关后端（见 <see cref="InstanceCoordinator"/>）。
    /// </summary>
    public static (WebApplication App, string BaseUrl) Start(string[] args, int preferredPort = 5200, bool backendMode = false)
    {
        // 关闭出站 HTTP 请求的 W3C trace 传播（不附加 traceparent 头）：部分 API 网关（如 DeepSeek）
        // 对 traceparent 校验严格，曾出现带该头的请求被网关以 invalid header 拒绝（请求头被截断畸形）。
        AppContext.SetSwitch("System.Net.Http.EnableActivityPropagation", false);

        var port = backendMode ? 5200 : FindFreePort(preferredPort); // 多实例共享同一后端：固定 5200
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = HubApp.CreateBuilder(args);
        // 固定 Production：避免被引用的 Web 项目把 appsettings.Development.json 复制进输出后覆盖配置
        builder.Environment.EnvironmentName = "Production";
        builder.WebHost.UseUrls(baseUrl);
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration); // 覆盖 NoopAgentGateway 为真实网关
        builder.Services.AddSingleton<AgentScheduler>(); // 智能体定时任务（cron）调度器
        builder.Services.AddSingleton<AguiGroupChat.Hub.Persistence.MessageRetentionService>(); // 消息保留策略（按天清理历史）
        builder.Services.AddSingleton(new SystemApi.ModelConfigState()); // 运行时模型配置（endpoint / apiKey）
        builder.Services.AddSingleton(builder.Configuration.GetSection("LinkProxy").Get<LinkProxyOptions>() ?? new LinkProxyOptions()); // 链接代理配置
        // HTTP API 枚举字符串化（与协议 §2 一致）
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

        var app = builder.Build();
        app.UseDefaultFiles();
        // 桌面版前端用静态文件（index.html 直引 app.js / style.css / i18n 无版本号）：no-cache + ETag 强制每次校验，
        // 装新包 / WebView 缓存旧文件时必然重新加载，避免改版不生效。Web 版（独立后端）在 Program.cs 相同处理。
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
                ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate",
        });

        HubApp.MapEndpoints(app);
        app.MapAgentApi();      // 智能体目录 + 运行时可新增 / 更新 / 删除 AI 角色
        app.MapTwinApi();       // 用户 AI 分身
        app.MapAttachmentApi(); // 附件上传 / 下载
        app.MapKnowledgeBaseApi(); // 知识库：创建 / 上传文档 / 绑定智能体
        app.MapGroupNameApi();  // 群名自动生成
        app.MapSkillApi();      // 技能库（可复用技能：shell / http / prompt）CRUD + 试运行
        app.MapLinkProxyApi();  // 链接代理（智能体回复中的链接由后端代访）
        app.MapExportImportApi(); // 数据导出 / 导入（账号 + 智能体 + 聊天记录 + 附件）
        app.MapSystemApi();     // 模型配置（endpoint / apiKey）+ 初始化（清空一切）
        app.MapMemoryApi();     // 记忆治理：分群分级 / 自动遗忘 / 可视化
        app.MapScheduledTaskApi(); // 重复性定时任务（1.4）
        app.MapMarketplaceApi(); // 智能体 / 技能市场（3.3）
        app.MapAdminApi();      // 管理员控制台：用户管理（禁用 / 重置密码）+ 系统状态
        app.Services.RegisterAgentPersistence();
        app.Services.RegisterKnowledgeBasePersistence();
        app.Services.RegisterSkillPersistence(); // 技能库（可复用技能定义）跨重启保持
        app.Services.RegisterSessionPersistence(); // 会话落库（哈希）：桌面重启后「保持登录」仍有效
        app.Services.RegisterBridgeCursorPersistence(); // 外部 AG-UI 话题增量游标跨重启保持
        app.Services.RegisterModelConfigPersistence(); // 运行时模型配置跨重启保持
        app.Services.RegisterScheduledTaskPersistence(); // 重复性定时任务配置跨重启保持
        app.Services.RegisterTotpPersistence(); // TOTP 二次验证密钥跨重启保持
        var loaded = HubApp.InitializePersistence(app);
        if (!loaded && app.Services.GetRequiredService<GroupChatOptions>().SeedSampleData)
            HubApp.SeedSampleDataAsync(app).GetAwaiter().GetResult();
        AgentHosting.RegisterAgentTriggerRules(
            app.Services.GetRequiredService<GroupHub>(),
            app.Services.GetRequiredService<AgentOptions>());
        // 应用就绪后启动定时任务调度器（智能体 Schedule cron 到点触发）
        app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<AgentScheduler>().Start());
        // 应用就绪后启动桥接端点健康度周期探测（3.1）
        app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<AguiGroupChat.Agents.BridgeHealthService>().Start());
        // 应用就绪后启动消息保留周期检查（GroupChat:MessageRetentionDays 配置）
        app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<AguiGroupChat.Hub.Persistence.MessageRetentionService>().Start());
        app.MapFallbackToFile("index.html");

        if (backendMode)
        {
            // 后端是新进程：任何残留实例计数都已陈旧（上次异常退出可能遗留），必须先归零，
            // 否则遗留计数会把后续每次启动的计数抬升，导致计数永远到不了 0、后端永不关闭。
            InstanceCoordinator.ResetInstanceCount();

            // 优雅停机端点：最后一个 UI 实例关闭时调用（仅回环可达 + 共享 secret 校验），后台停机不阻塞请求
            var secret = InstanceCoordinator.ReadOrCreateBackendSecret();
            app.MapPost("/ag-ui/shutdown", (HttpContext ctx) =>
            {
                var given = ctx.Request.Query["secret"].ToString();
                if (string.IsNullOrEmpty(given) || !string.Equals(given, secret, StringComparison.Ordinal))
                    return Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200); // 先返回响应再停机
                    try { await app.StopAsync(); } catch { /* 停机异常不再处理 */ }
                    // LLamaSharp 等 native 线程可能阻止托管进程自然退出：冲刷完成后显式收尾（保证持久化落盘完成）
                    Environment.Exit(0);
                });
                return Results.Ok(new { ok = true });
            });
        }

        EnsureEmbeddingModel(builder.Configuration);
        app.StartAsync().GetAwaiter().GetResult();
        Console.WriteLine($"[Desktop] 本地服务已启动：{baseUrl}（SQLite + {(IsMemoryEnabled(app) ? "本地 llama embedding" : "记忆未启用")}）");
        return (app, baseUrl);
    }

    private static bool IsMemoryEnabled(WebApplication app)
        => app.Services.GetRequiredService<AgentOptions>().Memory.Enabled;

    /// <summary>启动前确保本地 embedding 模型就绪（仅 Memory.Provider=llama 时）。模型不随安装包捆绑（数百 MB），
    /// 缺失时：配置了 <c>Agents:Memory:ModelDownloadUrl</c> 则自动下载到可写目录（Program Files 安装不可写时
    /// 回退 %LocalAppData%\AguiGroupChat\models）；未配置则打印放置指引，记忆保持禁用、不阻断启动。</summary>
    private static void EnsureEmbeddingModel(IConfiguration configuration)
    {
        var memory = configuration.GetSection("Agents:Memory");
        if (!bool.TryParse(memory["Enabled"], out var enabled) || !enabled) return;
        if (!string.Equals(memory["Provider"], "llama", StringComparison.OrdinalIgnoreCase)) return;

        var contentRoot = configuration["ContentRoot"] ?? Directory.GetCurrentDirectory();
        var configuredPath = memory["LlamaModelPath"];
        if (!string.IsNullOrWhiteSpace(configuredPath)
            && !string.Equals(Path.GetFileName(configuredPath), "embedding.gguf", StringComparison.OrdinalIgnoreCase))
        {
            // 自定义了其它文件名：自动下载逻辑固定写 embedding.gguf，交给用户自行放置
            Console.WriteLine($"[Desktop] 自定义模型路径 {configuredPath}，跳过自动下载（请自行放置模型）。");
            return;
        }

        var preferredDir = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(contentRoot, "models")
            : Path.IsPathRooted(configuredPath)
                ? Path.GetDirectoryName(configuredPath)!
                : Path.Combine(contentRoot, Path.GetDirectoryName(configuredPath) ?? "models");
        var candidates = new[]
        {
            preferredDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AguiGroupChat", "models"),
        }.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToList();
        if (candidates.Any(d => File.Exists(Path.Combine(d, "embedding.gguf")))) return;

        var url = memory["ModelDownloadUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("[Desktop] 未找到本地 embedding 模型，语义记忆暂不可用。启用方式：");
            Console.WriteLine($"  1) 配置 Agents:Memory:ModelDownloadUrl 后重启（应用将自动下载，默认目标 {candidates[0]}\\embedding.gguf）；");
            Console.WriteLine($"  2) 手动放置 GGUF 模型为 {candidates[0]}\\embedding.gguf。768 维推荐 nomic-embed-text-v1.5.Q8_0.gguf（约 130MB）；");
            Console.WriteLine("     bge-m3 为 1024 维，需同步把 Agents:Memory:EmbeddingDimensions 改为 1024。");
            return;
        }

        var targetDir = candidates[0];
        try
        {
            Directory.CreateDirectory(targetDir);
            var probe = Path.Combine(targetDir, ".agui-wt");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
        }
        catch
        {
            targetDir = candidates[^1];
            Directory.CreateDirectory(targetDir);
        }

        var target = Path.Combine(targetDir, "embedding.gguf");
        var part = target + ".part";
        Console.WriteLine($"[Desktop] 正在下载 embedding 模型（首次启动一次性，完成后记忆自动启用）：{url}");
        Console.WriteLine($"  -> {target}");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
            using var stream = http.GetStreamAsync(url).GetAwaiter().GetResult();
            stream.CopyTo(fs);
            File.Move(part, target, overwrite: true);
            Console.WriteLine("[Desktop] embedding 模型下载完成，语义记忆已就绪。");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* 忽略清理失败 */ }
            Console.WriteLine($"[Desktop] embedding 模型下载失败：{ex.Message}");
            Console.WriteLine($"  可稍后手动放置模型为 {target} 后重启。");
        }
    }

    /// <summary>探测空闲端口（默认 5200；被占用则让系统分配）。</summary>
    private static int FindFreePort(int preferred)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, preferred);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
