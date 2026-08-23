using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Postgres;
using AguiGroupChat.Hub.Persistence.Relational;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 系统级 HTTP API：
///   GET  /ag-ui/settings/model —— 查询当前模型配置（endpoint / 是否已配置 apiKey / provider）
///   POST /ag-ui/settings/model —— 保存模型配置：endpoint 留空 → deepseek 自动用官方端点
///                                （https://api.deepseek.com）；apiKey 留空 → 使用环境变量
///                                （DEEPSEEK_API_KEY / OPENAI_API_KEY）
///   POST /ag-ui/reset          —— 系统初始化：清空账号 / 智能体 / 群 / 消息 / 附件 / 记忆 / 会话 / 配置
/// </summary>
public static class SystemApi
{
    /// <summary>运行时模型配置（持久化扩展区「modelConfig」的载体；IsConfigured 标记是否显式保存过）。</summary>
    public sealed class ModelConfigState
    {
        public bool IsConfigured { get; set; }
        public string? Endpoint { get; set; }
        public string? ApiKey { get; set; }

        /// <summary>思考模式（默认开启）：开启时智能体调用推理模型（DeepSeek 自动用 deepseek-reasoner）。</summary>
        public bool ThinkingMode { get; set; } = true;
    }

    public static void MapSystemApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui");

