using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Postgres;
using AguiGroupChat.Hub.Persistence.Relational;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Transport;
using AguiGroupChat.Hub.Users;
using System.Text.Json;

namespace AguiGroupChat.Hub;

/// <summary>
/// Hub 装配入口：把 DI 注册与端点映射抽离为可复用方法，
/// 供 Program.cs 与集成测试（自托管 WebApplication）共用。
/// </summary>
public static class HubApp
{
    public static WebApplicationBuilder CreateBuilder(string[] args) => WebApplication.CreateBuilder(args);

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("GroupChat").Get<GroupChatOptions>() ?? new GroupChatOptions();
        builder.Services.AddSingleton(options);
        // 存储提供器：memory（默认，进程内 + JSON 快照）或 postgres（PostgreSQL 落盘，禁用 JSON 快照）
        var storageOptions = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        builder.Services.AddSingleton(storageOptions);
        // 变更通知中心：各存储变更后通知持久化服务标记脏位
        var changeHub = new ChangeHub();
        builder.Services.AddSingleton(changeHub);

        if (string.Equals(storageOptions.Provider, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            // PostgreSQL 模式：建表（幂等）并以数据库实现替换全部内存存储；JSON 快照（PersistenceService）不注册
            var pg = new PostgresStore(storageOptions.ConnectionString ?? "");
            if (storageOptions.AutoCreateSchema) pg.EnsureSchema();
            builder.Services.AddSingleton(pg);
            builder.Services.AddSingleton<IGroupStore, PostgresGroupStore>();
            builder.Services.AddSingleton<IUserStore, PostgresUserStore>();
            builder.Services.AddSingleton<IAgentRegistryStore, PostgresAgentRegistryStore>();
            builder.Services.AddSingleton<ISectionStore, PostgresSectionStore>();
            builder.Services.AddSingleton<IUsageStore, PostgresUsageStore>(); // 模型用量统计（按日聚合）
            builder.Services.AddSingleton<ITaskStore, PostgresTaskStore>();    // 工作任务编排
        }
        else if (string.Equals(storageOptions.Provider, "mysql", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(storageOptions.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            // MySQL / SQLite 模式：共用 Relational* 实现（方言差异隔离在 RelationalStore），同样禁用 JSON 快照
            var isMySql = string.Equals(storageOptions.Provider, "mysql", StringComparison.OrdinalIgnoreCase);
            RelationalStore relational = isMySql
                ? new MySqlStore(storageOptions.ConnectionString ?? "")
                : new SqliteStore(ResolveSqliteConnectionString(builder, storageOptions.ConnectionString));
            if (storageOptions.AutoCreateSchema) relational.EnsureSchema();
            builder.Services.AddSingleton<RelationalStore>(relational);
            builder.Services.AddSingleton<IGroupStore, RelationalGroupStore>();
            builder.Services.AddSingleton<IUserStore, RelationalUserStore>();
            builder.Services.AddSingleton<IAgentRegistryStore, RelationalAgentRegistryStore>();
            builder.Services.AddSingleton<ISectionStore, RelationalSectionStore>();
            builder.Services.AddSingleton<IUsageStore, RelationalUsageStore>(); // 模型用量统计（按日聚合）
            builder.Services.AddSingleton<ITaskStore, RelationalTaskStore>();    // 工作任务编排
        }
        else
        {
            // 用户管理（Hub 扩展）：内存账号存储 + 认证服务（会话令牌）
            builder.Services.AddSingleton<IGroupStore>(new InMemoryGroupStore(options.MessageHistoryLimit, changeHub));
            builder.Services.AddSingleton<IUserStore>(new InMemoryUserStore(changeHub));
            builder.Services.AddSingleton<IUsageStore>(new InMemoryUsageStore(changeHub)); // 模型用量统计（按日聚合）
            builder.Services.AddSingleton<ITaskStore>(new InMemoryTaskStore(changeHub));  // 工作任务编排
            // 持久化（Hub 扩展）：单文件快照，变更后定时落盘，启动时恢复
            var persistenceOptions = builder.Configuration.GetSection("Persistence").Get<PersistenceOptions>() ?? new PersistenceOptions();
            if (!string.IsNullOrEmpty(persistenceOptions.FilePath) && !Path.IsPathRooted(persistenceOptions.FilePath))
                persistenceOptions.FilePath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, persistenceOptions.FilePath));
            builder.Services.AddSingleton(persistenceOptions);
            builder.Services.AddSingleton<PersistenceService>();
        }

        var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        builder.Services.AddSingleton(authOptions);
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<AgentRegistry>();
        /// 操作审计日志（内存环形缓冲）：管理员控制台查询关键 / 敏感操作留痕
        builder.Services.AddSingleton<AguiGroupChat.Hub.Infra.AuditLogService>();
        // 附件文件存储：与持久化快照同根目录（data/uploads），Web 层暴露 HTTP 端点，智能体网关读取文本注入
        var uploadsRoot = Path.Combine(builder.Environment.ContentRootPath, "data", "uploads");
        builder.Services.AddSingleton(new AttachmentStore(uploadsRoot));
        builder.Services.AddSingleton<ConnectionManager>();
        builder.Services.AddSingleton<AgentTriggerService>();
        // 工作任务编排服务（AgentGateway 经 Lazy 依赖，工作型智能体运行绑定任务状态）
        builder.Services.AddSingleton<TaskService>();
        // 预留接口：接入真实 AG-UI 网关时替换为自定义实现
        builder.Services.AddSingleton<IAgentGateway, NoopAgentGateway>();
        builder.Services.AddSingleton<GroupHub>();
        builder.Services.AddSingleton<WebSocketEndpoint>();
        builder.Services.AddSingleton<SseEndpoint>();
        builder.Services.AddSingleton(TimeProvider.System);
    }

