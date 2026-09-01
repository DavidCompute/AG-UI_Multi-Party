using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Transport;
using AguiGroupChat.Web;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Serialization;

// 关闭出站 HTTP 请求的 W3C trace 传播（不附加 traceparent 头）：部分 API 网关（如 DeepSeek）
// 对 traceparent 校验严格，曾出现带该头的请求被网关以 invalid header 拒绝（请求头被截断畸形）。
// 仅影响出站请求是否携带 trace 头，不影响功能与日志。
AppContext.SetSwitch("System.Net.Http.EnableActivityPropagation", false);

// 组合根：协议 Hub + MSAGENT 智能体网关 + 静态前端
var builder = HubApp.CreateBuilder(args);
HubApp.ConfigureServices(builder);
builder.Services.AddAgentFramework(builder.Configuration); // 以 MSAGENT 网关覆盖默认 NoopAgentGateway
builder.Services.AddSingleton<AgentScheduler>(); // 智能体定时任务（cron）调度器
builder.Services.AddSingleton<AguiGroupChat.Hub.Persistence.MessageRetentionService>(); // 消息保留策略（按天清理历史）
builder.Services.AddSingleton(new SystemApi.ModelConfigState()); // 运行时模型配置（endpoint / apiKey），持久化到扩展区 modelConfig
builder.Services.AddSingleton(new BrandingState()); // 白标 / 品牌化配置（6.4），持久化到扩展区「branding」
builder.Services.AddSingleton(new ConfigGovernanceState()); // 配置治理（6.3）：管理员持久化运维旋钮，持久化到扩展区「configGovernance」
builder.Services.AddSingleton(builder.Configuration.GetSection("LinkProxy").Get<LinkProxyOptions>() ?? new LinkProxyOptions()); // 链接代理配置（appsettings 的 LinkProxy 节）
builder.Services.AddSingleton(builder.Configuration.GetSection("ClientTool").Get<AguiGroupChat.Web.ClientToolOptions>() ?? new AguiGroupChat.Web.ClientToolOptions()); // 客户端技能本机桥配置（ClientTool 节：RequireAdmin 等）
builder.Services.AddSingleton<NativeTunnelService>(); // 内网本机桥反向隧道（HTTP/SSE）路由 + 执行等待
builder.Services.AddSingleton(builder.Configuration.GetSection("NativeTunnel").Get<NativeTunnelOptions>() ?? new NativeTunnelOptions()); // 隧道令牌等配置
builder.Services.AddSingleton(sp => new NativeTunnelRateLimitBag(sp.GetRequiredService<NativeTunnelOptions>())); // 隧道端点限流器
// 数据导出 / 导入 zip 可能包含大量附件：放宽 multipart 请求体限制（默认 30MB 会拒绝大包）；
// 200MB 为上限——更高的体积更可能用于撑爆内存 / 磁盘，导入侧另有 zip 炸弹防护（条目数 / 解压体积 / 单条目上限）
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 200L * 1024 * 1024);

// HTTP API 枚举字符串化（与协议 §2 一致）：请求/响应均接受 user/agent、owner/admin/normal 等字符串枚举。
// 统一 camelCase（JsonStringEnumConverter 带 naming policy），与 WS 事件（AguiJson）序列化一致，
// 避免 HTTP 与 WS 数据枚举大小写不一致导致前端匹配失败。
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();

app.UseDefaultFiles();
// 静态前端资源（index.html 直接引用 app.js / style.css / i18n，无版本号）：用 no-cache + ETag 确保每次请求都向后端
// 重新校验。装新安装包（文件时间戳变化）后 WebView2 / 浏览器必然拉到新文件，避免旧缓存的 app.js 导致改版不生效。
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate",
});

// 安全响应头：防 MIME 嗅探 / 防 iframe 嵌套 / 限制 referrer；附件下载 / 预览路径不设 CSP
// （避免影响 PDF / 图片内联渲染），其余页面设 CSP 防存储型 XSS 同源内联执行
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "same-origin";
    // iframe 嵌入（6.4）：配置 AllowedFrameOrigins 时放行指定来源，否则默认禁止任何站点嵌套
    var frameOrigins = app.Services.GetRequiredService<GroupChatOptions>().AllowedFrameOrigins
        ?.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    if (frameOrigins.Count == 0)
    {
        headers["X-Frame-Options"] = "DENY";
    }
    else
    {
        // X-Frame-Options 是逐域名且不支持通配/多源，改为 CSP frame-ancestors 承载多源；省略 X-Frame-Options 由 CSP 兜底
        headers.Remove("X-Frame-Options");
    }
    if (!ctx.Request.Path.StartsWithSegments("/ag-ui/files"))
    {
        var frameAncestors = frameOrigins.Count == 0 ? "'none'" : string.Join(" ", frameOrigins.Select(UriEscapeCspSource));
        headers["Content-Security-Policy"] =
            $"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
            $"img-src 'self' data: blob:; font-src 'self'; connect-src 'self' ws: wss: https: http:; " +
            $"frame-ancestors {frameAncestors}; base-uri 'self'; form-action 'self'; object-src 'none'";
    }
    await next();
});

