using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Postgres;
using AguiGroupChat.Hub.Persistence.Redis;
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
        // 静态加固保险箱：用服务端密钥对敏感字段（模型 API Key / TOTP 密钥）落盘时加密，防快照/库明文泄露
        builder.Services.AddSingleton<SecretVault>();
        // 变更通知中心：各存储变更后通知持久化服务标记脏位
        var changeHub = new ChangeHub();
        builder.Services.AddSingleton(changeHub);

        // 存储注册：按 Provider 选择方言实现。每种方言把「底层连接上下文 + 一组 Store」收敛为单一职责的注册方法，
        // 消除四段 if/else 里重复的 Store 注册行，新增数据库时只加一个分支。
        switch (storageOptions.Provider.Trim().ToLowerInvariant())
        {
            case "postgres":
                RegisterPostgresStore(builder, storageOptions);
                break;
            case "mysql":
            case "sqlite":
                RegisterRelationalStore(builder, storageOptions);
                break;
            case "redis":
                RegisterRedisStore(builder, storageOptions);
                break;
            default:
                RegisterMemoryStore(builder, storageOptions, options, changeHub);
                break;
        }

        var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        builder.Services.AddSingleton(authOptions);
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<TotpService>(); // 登录二次验证（TOTP，4.4）
        builder.Services.AddSingleton<AgentRegistry>();
        /// 操作审计日志（内存环形缓冲）：管理员控制台查询关键 / 敏感操作留痕
        builder.Services.AddSingleton<AguiGroupChat.Hub.Infra.AuditLogService>();
        // 附件文件存储：与持久化快照同根目录（data/uploads），Web 层暴露 HTTP 端点，智能体网关读取文本注入
        var uploadsRoot = Path.Combine(builder.Environment.ContentRootPath, "data", "uploads");
        builder.Services.AddSingleton(new AttachmentStore(uploadsRoot));
        builder.Services.AddSingleton<ConnectionManager>();
        builder.Services.AddSingleton<AgentTriggerService>();
        // 预留接口：接入真实 AG-UI 网关时替换为自定义实现
        builder.Services.AddSingleton<IAgentGateway, NoopAgentGateway>();
        builder.Services.AddSingleton<GroupHub>();
        builder.Services.AddSingleton<WebSocketEndpoint>();
        builder.Services.AddSingleton<SseEndpoint>();
        builder.Services.AddSingleton(TimeProvider.System);
    }

    /// <summary>PostgreSQL 存储：建表（幂等），以数据库实现替换全部内存存储；JSON 快照（PersistenceService）不注册。</summary>
    private static void RegisterPostgresStore(WebApplicationBuilder builder, StorageOptions storageOptions)
    {
        var pg = new PostgresStore(storageOptions.ConnectionString ?? "");
        if (storageOptions.AutoCreateSchema) pg.EnsureSchema();
        builder.Services.AddSingleton(pg);
        builder.Services.AddSingleton<IGroupStore, PostgresGroupStore>();
        builder.Services.AddSingleton<IUserStore, PostgresUserStore>();
        builder.Services.AddSingleton<IAgentRegistryStore, PostgresAgentRegistryStore>();
        builder.Services.AddSingleton<ISectionStore, PostgresSectionStore>();
        builder.Services.AddSingleton<IUsageStore, PostgresUsageStore>(); // 模型用量统计（按日聚合）
        builder.Services.AddSingleton<ISessionStore>(new InMemorySessionStore()); // 登录会话（进程内 + 扩展区持久化）
    }

    /// <summary>MySQL / SQLite 存储：共用 Relational* 实现（方言差异隔离在 RelationalStore），同样禁用 JSON 快照。</summary>
    private static void RegisterRelationalStore(WebApplicationBuilder builder, StorageOptions storageOptions)
    {
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
        builder.Services.AddSingleton<ISessionStore>(new InMemorySessionStore()); // 登录会话（进程内 + 扩展区持久化）
    }

    /// <summary>Redis 存储（6.2 Web 多副本横向扩展）：全部 Store 与登录会话共享 Redis，多副本读写同一批 key 保持一致；禁用 JSON 快照（Redis 本身即落盘）。</summary>
    private static void RegisterRedisStore(WebApplicationBuilder builder, StorageOptions storageOptions)
    {
        var redis = new RedisContext(storageOptions.ConnectionString ?? "localhost:6379");
        builder.Services.AddSingleton(redis);
        builder.Services.AddSingleton<IGroupStore, RedisGroupStore>();
        builder.Services.AddSingleton<IUserStore, RedisUserStore>();
        builder.Services.AddSingleton<IAgentRegistryStore, RedisAgentRegistryStore>();
        builder.Services.AddSingleton<ISectionStore, RedisSectionStore>();
        builder.Services.AddSingleton<IUsageStore, RedisUsageStore>();   // 模型用量统计（按日聚合）
        builder.Services.AddSingleton<ISessionStore, RedisSessionStore>(); // 登录会话跨副本共享
    }

    /// <summary>内存存储：进程内账号存储 + 认证服务 + 单文件快照持久化（变更后定时落盘，启动时恢复）。</summary>
    private static void RegisterMemoryStore(WebApplicationBuilder builder, StorageOptions storageOptions, GroupChatOptions options, ChangeHub changeHub)
    {
        builder.Services.AddSingleton<IGroupStore>(new InMemoryGroupStore(options.MessageHistoryLimit, changeHub));
        builder.Services.AddSingleton<IUserStore>(new InMemoryUserStore(changeHub));
        builder.Services.AddSingleton<ISessionStore>(new InMemorySessionStore()); // 登录会话（进程内，随 JSON 快照持久化）
        builder.Services.AddSingleton<IUsageStore>(new InMemoryUsageStore(changeHub)); // 模型用量统计（按日聚合）
        // 持久化（Hub 扩展）：单文件快照，变更后定时落盘，启动时恢复
        var persistenceOptions = builder.Configuration.GetSection("Persistence").Get<PersistenceOptions>() ?? new PersistenceOptions();
        if (!string.IsNullOrEmpty(persistenceOptions.FilePath) && !Path.IsPathRooted(persistenceOptions.FilePath))
            persistenceOptions.FilePath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, persistenceOptions.FilePath));
        builder.Services.AddSingleton(persistenceOptions);
        builder.Services.AddSingleton<PersistenceService>();
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

        // Redis 模式：登录会话由 RedisSessionStore 共享（跨副本一致），无需再写入 sections 扩展区
        if (services.GetService<ISessionStore>() is RedisSessionStore) return;

        var auth = services.GetRequiredService<AuthService>();
        Func<object?> snapshot = () => auth.SnapshotSessions().Select(s => (object)s).ToList();
        Action<JsonElement> restore = element => auth.RestoreSessions(
            element.Deserialize<List<PersistedSession>>(AguiJson.Options) ?? []);
        services.GetService<ISectionStore>()?.AddSection("sessions", snapshot, restore);
    }

    /// <summary>注册 TOTP 二次验证密钥（4.4）到扩展区「totpSecrets」：memory 与数据库模式均需（核心快照不含它）。</summary>
    public static void RegisterTotpPersistence(this IServiceProvider services)
    {
        var totp = services.GetService<TotpService>();
        if (totp is null) return;
        Func<object?> snapshot = () => totp.Snapshot().ToDictionary(kv => kv.Key, kv => kv.Value);
        Action<JsonElement> restore = element => totp.Restore(
            element.Deserialize<Dictionary<string, UserTotp>>(AguiJson.Options) ?? []);

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null) persistence.AddSection("totpSecrets", snapshot, restore);
        else services.GetService<ISectionStore>()?.AddSection("totpSecrets", snapshot, restore);
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
        if (provider == "redis")
        {
            // Redis 模式（6.2 多副本）：各 Store 经 Redis 共享；这里恢复扩展区（智能体定义），
            // 并把成员在线状态复位为离线（连接态，避免上次进程留下的在线残留）。
            var sections = app.Services.GetService<ISectionStore>();
            if (sections is not null)
            {
                sections.LoadSections();
                app.Lifetime.ApplicationStopping.Register(sections.Flush);
            }
            var store = app.Services.GetRequiredService<IGroupStore>();
            ResetGroupMembersOnline(store);

            return store.AllGroups().Count > 0;
        }

        var persistence = app.Services.GetRequiredService<PersistenceService>();
        app.Lifetime.ApplicationStopping.Register(() => persistence.Flush());
        return persistence.Load();
    }

    /// <summary>把全部群成员的在线状态复位为离线（连接态为瞬时量，进程退出/启动时归零）。</summary>
    private static void ResetGroupMembersOnline(IGroupStore store)
    {
        foreach (var g in store.AllGroups())
        {
            foreach (var m in store.ListMembers(g.GroupId))
            {
                if (m.OnlineStatus == OnlineStatus.Offline) continue;
                m.OnlineStatus = OnlineStatus.Offline;
                store.UpdateMember(g.GroupId, m);
            }
        }
    }
}