        // ---- 查询模型配置：前端据此判断是否需要在进入时弹出配置 ----
        root.MapGet("/settings/model", (HttpContext ctx, AuthService auth, AuthOptions authOptions, AgentOptions options, ModelConfigState modelConfig) =>
        {
            var (_, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            return Results.Ok(new
            {
                configured = modelConfig.IsConfigured,
                provider = options.Provider,
                endpoint = options.Endpoint,
                apiKeyConfigured = !string.IsNullOrWhiteSpace(modelConfig.ApiKey)
                    || !string.IsNullOrWhiteSpace(options.ApiKey), // 含环境变量 / Agents__ApiKey 配置注入的密钥
                model = options.Model,
                thinkingMode = options.ThinkingMode,
            });
        });

        // ---- 保存模型配置：endpoint / apiKey 均可留空（留空 → 默认官方端点 / 环境变量密钥）----
        // 仅系统管理员可改：恶意 endpoint 会把全部智能体请求（含群消息）转发到攻击者服务器
        root.MapPost("/settings/model", (ModelConfigHttpRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions,
            AgentOptions options, ModelConfigState modelConfig, AgentCatalog catalog, ChangeHub changes,
            AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var (meId, error) = WebIdentity.RequireAdmin(ctx, auth, authOptions);
            if (error is not null) return error;

            var endpoint = (req.Endpoint ?? "").Trim();
            if (endpoint.Length > 0)
            {
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var ep) || (ep.Scheme != Uri.UriSchemeHttp && ep.Scheme != Uri.UriSchemeHttps))
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "endpoint 不是合法的 http/https URL"));
            }

            modelConfig.Endpoint = endpoint.Length > 0 ? endpoint : null; // 留空 → deepseek 自动官方端点
            modelConfig.ApiKey = string.IsNullOrWhiteSpace(req.ApiKey) ? null : req.ApiKey.Trim(); // 留空 → 环境变量
            if (req.ThinkingMode is { } thinking) modelConfig.ThinkingMode = thinking; // 未传保持原值
            modelConfig.IsConfigured = true;
            ApplyModelConfig(options, modelConfig, catalog);
            changes.Notify(); // 驱动持久化（JSON 快照 / agui_sections 定时落盘）

            audit.Record("settings.model", meId, auth.GetUser(meId)?.Username,
                detail: "修改模型配置" + (endpoint.Length > 0 ? $" 端点={endpoint}" : " （使用官方端点）"));

            return Results.Ok(new
            {
                ok = true,
                endpoint = options.Endpoint ?? "https://api.deepseek.com", // 留空默认官方端点
                apiKeyConfigured = !string.IsNullOrWhiteSpace(modelConfig.ApiKey),
            });
        });

        // ---- 系统初始化：删除所有数据（清空一切），仅系统管理员可执行 ----
        root.MapPost("/reset", (HttpContext ctx, AuthService auth, AuthOptions authOptions, IServiceProvider sp) =>
        {
            var (meId, error) = WebIdentity.RequireAdmin(ctx, auth, authOptions);
            if (error is not null) return error;

            var storage = sp.GetRequiredService<StorageOptions>();
            var provider = storage.Provider.Trim().ToLowerInvariant();

            // 数据库模式：清空全部业务表（保留 schema；sections 表承载 agents/kb/bridgeCursors/modelConfig 等扩展区）
            if (provider is "postgres" or "mysql" or "sqlite")
            {
                if (provider == "postgres")
                {
                    sp.GetRequiredService<PostgresStore>().ClearAllData();
                }
                else
                {
                    sp.GetRequiredService<RelationalStore>().ExecuteScript("""
                        DELETE FROM agui_message_memory;
                        DELETE FROM agui_agent_registrations;
                        DELETE FROM agui_group_reads;
                        DELETE FROM agui_messages;
                        DELETE FROM agui_topics;
                        DELETE FROM agui_group_members;
                        DELETE FROM agui_groups;
                        DELETE FROM agui_users;
                        DELETE FROM agui_sections;
                        DELETE FROM agui_usage
                        """);
                }
            }
            else
            {
                // 内存 / Redis 模式：清空各存储（Redis 的 ClearAll 会冲刷 agui:* 全部 key）
                sp.GetRequiredService<IGroupStore>().ClearAll();
                sp.GetRequiredService<IUserStore>().ClearAll();
                sp.GetService<IUsageStore>()?.ClearAll(); // 用量统计（可选）
                if (provider != "redis")
                    sp.GetService<PersistenceService>()?.Reset(); // 删除 JSON 快照文件（仅 memory 模式）
            }

            // 内存对象（两种模式都清：数据库模式清完表后同步清内存缓存）
            sp.GetRequiredService<AuthService>().ClearSessions(); // 全部已登录端立即失效
            sp.GetRequiredService<AgentRegistry>().Clear();
            sp.GetRequiredService<AgentCatalog>().RestoreAll([]);
            sp.GetRequiredService<AgentCatalog>().InvalidateAll();
            sp.GetRequiredService<KnowledgeBaseCatalog>().RestoreAll([]);
            sp.GetService<AgentGateway>()?.ClearBridgeCursors();
            sp.GetRequiredService<AttachmentStore>().ClearAll();
            sp.GetService<IMessageMemoryStore>()?.ClearAll();

            // 模型配置复位（再次进入系统时重新弹窗填写）
            var modelConfig = sp.GetRequiredService<ModelConfigState>();
            modelConfig.IsConfigured = false;
            modelConfig.Endpoint = null;
            modelConfig.ApiKey = null;
            modelConfig.ThinkingMode = true; // 思考模式复位为默认开启
            ApplyModelConfig(sp.GetRequiredService<AgentOptions>(), modelConfig, sp.GetRequiredService<AgentCatalog>());
            sp.GetRequiredService<ChangeHub>().Notify();

            sp.GetRequiredService<AguiGroupChat.Hub.Infra.AuditLogService>()
                .Record("data.reset", meId, auth.GetUser(meId)?.Username, detail: "系统初始化（清空全部数据）");

            return Results.Ok(new { reset = true });
        });
    }

    /// <summary>
    /// 注册模型配置到持久化扩展区「modelConfig」：重启后恢复用户填写的 endpoint / apiKey。
    /// 须在应用构建后、状态恢复（InitializePersistence）之前调用（Web 组合根）。
    /// </summary>
    public static void RegisterModelConfigPersistence(this IServiceProvider services)
    {
        var state = services.GetRequiredService<ModelConfigState>();
        var options = services.GetRequiredService<AgentOptions>();
        var catalog = services.GetRequiredService<AgentCatalog>();
        Func<object?> snapshot = () => state;
        Action<JsonElement> restore = element =>
        {
            var saved = element.Deserialize<ModelConfigState>(AguiJson.Options);
            if (saved is null) return;
            state.IsConfigured = saved.IsConfigured;
            state.Endpoint = saved.Endpoint;
            state.ApiKey = saved.ApiKey;
            state.ThinkingMode = saved.ThinkingMode;
            ApplyModelConfig(options, state, catalog);
        };

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
        {
            persistence.AddSection("modelConfig", snapshot, restore);
        }
        else
        {
            services.GetService<ISectionStore>()?.AddSection("modelConfig", snapshot, restore);
        }
    }

    /// <summary>把状态应用到运行时 AgentOptions（生效无需重启）：endpoint 留空 → 官方端点；apiKey 留空 → 环境变量。</summary>
    private static void ApplyModelConfig(AgentOptions options, ModelConfigState state, AgentCatalog catalog)
    {
        options.Endpoint = state.Endpoint;
        // apiKey 留空：回退进程环境变量；环境变量也没有时保留现有配置值
        // （密钥可能仅经 Agents__ApiKey 配置注入，进程环境未必有 DEEPSEEK_API_KEY，不能置空）
        options.ApiKey = string.IsNullOrWhiteSpace(state.ApiKey)
            ? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
              ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
              ?? options.ApiKey
            : state.ApiKey;
        options.ThinkingMode = state.ThinkingMode;
        catalog.InvalidateAll(); // 下次触发按新配置重建 ChatClient
    }
}

/// <summary>模型配置保存请求体。</summary>
public sealed record ModelConfigHttpRequest(string? Endpoint, string? ApiKey, bool? ThinkingMode = null);