static string UriEscapeCspSource(string src)
{
    // CSP frame-ancestors 需要 scheme 源：若用户填的是裸域名补 https:，其余按原样（带引号转义危险字符）
    var s = src.Trim();
    if (!s.Contains("://")) s = "https://" + s;
    return s.Replace("'", "").Replace(";", "").Replace("\r", "").Replace("\n", "");
}

HubApp.MapEndpoints(app);
app.MapAgentApi(); // 智能体目录 + 运行时可新增 / 更新 / 删除 AI 角色
app.MapTwinApi(); // 用户 AI 分身（启用 / 停用 / 查询）
app.MapAttachmentApi(); // 附件上传 / 下载（消息附件）
app.MapLinkProxyApi(); // 链接代理：智能体回复中的 http/https 链接由 Hub 代访后返回前端
app.MapExportImportApi(); // 数据导出 / 导入：账号 + 智能体 + 聊天记录（含附件）
app.MapKnowledgeBaseApi(); // 知识库：创建 / 上传文档 / 绑定智能体
app.MapSkillApi(); // 技能库（可复用技能：shell / http / prompt）CRUD + 试运行
app.MapClientToolBridgeApi(); // 客户端执行技能的 shell 本机桥（登录用户执行，沙箱 + 超时）
app.MapNativeTunnelApi(); // 内网本机桥反向隧道入口（HTTP/SSE）：桥连入 + 结果回传
app.MapGroupNameApi(); // 群名自动生成（创建群不填名字时）
app.MapSystemApi(); // 系统级：模型配置（endpoint / apiKey）+ 初始化（清空一切）
app.MapBrandingApi(); // 白标 / 品牌化（6.4）：应用名 + Logo + 主色（管理员可配置）
app.MapMemoryApi(); // 记忆治理：分群分级 / 自动遗忘 / 可视化
app.MapScheduledTaskApi(); // 重复性定时任务（1.4）：按 cron 值班汇报
app.MapMarketplaceApi(); // 智能体 / 技能市场（3.3）：内置角色包一键导入
app.MapAdminApi();  // 管理员控制台：用户管理（禁用 / 重置密码）+ 系统状态
app.MapConfigGovernanceApi(); // 配置治理（6.3）：管理员在线调整并持久化运维参数

// 智能体目录 / 知识库 / 登录会话 / 外部 AG-UI 增量游标接入统一持久化（须在状态恢复之前注册）
app.Services.RegisterAgentPersistence();
app.Services.RegisterKnowledgeBasePersistence();
app.Services.RegisterSkillPersistence(); // 技能库（可复用技能定义）跨重启保持
app.Services.RegisterSessionPersistence(); // 会话跨重启保持：桌面版 / 服务重启后「保持登录」仍有效
app.Services.RegisterBridgeCursorPersistence(); // 外部 AG-UI 话题增量游标跨重启保持
app.Services.RegisterModelConfigPersistence(); // 运行时模型配置（endpoint / apiKey）跨重启保持
app.Services.RegisterScheduledTaskPersistence(); // 重复性定时任务配置跨重启保持
app.Services.RegisterTotpPersistence(); // TOTP 二次验证密钥跨重启保持
app.Services.RegisterBrandingPersistence(); // 白标 / 品牌化配置（6.4）跨重启保持
app.Services.RegisterConfigGovernancePersistence(); // 配置治理覆盖（6.3）跨重启保持

// 恢复持久化状态；无历史数据且开启示例数据时才播种
var loaded = HubApp.InitializePersistence(app);
if (!loaded && app.Services.GetRequiredService<GroupChatOptions>().SeedSampleData)
    await HubApp.SeedSampleDataAsync(app);

// 为配置中声明的智能体注册群内触发规则（协议 §6）
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

app.Run();
