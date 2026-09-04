using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 技能库管理 HTTP API（OpenClaw 风格可复用技能）：技能定义（shell / http / prompt）的
/// 新增 / 更新 / 删除 / 试运行，供前端「技能库」管理弹窗与「数字员工自建技能」共用。
/// 技能库是全局可复用的技能目录：任何数字员工经 <see cref="AgentUpsertHttpRequest.SkillDefIds"/> 挂载引用。
/// </summary>
public static class SkillApi
{
    public static void MapSkillApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/skills");

        // ---- 技能库列表（需登录；技能正文 / 解释器 / HTTP 配置仅归属者或管理员可见，避免脚本/密钥泄露）----
        root.MapGet("/", (HttpContext ctx, AuthService auth, AgentSkillCatalog catalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var isAdmin = auth.IsAdmin(user.UserId);
            return Results.Ok(catalog.ListAll().Select(s => ToDto(s, s.OwnerId == user.UserId || isAdmin)));
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 用自然语言生成技能配置（无需手填各字段）：输入需求，由大模型产出结构化技能定义，前端据此填入表单 ----
        root.MapPost("/generate", async (SkillGenerateRequest req, HttpContext ctx, AuthService auth, AgentOptions options, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Request)) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "需求描述不能为空"));
            try
            {
                var isAdmin = auth.IsAdmin(user.UserId);
                var preferClient = req.PreferClient == true;
                var runEnv = preferClient
                    ? DescribeClientEnv(req.ClientOs)
                    : DescribeServerEnv();
                var gen = await SkillDefinitionGenerator.GenerateAsync(
                    options, req.Request, preferClient, isAdmin, loggerFactory.CreateLogger("SkillApi.Generate"), ct, runEnv);
                return Results.Ok(new
                {
                    generated = true,
                    skillId = gen.SkillId,
                    name = gen.Name,
                    kind = gen.Kind,
                    description = gen.Description,
                    body = gen.Body,
                    executionLocation = gen.ExecutionLocation,
                    clientRunner = gen.ClientRunner,
                    requiresApproval = gen.RequiresApproval,
                    targetEnv = runEnv,
                });
            }
            catch (OperationCanceledException) { return Results.Json(new AguiError(ErrorCodes.BadRequest, "生成已取消或超时"), statusCode: StatusCodes.Status408RequestTimeout); }
            catch (Exception ex) { return Results.Json(new AguiError(ErrorCodes.BadRequest, "生成失败：" + ex.Message), statusCode: StatusCodes.Status400BadRequest); }
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 新增技能（需登录；Shell / HTTP 技能的创建仅限管理员——它们的执行可能触发任意命令 / 外部请求，
        //      普通用户只能创建纯提示词技能，避免「自建 shell 技能自 run」的任意命令执行面）----
        root.MapPost("/", (SkillDefHttpRequest req, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var isAdmin = auth.IsAdmin(user.UserId);
            if (RequiresPrivilegedKind(req.Kind) && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, $"仅管理员可创建 {req.Kind} 技能"), statusCode: StatusCodes.Status403Forbidden);
            var (def, err) = BuildDef(req, ownerId: user.UserId, authorAdmin: isAdmin);
            if (err is not null) return err;
            var skill = def!; // BuildDef 保证 err 非空时 def 为空、err 为空时 def 非空
            if (catalog.Contains(skill.SkillId))
                return Results.Json(new AguiError(ErrorCodes.SkillExists, $"技能「{skill.SkillId}」已存在"), statusCode: StatusCodes.Status409Conflict);
            catalog.Upsert(skill);
            return Results.Ok(new { created = true, skillId = skill.SkillId });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 更新技能（技能归属者或系统管理员）；Shell / HTTP 技能非管理员不可改（含把 Prompt 改成 Shell 提权）----
        root.MapPut("/{skillId}", (string skillId, SkillDefHttpRequest req, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var isAdmin = auth.IsAdmin(user.UserId);
            var existing = catalog.Get(skillId);
            if (existing is null)
                return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            if (existing.OwnerId is null)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能只读，请导出后另建"), statusCode: StatusCodes.Status403Forbidden);
            if (!isAdmin && existing.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可编辑该技能"), statusCode: StatusCodes.Status403Forbidden);
            if (RequiresPrivilegedKind(req.Kind) && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, $"仅管理员可把技能改为 {req.Kind} 类型"), statusCode: StatusCodes.Status403Forbidden);
            var (def, err) = BuildDef(req, ownerId: existing.OwnerId, authorAdmin: isAdmin);
            if (err is not null) return err;
            var skill = def!; // BuildDef 保证 err 非空时 def 为空、err 为空时 def 非空
            skill.SkillId = skillId; // ID 用 URL 的，不允许改名
            catalog.Upsert(skill);
            return Results.Ok(new { updated = true, skillId = skill.SkillId });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 删除技能（技能归属者或系统管理员）；已被数字员工引用的技能一并解除（按引用的 agent 引用清理在下文做，暂无外键）----
        root.MapDelete("/{skillId}", (string skillId, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var existing = catalog.Get(skillId);
            if (existing is null)
                return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            if (existing.OwnerId is null)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能只读，请导出后另建"), statusCode: StatusCodes.Status403Forbidden);
            if (!auth.IsAdmin(user.UserId) && existing.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可删除该技能"), statusCode: StatusCodes.Status403Forbidden);
            catalog.Remove(skillId);
            return Results.Ok(new { deleted = true, skillId });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 试运行技能（仅归属者或管理员；系统技能仅管理员）----
        //      /run 是无审批通道的手动执行，不能让它被任意登录用户触发 shell / HTTP；
        //      归属者运行自己建的 prompt 技能用于调试验证，shell / HTTP 则限定管理员与归属者。
        root.MapPost("/{skillId}/run", async (string skillId, SkillRunHttpRequest req, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog, AgentCatalog agents, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            // 本机桥 / 桌面自托管标记：经 DI 取得（测试宿主可为空）。
            // IsHostLocal=true 表示“宿主即用户本机”（桌面版），Client 技能可在 Web 宿主直接执行、无需独立本机桥。
            var nativeTunnel = ctx.RequestServices.GetService<AguiGroupChat.Agents.NativeTunnelService>();
            var hostLocal = ctx.RequestServices.GetService<ClientToolOptions>()?.IsHostLocal == true;
            var hostEnv = ctx.RequestServices.GetService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            var existing = catalog.Get(skillId);
            if (existing is null)
                return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            // dotnet 技能：（建立限管理员，运行面向任意登录用户）。
            // server → 服务端 Roslyn 编译执行；client → 本机执行：优先按请求上报的 client（当前浏览器机器），
            // 否则落到平台级桥（一座桥的机器即本机）在本机编译运行；无可用桥才给出说明。
            if (existing.Kind == AgentSkillKind.Dotnet)
            {
                if (existing.ExecutionLocation == AgentSkillExecutionLocation.Client)
                {
                    var source = existing.Body ?? "";
                    var query = req.Query ?? "";
                    // 桌面版/自托管（宿主=用户本机）：直接在 Web 宿主上进程内跑 Roslyn，无需独立本机桥。
                    if (hostLocal && hostEnv is not null)
                    {
                        var hostDr = await agents.RunSkillAsync(existing, query, ct);
                        return Results.Ok(new { skillId, result = ("【本机 dotnet · 在桌面宿主机直接执行】\n" + hostDr), localOnly = true });
                    }
                    var clientId = (req.ClientId ?? "").Trim();
                    if (nativeTunnel is not null && clientId.Length > 0 && nativeTunnel.HasClient(clientId))
                    {
                        // 客户端本机 dotnet 必须经“发起请求的这台浏览器所在机器”的本机桥执行
                        var localResult = await nativeTunnel.ExecuteDotnetForClientAsync(
                            clientId, source, query, TimeSpan.FromSeconds(160), ct);
                        if (!string.IsNullOrWhiteSpace(localResult))
                            return Results.Ok(new { skillId, result = ("【本机 dotnet · 经本机桥执行】client=" + clientId + " 结果：\n" + localResult), localOnly = true });
                    }
                    // 没有为该请求机器找到桥：明确报告，而非回退到可能并非请求机器的其它桥
                    return Results.Ok(new { skillId, result = ("执行失败，没有安装桥：本客户端技能需在发起请求的浏览器所在机器执行，\n"
                        + "但该机器未连接本机桥（找不到 client=" + (clientId.Length == 0 ? "（未上报）" : clientId) + "）。请在本机安装/启动 AguiGroupChat.NativeBridge 后重试。"), localOnly = true });
                }
                var dr = await agents.RunSkillAsync(existing, req.Query ?? "", ct);
                return Results.Ok(new { skillId, result = dr });
            }
            // shell + client（本机执行技能）：试运行应经本机桥在本机（当前机器）执行，落到服务端 bash 会因缺 PowerShell 命令而 127。
            if (existing.Kind == AgentSkillKind.Shell && existing.ExecutionLocation == AgentSkillExecutionLocation.Client)
            {
                bool isAdminShell = auth.IsAdmin(user.UserId);
                bool byThisUser = existing.OwnerId is not null && (existing.OwnerId == user.UserId || isAdminShell);
                bool systemByAdmin = existing.OwnerId is null && isAdminShell;
                if (!byThisUser && !systemByAdmin)
                    return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅技能创建者或系统管理员可试运行该本机技能"), statusCode: StatusCodes.Status403Forbidden);
                var command = existing.Body ?? "";
                var query = req.Query ?? "";
                // 桌面版/自托管（宿主=用户本机 Win）：直接在宿主上用 PowerShell 执行，无需独立本机桥。
                if (hostLocal && hostEnv is not null)
                {
                    var root = Path.Combine(hostEnv.ContentRootPath, "data", "clienttoolruns", HostShell.SanitizeSegment(user.UserId));
                    var hostOut = await HostShell.RunAsync(root, command, null, 60, query, ct);
                    return Results.Ok(new { skillId, result = ("【本机 shell · 在桌面宿主机直接执行】\n" + hostOut), localOnly = true });
                }
                var clientIdShell = (req.ClientId ?? "").Trim();
                if (nativeTunnel is not null && clientIdShell.Length > 0 && nativeTunnel.HasClient(clientIdShell))
                {
                    // 客户端本机 shell 必须经“发起请求的这台浏览器所在机器”的本机桥执行
                    var shellLocal = await nativeTunnel.ExecuteForClientAsync(
                        clientIdShell, command, null, 60, query, TimeSpan.FromSeconds(120), ct);
                    if (!string.IsNullOrWhiteSpace(shellLocal))
                        return Results.Ok(new { skillId, result = ("【本机 shell · 经本机桥执行】client=" + clientIdShell + " 结果：\n" + shellLocal), localOnly = true });
                }
                // 该请求机器没有桥：明确报告，而非回退到可能并非请求机器的其它桥
                return Results.Ok(new { skillId, result = ("执行失败，没有安装桥：本客户端 shell 技能需在发起请求的浏览器所在机器执行，\n"
                    + "但该机器未连接本机桥（找不到 client=" + (clientIdShell.Length == 0 ? "（未上报）" : clientIdShell) + "）。请在本机安装/启动 AguiGroupChat.NativeBridge 后重试。"), localOnly = true });
            }
            // 任何仍带 ExecutionLocation=Client 的技能（http / prompt 等）试运行都不应落服务端跑——
            // 它们应在其“本机/前端或桥所在机器”执行。这里既没有浏览器可驱动 http，也没有 dotnet/shell 的桥执行入口，
            // 因此给明确指引而非静默用服务端 Runner（会与本机语境错位）。
            if (existing.ExecutionLocation == AgentSkillExecutionLocation.Client)
            {
                var ownerGateOk = existing.OwnerId is not null ? (existing.OwnerId == user.UserId || auth.IsAdmin(user.UserId))
                    : auth.IsAdmin(user.UserId);
                if (!ownerGateOk)
                    return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可试运行该技能"), statusCode: StatusCodes.Status403Forbidden);
                var hintClient = "该技能标注为“本机(client)执行”，但 kind=" + existing.Kind + " 需要在其本机/前端或桥所在机器执行，\n"
                    + "技能库试运行无法在当前宿主代为驱动。请在挂载了本技能、且其机器装有/连接本机桥的数字员工对话中触发，\n"
                    + "由系统在你本机执行并回传；或把 executionLocation 改为 server 后在此试运行。\n\n正文：\n" + (existing.Body ?? "") + "\n\n请求：" + (req.Query ?? "");
                return Results.Ok(new { skillId, result = hintClient, localOnly = true });
            }
            // 其余为 Server 执行技能：系统技能无归属者 → 仅管理员可试运行；归属技能 → 归属者或管理员可试运行
            if (existing.OwnerId is not null && existing.OwnerId != user.UserId && !auth.IsAdmin(user.UserId))
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅技能创建者或系统管理员可试运行"), statusCode: StatusCodes.Status403Forbidden);
            if (existing.OwnerId is null && !auth.IsAdmin(user.UserId))
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅系统管理员可试运行系统技能"), statusCode: StatusCodes.Status403Forbidden);
            var result = await agents.RunSkillAsync(existing, req.Query ?? "", ct);
            return Results.Ok(new { skillId, result });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 试运行参数建议：按技能的说明 / 正文 / 类型，让大模型给一个典型示例 query，供前端试运行时预填（权限同 /run）----
        root.MapPost("/{skillId}/suggest", async (string skillId, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog, AgentOptions options, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var existing = catalog.Get(skillId);
            if (existing is null)
                return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            if (existing.OwnerId is not null && existing.OwnerId != user.UserId && !auth.IsAdmin(user.UserId))
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅技能创建者或系统管理员可试运行"), statusCode: StatusCodes.Status403Forbidden);
            if (existing.OwnerId is null && !auth.IsAdmin(user.UserId))
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅系统管理员可试运行系统技能"), statusCode: StatusCodes.Status403Forbidden);
            var suggestion = await SkillQuerySuggester.SuggestAsync(options, existing, loggerFactory.CreateLogger("SkillApi.Suggest"), ct);
            return Results.Ok(new { skillId, suggestion });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());
    }

    /// <summary>Shell / HTTP / .NET 技能属特权类型：创建 / 修改 / 运行仅限管理员（归属者试运行 prompt 由 /run 单独管控）。</summary>
    private static bool RequiresPrivilegedKind(string? kind)
        => Enum.TryParse<AgentSkillKind>(kind, true, out var k) && k is AgentSkillKind.Shell or AgentSkillKind.Http or AgentSkillKind.Dotnet or AgentSkillKind.Org_deploy;

    private static object ToDto(AgentSkillDefinition s, bool canReadBody)
    {
        var dto = new Dictionary<string, object?>
        {
            ["skillId"] = s.SkillId,
            ["name"] = s.Name,
            ["kind"] = s.Kind.ToString().ToLowerInvariant(),
            ["requiresApproval"] = s.RequiresApproval,
            ["executionLocation"] = s.ExecutionLocation.ToString().ToLowerInvariant(),
            ["ownerId"] = s.OwnerId,
        };
        if (canReadBody)
        {
            dto["description"] = s.Description;
            dto["body"] = s.Body;
            dto["parametersJson"] = s.ParametersJson;
            dto["interpreter"] = s.Interpreter;
            dto["httpTimeoutSeconds"] = s.HttpTimeoutSeconds;
            dto["clientRunner"] = s.ClientRunner;
        }
        return dto;
    }

    private static (AgentSkillDefinition? Def, IResult? Err) BuildDef(SkillDefHttpRequest req, string? ownerId, bool authorAdmin)
    {
        var rawId = (req.SkillId ?? "").Trim();
        var id = AgentSkillDefinition.IsValidAsciiToolId(rawId)
            ? rawId
            : AgentSkillDefinition.ToAsciiToolId(rawId, new HashSet<string>(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(id))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "技能标识不能为空")));
        if (string.IsNullOrWhiteSpace(req.Name))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "技能名称不能为空")));
        if (string.IsNullOrWhiteSpace(req.Description))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "技能描述不能为空（模型据此决定是否调用）")));
        var kind = Enum.TryParse<AgentSkillKind>(req.Kind, true, out var k) ? k : AgentSkillKind.Prompt;
        // Shell 技能强制需审批（任意本机命令执行面最大）；HTTP / 提示词技能按创建者的 RequiresApproval 决定
        //（是否允许调用本机 / 内网另由 Agents:AllowPrivateSkillEndpoints 控制，安全兜底与其解耦）
        var requiresApproval = kind is AgentSkillKind.Shell ? true : (req.RequiresApproval ?? true);
        // 客户端执行位置：默认服务端（现状不变）；Shell / HTTP 客户端技能一律强制需审批（本机执行 / 外部请求安全兜底）
        var executionLocation = Enum.TryParse<AgentSkillExecutionLocation>(req.ExecutionLocation, true, out var loc)
            && req.ExecutionLocation is not null
            ? loc
            : AgentSkillExecutionLocation.Server;
        if (kind == AgentSkillKind.Dotnet)
        {
            // .NET（C# 动态编译）技能：动态代码面最高 → 一律强制审批（安全兜底）。
            // 执行位置遵循用户选择：server = 服务端 Roslyn 编译执行；client = 经内网本机桥在该用户机器/内网机编译执行（浏览器本身不能跑通用 C#）。
            requiresApproval = true;
        }
        if (executionLocation == AgentSkillExecutionLocation.Client)
            requiresApproval = true; // 客户端执行（尤其 shell / dotnet）属本机/外部副作用，一律需人工批准
        if ((kind == AgentSkillKind.Shell || kind == AgentSkillKind.Dotnet) && string.IsNullOrWhiteSpace(req.Body))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "该技能正文（shell 命令/脚本，或 C# 源码）不能为空")));
        if (kind == AgentSkillKind.Http && string.IsNullOrWhiteSpace(req.Body))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "HTTP 技能的正文（JSON 配置）不能为空")));

        return (new AgentSkillDefinition
        {
            SkillId = id,
            Name = req.Name.Trim(),
            Description = req.Description.Trim(),
            Kind = kind,
            Body = req.Body ?? "",
            ParametersJson = req.ParametersJson ?? "",
            Interpreter = string.IsNullOrWhiteSpace(req.Interpreter) ? null : req.Interpreter.Trim(),
            HttpTimeoutSeconds = Math.Clamp(req.HttpTimeoutSeconds.GetValueOrDefault(30), 5, 120),
            RequiresApproval = requiresApproval,
            OwnerId = ownerId,
            ExecutionLocation = executionLocation,
            ClientRunner = AgentApi.BuildClientRunner(kind, executionLocation, req.Body ?? "", req.ClientRunner),
        }, null);
    }

    private static UserAccount? RequireUser(HttpContext ctx, AuthService auth) => AgentApi.RequireUser(ctx, auth);

    // ---- 生成技能时的「目标运行环境」描述：优先按用户偏好/上报的浏览器系统；否则回退为服务端宿主系统。----
    private static string NormalizeOs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "未知";
        var v = s.Trim().ToLowerInvariant();
        return v switch
        {
            "win" or "win32" or "windows" => "Windows",
            "mac" or "macos" or "darwin" or "osx" => "macOS",
            "linux" or "unix" => "Linux",
            _ => v,
        };
    }

    private static string DescribeServerEnv()
        => "目标运行于服务端（宿主系统：" + (OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux") + "）";

    private static string DescribeClientEnv(string? clientOs)
    {
        var os = NormalizeOs(clientOs);
        return os switch
        {
            "Windows" => "目标运行于本机（浏览器所在系统：Windows，用 PowerShell 脚本）",
            "macOS" => "目标运行于本机（浏览器所在系统：macOS，用 bash 脚本）",
            "Linux" => "目标运行于本机（浏览器所在系统：Linux，用 bash 脚本）",
            _ => "目标运行于本机（浏览器所在系统：未上报" + (string.IsNullOrWhiteSpace(clientOs) ? "" : "（" + clientOs + "）") + "）",
        };
    }

    private static IResult Unauthorized()
        => Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "未登录或令牌无效"), statusCode: StatusCodes.Status401Unauthorized);
}

/// <summary>技能库请求体：技能定义字段（与 <see cref="AgentSkillDefinition"/> 对齐，SkillId 为可选新标识，更新时用 URL）。</summary>
public sealed record SkillDefHttpRequest(
    string? SkillId,
    string Name,
    string Description,
    string Kind,
    string? Body,
    string? ParametersJson,
    string? Interpreter,
    int? HttpTimeoutSeconds,
    bool? RequiresApproval,
    string? ExecutionLocation = null,
    string? ClientRunner = null);

/// <summary>技能试运行请求体。ClientId：本机执行的机器标识（浏览器本机桥 /ag-ui/bridge/info 读取），用于把 client dotnet 送到当前机器执行。</summary>
public sealed record SkillRunHttpRequest(string Query = "", string? ClientId = null);

/// <summary>用自然语言生成技能配置的请求体。ClientOs：客户端/本机执行时的浏览器所在系统（windows/macos/linux/other），供生成对应脚本；服务端执行时后端自行推断。</summary>
public sealed record SkillGenerateRequest(string Request, bool? PreferClient = null, string? ClientOs = null);
