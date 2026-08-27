using System.Text.Json;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Postgres;
using AguiGroupChat.Hub.Persistence.Relational;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>智能体网关的 DI 装配与触发规则注册。</summary>
public static class AgentHosting
{
    /// <summary>
    /// 注册 MSAGENT 智能体网关。须在 HubApp.ConfigureServices 之后调用，
    /// 以覆盖 Hub 默认注册的 NoopAgentGateway（后注册者生效）。
    /// </summary>
    public static void AddAgentFramework(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Agents").Get<AgentOptions>() ?? new AgentOptions();
        ResolveApiKey(options, configuration);
        services.AddSingleton(options);
        services.AddSingleton<MemoryContextProvider>(); // MSAGENT AIContextProvider：run 前记忆检索注入
        services.AddSingleton<IAgentDefinitionStore, AgentDefinitionStore>(); // 私密智能体归属查询（GroupHub 校验用）
        services.AddSingleton<TwinService>(); // 用户 AI 分身（人设生成 + 生命周期）
        services.AddSingleton<ITwinAgentSync>(sp => sp.GetRequiredService<TwinService>()); // 分身跟随钩子（GroupHub）
        services.AddSingleton<AgentCatalog>();
        services.AddSingleton<KnowledgeBaseCatalog>(); // 知识库目录（文档切片向量 + 检索）
        services.AddSingleton<AgentSkillCatalog>(sp => new AgentSkillCatalog(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(), options)); // 技能库（OpenClaw 风格可复用技能）
        // 模型 token 用量统计与配额（依赖 Hub 的 IUsageStore；配额值取 Agents:DailyTokenQuotaPerUser）
        services.AddSingleton(sp => new AguiGroupChat.Hub.Agents.AgentUsageService(
            sp.GetRequiredService<AguiGroupChat.Hub.Storage.IUsageStore>(),
            options.DailyTokenQuotaPerUser,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AguiGroupChat.Hub.Agents.AgentUsageService>>()));
        // 同时注册具体类型与接口：组合根 RegisterBridgeCursorPersistence 按具体类型解析网关（注册扩展区）
        services.AddSingleton<AgentGateway>();
        services.AddSingleton<IAgentGateway>(sp => sp.GetRequiredService<AgentGateway>());
        // 重复性定时任务（1.4）：调度器（AgentScheduler）每分钟轮询这些任务触发
        services.AddSingleton<ScheduledTaskService>();
        // 桥接端点健康度（3.1）：周期 TCP 探测 + 管理员控制台查看
        services.AddSingleton<BridgeHealthService>();
        // 轻量运行指标（6.1）：进程内计数器，管理员控制台查看
        services.AddSingleton<MetricsService>();
        // 智能体 / 技能市场（3.3）：内置角色包一键导入
        services.AddSingleton<MarketplaceService>();
        // 桥接能力协商（3.2）：探测外部端点能力供管理员查看
        services.AddSingleton<BridgeCapabilitiesService>();
        AddMessageMemory(services, options, configuration);
    }