    public static void MapEndpoints(WebApplication app)
    {
        var options = app.Services.GetRequiredService<GroupChatOptions>();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(1, options.HeartbeatIntervalSeconds)),
        });

        app.Map("/ws", (HttpContext ctx, WebSocketEndpoint endpoint) => endpoint.HandleAsync(ctx));
        app.MapGet("/sse", (HttpContext ctx, SseEndpoint endpoint) => endpoint.HandleAsync(ctx));

        app.MapGroupApi();
        app.MapUserApi();
    }

    public static async Task SeedSampleDataAsync(WebApplication app)
    {
        var hub = app.Services.GetRequiredService<GroupHub>();
        var auth = app.Services.GetService<AuthService>();
        await new SampleDataSeeder(hub, auth).SeedAsync();
    }

    /// <summary>SQLite 相对路径连接串基于内容根目录解析（缺省 Data Source=data/agui.sqlite）。</summary>
    private static string ResolveSqliteConnectionString(WebApplicationBuilder builder, string? connectionString)
    {
        var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
            string.IsNullOrWhiteSpace(connectionString) ? "Data Source=data/agui.sqlite" : connectionString);
        if (!string.IsNullOrEmpty(csb.DataSource) && csb.DataSource != ":memory:" && !Path.IsPathRooted(csb.DataSource))
            csb.DataSource = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, csb.DataSource));
        return csb.ToString();
    }

    /// <summary>
    /// 注册登录会话到持久化扩展区「sessions」：令牌以哈希落库（agui_sections），启动时恢复——
    /// 使桌面版 / 重启后的 Web 服务跨进程保持「保持登录状态」（会话原本为进程内存态，重启即失效）。
    /// memory 模式由 PersistenceService 核心快照已覆盖会话，无需重复注册。
    /// 须在应用构建后、状态恢复（InitializePersistence）之前调用。
    /// </summary>
    public static void RegisterSessionPersistence(this IServiceProvider services)
    {
        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null) return; // memory 模式：核心快照已含会话（SnapshotSessions）

        var auth = services.GetRequiredService<AuthService>();
        Func<object?> snapshot = () => auth.SnapshotSessions().Select(s => (object)s).ToList();
        Action<JsonElement> restore = element => auth.RestoreSessions(
            element.Deserialize<List<PersistedSession>>(AguiJson.Options) ?? []);
        services.GetService<ISectionStore>()?.AddSection("sessions", snapshot, restore);
    }

    /// <summary>
    /// 恢复持久化状态（须在各扩展区注册完成后调用，如智能体目录）。
    /// 返回是否已存在历史数据；无历史数据且开启示例数据时调用方应播种示例数据。
    /// 同时注册关闭时的强制落盘。
    /// </summary>
    public static bool InitializePersistence(WebApplication app)
    {
        var storageOptions = app.Services.GetRequiredService<StorageOptions>();
        var provider = storageOptions.Provider.Trim().ToLowerInvariant();
        // 关闭前先兜底冲刷防抖中的流式增量（须在 PersistenceService / sections 关闭落盘之前注册，保证执行顺序）
        app.Lifetime.ApplicationStopping.Register(() => app.Services.GetService<GroupHub>()?.FlushAllPendingContent());
        if (provider is "postgres" or "mysql" or "sqlite")
        {
            // 数据库模式：群组 / 成员 / 话题 / 消息 / 用户 / 触发规则由各 Store 直接读写库；
            // 这里恢复上层注册的扩展区（智能体定义），并把成员在线状态复位为离线（连接态）。
            var sections = app.Services.GetService<ISectionStore>();
            if (sections is not null)
            {
                sections.LoadSections();
                app.Lifetime.ApplicationStopping.Register(sections.Flush);
            }
            var store = app.Services.GetRequiredService<IGroupStore>();
            if (store is PostgresGroupStore pgs) pgs.ResetAllOnlineStatuses();
            else if (store is RelationalGroupStore rgs) rgs.ResetAllOnlineStatuses();

            // 返回是否已有历史数据（决定是否播种示例数据）
            return store.AllGroups().Count > 0;
        }

        var persistence = app.Services.GetRequiredService<PersistenceService>();
        app.Lifetime.ApplicationStopping.Register(() => persistence.Flush());
        return persistence.Load();
    }
}
