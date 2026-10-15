using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 智能体 / 技能市场（3.3）——内置目录 + <b>在线分发</b>：
///   GET    /ag-ui/marketplace                              —— 聚合目录（内置包 + 各远程源拉取的包列表）
///   POST   /ag-ui/marketplace/import/{packId}              —— 一键导入内置包
///   POST   /ag-ui/marketplace/install                      —— 安装远程包 { sourceId, packId }
///   POST   /ag-ui/marketplace/publish                      —— 把自建数字员工导出为可分发包 JSON
///   GET    /ag-ui/admin/marketplace/sources                —— 列出目录源（管理员）
///   POST   /ag-ui/admin/marketplace/sources                —— 新增目录源（管理员）
///   DELETE /ag-ui/admin/marketplace/sources/{id}           —— 删除目录源（管理员）
/// 安装统一走 <see cref="MarketplaceService.ImportDefinitions"/>（两趟 ID + 包内引用重映射）。
/// </summary>
public static class MarketplaceApi
{
    public static void MapMarketplaceApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/marketplace");

        // ---- 聚合目录：内置 + 远程（远程失败不致命，逐源回报）----
        root.MapGet("/", async (MarketplaceService market, MarketplaceState state,
            MarketplaceRemoteService remote, CancellationToken ct) =>
        {
            var builtin = market.Packs().Select(p => new
            {
                p.PackId,
                p.Name,
                p.Description,
                agentCount = p.Agents.Count,
                remote = false,
                sourceId = (string?)null,
                sourceName = (string?)null,
                agents = p.Agents.Select(a => new { agentId = a.AgentId, a.Nickname, a.Description }).ToList(),
            }).ToList();

            var remoteSources = new List<object>();
            foreach (var src in state.Snapshot())
            {
                if (!src.Enabled)
                {
                    remoteSources.Add(new { sourceId = src.Id, sourceName = src.Name, url = src.Url, enabled = false, ok = false, error = (string?)null, packs = Array.Empty<object>() });
                    continue;
                }
                var res = await remote.FetchCatalogAsync(src, ct);
                remoteSources.Add(new
                {
                    sourceId = src.Id,
                    sourceName = src.Name,
                    url = src.Url,
                    enabled = true,
                    ok = res.Ok,
                    error = res.Error,
                    packs = res.Packs.Select(p => new
                    {
                        p.PackId, p.Name, p.Description, agentCount = p.AgentCount,
                        remote = true, sourceId = src.Id, sourceName = src.Name,
                        agents = Array.Empty<object>(),
                    }).ToList(),
                });
            }
            return Results.Ok(new { builtin, remote = remoteSources });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 安装前预览：返回某包的「数字员工清单」（内置直接给出；远程按需拉整包，不落库）----
        root.MapGet("/preview", async (string? sourceId, string? packId, MarketplaceService market,
            MarketplaceState state, MarketplaceRemoteService remote, CancellationToken ct) =>
        {
            var pid = (packId ?? "").Trim();
            if (pid.Length == 0) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "packId 必填"));
            var sid = (sourceId ?? "").Trim();

            string name, description;
            IReadOnlyList<AgentDefinition> agents;
            if (sid.Length == 0)
            {
                var pack = market.Packs().FirstOrDefault(p => string.Equals(p.PackId, pid, StringComparison.Ordinal));
                if (pack is null) return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "内置包不存在"));
                name = pack.Name; description = pack.Description; agents = pack.Agents;
            }
            else
            {
                var src = state.Get(sid);
                if (src is null || !src.Enabled)
                    return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "目录源不存在或已停用"));
                MarketplacePackDocument doc;
                try { doc = await remote.FetchPackAsync(src, pid, ct); }
                catch (AguiProtocolException ex) { return Results.BadRequest(new AguiError(ex.ErrorCode, ex.Message)); }
                catch (Exception ex) { return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"预览失败：{ex.Message}")); }
                name = doc.Name; description = doc.Description; agents = doc.Agents;
            }

            return Results.Ok(new
            {
                packId = pid,
                name,
                description,
                agentCount = agents.Count,
                remote = sid.Length > 0,
                sourceId = sid.Length > 0 ? sid : null,
                agents = agents.Select(a => new
                {
                    agentId = a.AgentId,
                    a.Nickname,
                    a.Description,
                    skillCount = a.SkillDefIds?.Count ?? 0,
                    memoryType = a.MemoryProfile?.MemoryType,
                    triggerMode = a.TriggerMode.ToString().ToLowerInvariant(),
                    knowledgeBaseCount = a.KnowledgeBaseIds?.Count ?? 0,
                }).ToList(),
            });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 一键导入内置包（可只导子集：body { agentIds }；空 / 缺省 = 整包）----
        root.MapPost("/import/{packId}", (string packId, MarketplaceImportRequest? req, HttpContext ctx, MarketplaceService market, ChangeHub changes) =>
        {
            var userId = WebIdentity.UserId(ctx)!;
            var pack = market.Get(packId);
            if (pack is null) return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "角色包不存在"));
            var agents = FilterAgents(pack.Agents, req?.AgentIds);
            if (agents is null) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "所选数字员工为空"));
            var result = market.ImportDefinitions(pack.PackId, pack.Name, agents, userId);
            changes.Notify();
            return Results.Ok(new { ok = true, result.PackId, result.PackName, result.AgentsCreated });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 安装远程包 ----
        root.MapPost("/install", async (MarketplaceInstallRequest req, HttpContext ctx, AuthService auth,
            MarketplaceService market, MarketplaceState state, MarketplaceRemoteService remote,
            ChangeHub changes, AuditLogService audit, CancellationToken ct) =>
        {
            var userId = WebIdentity.UserId(ctx)!;
            var src = state.Get((req.SourceId ?? "").Trim());
            if (src is null || !src.Enabled)
                return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "目录源不存在或已停用"));

            MarketplacePackDocument doc;
            try { doc = await remote.FetchPackAsync(src, (req.PackId ?? "").Trim(), ct); }
            catch (AguiProtocolException ex) { return Results.BadRequest(new AguiError(ex.ErrorCode, ex.Message)); }
            catch (Exception ex) { return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"安装失败：{ex.Message}")); }

            foreach (var a in doc.Agents) SanitizeInstalled(a);
            var agents = FilterAgents(doc.Agents, req.AgentIds);
            if (agents is null) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "所选数字员工为空"));
            var result = market.ImportDefinitions(doc.PackId, doc.Name, agents, userId);
            changes.Notify();
            audit.Record("marketplace.install", userId, auth.GetUser(userId)?.Username, targetType: "pack", targetId: doc.PackId,
                detail: $"从「{src.Name}」安装 {result.AgentsCreated} 个数字员工");
            return Results.Ok(new { ok = true, result.PackId, result.PackName, result.AgentsCreated, sourceId = src.Id });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 发布：把自建数字员工导出为可分发包（JSON，可自行托管）----
        root.MapPost("/publish", (MarketplacePublishRequest req, HttpContext ctx, AgentCatalog catalog) =>
        {
            var userId = WebIdentity.UserId(ctx)!;
            var ids = (req.AgentIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count == 0) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "至少选择一个数字员工"));
            if (ids.Count > 100) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "单包最多 100 个数字员工"));

            var picked = new List<AgentDefinition>();
            foreach (var id in ids)
            {
                var def = catalog.GetDefinition(id);
                if (def is null) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"数字员工不存在：{id}"));
                if (!string.Equals(def.OwnerId, userId, StringComparison.Ordinal))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, $"只能发布自己创建的数字员工：{id}"),
                        statusCode: StatusCodes.Status403Forbidden);
                picked.Add(def);
            }

            var name = (req.Name ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "包名必填"));
            var doc = new MarketplacePackDocument
            {
                PackId = string.IsNullOrWhiteSpace(req.PackId) ? "pack_" + IdGenerator.NewId() : req.PackId!.Trim(),
                Name = name,
                Description = (req.Description ?? "").Trim(),
                Agents = picked.Select(StripInstanceFields).ToList(),
            };
            return Results.Json(doc, AguiJson.Options);
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 目录源管理（管理员）----
        var admin = app.MapGroup("/ag-ui/admin/marketplace/sources");
        admin.MapGet("/", (MarketplaceState state) => Results.Ok(state.Snapshot()
            .Select(s => new { s.Id, s.Name, s.Url, s.Enabled, s.CreatedAt, s.CreatedBy })))
            .AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        admin.MapPost("/", (MarketplaceSourceRequest req, HttpContext ctx, AuthService auth,
            MarketplaceState state, ChangeHub changes, AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var name = (req.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > 60) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "名称必填且 ≤60 字符"));
            var (error, url) = ValidateCatalogUrl(req.Url);
            if (error is not null) return error;

            var source = new MarketplaceSource
            {
                Id = "ms_" + IdGenerator.NewId(),
                Name = name,
                Url = url!,
                Enabled = req.Enabled ?? true,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CreatedBy = me,
            };
            state.Add(source);
            changes.Notify();
            audit.Record("marketplace.source.add", me, auth.GetUser(me)?.Username, targetType: "source", targetId: source.Id,
                detail: $"新增市场目录源 {source.Url}");
            return Results.Ok(new { source.Id, source.Name, source.Url, source.Enabled, source.CreatedAt });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        admin.MapDelete("/{id}", (string id, HttpContext ctx, AuthService auth,
            MarketplaceState state, ChangeHub changes, AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            if (!state.Remove(id)) return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "目录源不存在"));
            changes.Notify();
            audit.Record("marketplace.source.remove", me, auth.GetUser(me)?.Username, targetType: "source", targetId: id,
                detail: "删除市场目录源");
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());
    }

    /// <summary>按 agentIds 过滤要导入的数字员工（null / 空 = 整包）；返回 null 表示「显式给了名单但一个都不匹配」。</summary>
    private static IReadOnlyList<AgentDefinition>? FilterAgents(IReadOnlyList<AgentDefinition> all, string[]? agentIds)
    {
        var ids = (agentIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return all;
        var picked = all.Where(a => ids.Contains(a.AgentId)).ToList();
        return picked.Count == 0 ? null : picked;
    }

    /// <summary>安装时的安全净化：外部包不得带入桥接端点 / 令牌、他实例的用户组 id、私有 / 技能目标标记。</summary>
    private static void SanitizeInstalled(AgentDefinition d)
    {
        d.BridgeEndpoint = null;
        d.BridgeToken = null;
        d.BridgeMode = null;
        d.AllowedGroupIds = null;
        d.IsPrivate = false;
        d.IsSkillTarget = false;
        d.OwnerId = null; // 由导入方设置
    }

    /// <summary>发布时的净化：剥离实例专属字段与凭据（不随包分发）。</summary>
    private static AgentDefinition StripInstanceFields(AgentDefinition d)
    {
        var copy = JsonSerializer.Deserialize<AgentDefinition>(JsonSerializer.Serialize(d, AguiJson.Options), AguiJson.Options)!;
        copy.OwnerId = null;
        copy.AllowedGroupIds = null;
        copy.BridgeEndpoint = null;
        copy.BridgeToken = null;
        copy.BridgeMode = null;
        copy.IsSkillTarget = false;
        return copy;
    }

    private static (IResult? Error, string? Url) ValidateCatalogUrl(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return (Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "目录地址不能为空")), null);
        if (u.Length > 2000) return (Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "目录地址过长")), null);
        if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return (Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "目录地址需为 http / https 绝对 URL")), null);
        return (null, parsed.ToString());
    }

    /// <summary>注册市场目录源到持久化扩展区「marketplace」：重启后恢复。</summary>
    public static void RegisterMarketplacePersistence(this IServiceProvider services)
    {
        var state = services.GetRequiredService<MarketplaceState>();
        Func<object?> snapshot = () => state.Snapshot().Select(s => (object)s).ToList();
        Action<JsonElement> restore = element =>
        {
            var saved = element.Deserialize<List<MarketplaceSource>>(AguiJson.Options) ?? [];
            state.ReplaceAll(saved);
        };

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
            persistence.AddSection("marketplace", snapshot, restore);
        else
            services.GetService<ISectionStore>()?.AddSection("marketplace", snapshot, restore);
    }
}

/// <summary>安装远程包请求体（agentIds 为空 / 缺省 = 整包）。</summary>
public sealed record MarketplaceInstallRequest(string? SourceId, string? PackId, string[]? AgentIds = null);

/// <summary>导入内置包请求体（agentIds 为空 / 缺省 = 整包）。</summary>
public sealed record MarketplaceImportRequest(string[]? AgentIds);

/// <summary>发布分发包请求体。</summary>
public sealed record MarketplacePublishRequest(string? PackId, string? Name, string? Description, string[]? AgentIds);

/// <summary>新增目录源请求体。</summary>
public sealed record MarketplaceSourceRequest(string? Name, string? Url, bool? Enabled);