    /// <summary>
    /// 语义记忆（RAG）装配：postgres（pgvector）/ sqlite（sqlite-vec，vec0 不可用时降级）存储提供器
    /// 且 Agents:Memory:Enabled=true 时注册向量存储与记忆服务（GroupHub / AgentGateway 经可选依赖
    /// IMessageMemory 注入）。embedding 提供方按 Agents:Memory:Provider 选择：llama（LLamaSharp 本地模型）
    /// 或 http（OpenAI 兼容端点）。存储 / 模型不可用时内部降级为不可用，不影响群聊主流程。
    /// </summary>
    private static void AddMessageMemory(IServiceCollection services, AgentOptions options, IConfiguration configuration)
    {
        if (!options.Memory.Enabled)
        {
            // 未开启（默认）：注册 null 占位，resolve 时打一条提示日志，便于确认记忆未启用的原因
            services.AddSingleton<IMessageMemory>(sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentMessageMemory>();
                logger.LogDebug("语义记忆未启用（Agents:Memory:Enabled 默认 false，开启后智能体回复前会先检索历史记忆）");
                return null!;
            });
            services.AddSingleton<IGraphMemory>(_ => null!); // 图谱记忆随语义记忆同门：未启用即不可用
            return;
        }
        var provider = (configuration["Storage:Provider"] ?? "memory").Trim().ToLowerInvariant();
        if (provider is not ("postgres" or "sqlite"))
        {
            // 非 postgres/sqlite 存储模式（mysql / memory 等）+ Memory.Enabled=true：
            // 不注册会 NRE 的 AgentMessageMemory（store 为 null 时每次操作异常被静默 catch），
            // 打印明确警告 + 注册 null 占位——前端知识库 / 记忆功能收到「不可用」提示
            services.AddSingleton<IMessageMemory>(sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentMessageMemory>();
                logger.LogWarning("当前存储模式（{Provider}）不支持语义记忆，已禁用（向量检索需 pgvector 或 sqlite-vec，请切换 Storage:Provider=postgres/sqlite 或关闭 Agents:Memory:Enabled）", provider);
                return null!;
            });
            services.AddSingleton<IGraphMemory>(_ => null!); // 图谱记忆依赖向量存储，随记忆一同不可用
            return;
        }

        // embedding 提供方：llama（LLamaSharp 本地 GGUF）或 http（OpenAI 兼容端点）
        services.AddSingleton<IEmbeddingProvider>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<LlamaEmbeddingProvider>();
            var m = options.Memory;
            if (string.Equals(m.Provider, "llama", StringComparison.OrdinalIgnoreCase))
            {
                var path = ResolveLlamaModelPath(m, configuration);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    logger.LogWarning("本地 embedding 模型不存在：{Path}。请放置 GGUF 模型（配置 Agents:Memory:LlamaModelPath，或放到应用目录 models/embedding.gguf / %LocalAppData%\\AguiGroupChat\\models\\embedding.gguf），或配置 Agents:Memory:ModelDownloadUrl 自动下载；当前记忆已禁用", path);
                    return new NullEmbeddingProvider();
                }
                try
                {
                    return new LlamaEmbeddingProvider(path, m.LlamaContextSize, m.LlamaThreads, logger);
                }
                catch (Exception ex)
                {
                    // llama.cpp 原生库加载失败（目标机缺 VC++ 运行库 / CPU 指令集不支持 / DLL 缺失等）：
                    // 只禁用语义记忆，绝不阻断群聊主流程（安装 Microsoft Visual C++ Redistributable 后重启可恢复）
                    logger.LogWarning(ex,
                        "本地 embedding 模型加载失败：{Path}。语义记忆已禁用（群聊等其余功能不受影响）；" +
                        "可安装 Microsoft Visual C++ Redistributable x64（aka.ms/vs/17/release/vc_redist.x64.exe）后重启", path);
                    return new NullEmbeddingProvider();
                }
            }
            var endpoint = m.EmbeddingEndpoint ?? options.Endpoint;
            if (string.IsNullOrWhiteSpace(endpoint)) endpoint = "https://api.openai.com/v1";
            return new HttpEmbeddingProvider(endpoint, m.EmbeddingModel, m.EmbeddingApiKey, m.EmbeddingTimeoutSeconds, logger);
        });

        // 记忆服务：store 可用才注册 AgentMessageMemory；store 为 null（如 sqlite 分支的存储类型不符）时
        // 注册 null 占位 + 明确警告，避免「store 为 null 仍构造 AgentMessageMemory → 每次操作 NRE 被静默 catch」
        services.AddSingleton<IMessageMemory>(sp =>
        {
            var store = sp.GetService<IMessageMemoryStore>();
            if (store is null)
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentMessageMemory>();
                logger.LogWarning("当前存储模式（{Provider}）不支持语义记忆，已禁用（向量存储初始化失败：sqlite-vec 仅支持 SQLite 存储提供器）", provider);
                return null!;
            }
            return new AgentMessageMemory(store, options,
                sp.GetRequiredService<ILogger<AgentMessageMemory>>(), sp.GetService<IEmbeddingProvider>());
        });
        // 自动遗忘维护服务（宿主自动启动；记忆 null 占位时内部跳过）
        services.AddHostedService<MemoryMaintenanceService>();

        if (provider == "postgres")
        {
            services.AddSingleton<IMessageMemoryStore>(sp =>
            {
                var pg = sp.GetRequiredService<PostgresStore>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<PgMessageMemoryStore>();
                var store = new PgMessageMemoryStore(pg, ResolveEmbeddingDimensions(sp, options, logger), logger);
                store.EnsureSchema();
                return store;
            });
            // 图谱记忆（Graph RAG，PostgreSQL + pgvector）：实体/关系表 + 递归 CTE 图遍历
            services.AddSingleton<IGraphMemoryStore>(sp =>
            {
                var pg = sp.GetRequiredService<PostgresStore>();
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<PgGraphMemoryStore>();
                var store = new PgGraphMemoryStore(pg, ResolveEmbeddingDimensions(sp, options, logger), logger);
                store.EnsureSchema();
                return store;
            });
            RegisterGraphMemory(services, options);
            return;
        }

        services.AddSingleton<IMessageMemoryStore>(sp =>
        {
            var relational = sp.GetRequiredService<RelationalStore>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SqliteVecMessageMemoryStore>();
            if (relational is not SqliteStore)
            {
                logger.LogWarning("当前存储模式（{Provider}）不支持语义记忆，已禁用（sqlite-vec 仅支持 SQLite 存储提供器）", provider);
                return null!;
            }
            var store = new SqliteVecMessageMemoryStore(relational, ResolveEmbeddingDimensions(sp, options, logger), logger);
            store.EnsureSchema();
            return store;
        });
        // 图谱记忆（Graph RAG，SQLite/MySQL）：实体向量 BLOB + 内存余弦 + 递归 CTE 图遍历
        services.AddSingleton<IGraphMemoryStore>(sp =>
        {
            var relational = sp.GetRequiredService<RelationalStore>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<RelationalGraphMemoryStore>();
            var store = new RelationalGraphMemoryStore(relational, ResolveEmbeddingDimensions(sp, options, logger), logger);
            store.EnsureSchema();
            return store;
        });
        RegisterGraphMemory(services, options);
    }

    /// <summary>注册图谱记忆（Graph RAG）服务：实体/关系抽取器 + 编排（向 GroupHub / MemoryContextProvider 提供 IGraphMemory）。
    /// 须在 IGraphMemoryStore + IEmbeddingProvider 已注册（AddMessageMemory 的 enabled 分支）后调用；GraphEnabled 关闭时内部禁用。</summary>
    private static void RegisterGraphMemory(IServiceCollection services, AgentOptions options)
    {
        services.AddSingleton<GraphEntityExtractor>();
        services.AddSingleton<IGraphMemory>(sp => new GraphMemory(
            sp.GetService<IGraphMemoryStore>(),
            sp.GetRequiredService<IEmbeddingProvider>(),
            sp.GetRequiredService<GraphEntityExtractor>(),
            options,
            sp.GetRequiredService<ILogger<GraphMemory>>()));
    }

    /// <summary>解析记忆建表用向量维度：embedding 提供方（本地 llama 模型）能探测实际维度时优先用实际维度，
    /// 与配置不符时输出明确告警（维度不一致会导致记忆写入失败）；HTTP 提供方无法探测时用配置维度。</summary>
    private static int ResolveEmbeddingDimensions(IServiceProvider sp, AgentOptions options, ILogger logger)
    {
        var configured = Math.Max(8, options.Memory.EmbeddingDimensions);
        if (sp.GetService<IEmbeddingProvider>() is LlamaEmbeddingProvider llama && llama.Dimension > 0)
        {
            var actual = llama.Dimension;
            if (actual != configured)
            {
                logger.LogWarning("向量维度配置（Agents:Memory:EmbeddingDimensions={Configured}）与 embedding 模型实际维度（{Actual}）不一致，将按实际维度 {Actual} 建表（请同步修改配置，否则记忆写入会失败）", configured, actual, actual);
            }
            return actual;
        }
        return configured;
    }

    /// <summary>解析本地 GGUF embedding 模型路径：配置值 → 内容根 <c>models/embedding.gguf</c> → 用户目录
    /// <c>%LocalAppData%\AguiGroupChat\models\embedding.gguf</c>（MSI perMachine 安装时 Program Files 不可写，
    /// 模型下载 / 放置落在用户目录）。首个存在的路径生效；都不存在时返回首选候选（供日志提示用）。</summary>
    private static string? ResolveLlamaModelPath(MemoryOptions memory, IConfiguration configuration)
    {
        var contentRoot = configuration["ContentRoot"] ?? Directory.GetCurrentDirectory();
        var candidates = new List<string>
        {
            Path.Combine(contentRoot, "models", "embedding.gguf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AguiGroupChat", "models", "embedding.gguf"),
        };
        if (!string.IsNullOrWhiteSpace(memory.LlamaModelPath))
            candidates.Insert(0, Path.IsPathRooted(memory.LlamaModelPath)
                ? memory.LlamaModelPath
                : Path.Combine(Directory.GetCurrentDirectory(), memory.LlamaModelPath));
        candidates = candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0]; // 不存在也返回首选候选（日志提示用）
    }

    /// <summary>模型缺失时的空 embedding 提供方：返回 null（记忆写入 / 检索静默失效）。</summary>
    private sealed class NullEmbeddingProvider : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public void Dispose() { }
    }

    /// <summary>
    /// 未显式配置 <c>Agents:ApiKey</c>（appsettings / user-secrets / 环境变量
    /// <c>Agents__ApiKey</c>，后者由配置系统自动映射）时，按优先级回退读取
    /// 环境变量 <c>DEEPSEEK_API_KEY</c>、<c>OPENAI_API_KEY</c>，避免密钥硬编码入库。
    /// </summary>
    private static void ResolveApiKey(AgentOptions options, IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey)) return;
        options.ApiKey = configuration["DEEPSEEK_API_KEY"]
            ?? configuration["OPENAI_API_KEY"];
    }

    /// <summary>
    /// 注册智能体目录到持久化扩展区「agents」：memory 模式写入 JSON 快照（PersistenceService），
    /// postgres 模式落库 agui_sections 表（ISectionStore）。
    /// 须在应用构建后、状态恢复（InitializePersistence）之前调用（Web 组合根）。
    /// </summary>
    public static void RegisterAgentPersistence(this IServiceProvider services)
    {
        var catalog = services.GetRequiredService<AgentCatalog>();
        Func<object?> snapshot = () => catalog.ListDefinitions().Select(d => (object)d).ToList();
        Action<JsonElement> restore = element => catalog.RestoreAll(
            element.Deserialize<List<AgentDefinition>>(AguiJson.Options) ?? []);

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
        {
            persistence.AddSection("agents", snapshot, restore);
        }
        else
        {
            services.GetService<ISectionStore>()?.AddSection("agents", snapshot, restore);
        }
    }

    /// <summary>注册重复性定时任务（1.4）到持久化扩展区「scheduledTasks」：重启后任务配置不丢。</summary>
    public static void RegisterScheduledTaskPersistence(this IServiceProvider services)
    {
        var scheduled = services.GetRequiredService<ScheduledTaskService>();
        Func<object?> snapshot = () => scheduled.Snapshot().Select(t => (object)t).ToList();
        Action<JsonElement> restore = element => scheduled.Restore(
            element.Deserialize<List<ScheduledTask>>(AguiJson.Options) ?? []);

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
        {
            persistence.AddSection("scheduledTasks", snapshot, restore);
        }
        else
        {
            services.GetService<ISectionStore>()?.AddSection("scheduledTasks", snapshot, restore);
        }
    }

    /// <summary>
    /// 注册知识库目录到持久化扩展区「kb」：memory 模式写入 JSON 快照（PersistenceService），
    /// postgres 模式落库 agui_sections 表（ISectionStore）。文档向量存记忆存储（GroupId=kb:{KbId}）。
    /// 须在应用构建后、状态恢复（InitializePersistence）之前调用（Web 组合根）。
    /// </summary>
    public static void RegisterKnowledgeBasePersistence(this IServiceProvider services)
    {
        var catalog = services.GetRequiredService<KnowledgeBaseCatalog>();
        Func<object?> snapshot = () => catalog.ListAll();
        Action<JsonElement> restore = element => catalog.RestoreAll(
            element.Deserialize<List<KnowledgeBase>>(AguiJson.Options) ?? []);

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
        {
            persistence.AddSection("kb", snapshot, restore);
        }
        else
        {
            services.GetService<ISectionStore>()?.AddSection("kb", snapshot, restore);
        }
    }

    /// <summary>
    /// 注册技能库到持久化扩展区「skills」：memory 模式写入 JSON 快照（PersistenceService），
    /// postgres 模式落库 agui_sections 表（ISectionStore）。
    /// 须在应用构建后、状态恢复（InitializePersistence）之前调用（Web 组合根）。
    /// </summary>
    public static void RegisterSkillPersistence(this IServiceProvider services)
    {
        var catalog = services.GetRequiredService<AgentSkillCatalog>();
        Func<object?> snapshot = () => catalog.ListAll();
        Action<JsonElement> restore = element => catalog.RestoreAll(
            element.Deserialize<List<AgentSkillDefinition>>(AguiJson.Options) ?? []);

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
        {
            persistence.AddSection("skills", snapshot, restore);
        }
        else
        {
            services.GetService<ISectionStore>()?.AddSection("skills", snapshot, restore);
        }
    }

    /// <summary>
    /// 注册外部 AG-UI 桥接增量游标到持久化扩展区「bridgeCursors」：网关重启后按话题的增量会话不丢失
    /// （会话首次仍发话题全量历史，游标恢复后即回到增量模式）。memory 模式写入 JSON 快照（PersistenceService），
    /// 数据库模式落库 agui_sections 表（ISectionStore）。
    /// 须在应用构建后、状态恢复（InitializePersistence）之前调用（Web 组合根）。
    /// </summary>
    public static void RegisterBridgeCursorPersistence(this IServiceProvider services)
    {
        var gateway = services.GetRequiredService<AgentGateway>();
        Func<object?> snapshot = gateway.SnapshotBridgeCursors;
        Action<JsonElement> restore = gateway.RestoreBridgeCursors;

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
        {
            persistence.AddSection("bridgeCursors", snapshot, restore);
        }
        else
        {
            services.GetService<ISectionStore>()?.AddSection("bridgeCursors", snapshot, restore);
        }
    }

    /// <summary>
    /// 把配置中声明的智能体注册到其所在群组的触发规则（协议 §6）。
    /// 适用于启动时数据（如示例种子）已经就绪的场景。
    /// </summary>
    public static void RegisterAgentTriggerRules(GroupHub hub, AgentOptions options)
    {
        var store = hub.Store;
        foreach (var def in options.Agents)
        {
            var groupIds = store.AllGroups()
                .Where(g => store.IsMember(g.GroupId, def.AgentId))
                .Select(g => g.GroupId)
                .ToList();
            if (groupIds.Count == 0) continue;

            hub.RegisterAgent(new AgentRegisterRequest
            {
                AgentId = def.AgentId,
                Nickname = def.Nickname,
                GroupIds = groupIds,
                TriggerMode = def.TriggerMode,
                Keywords = def.Keywords,
            });
        }
    }
}
