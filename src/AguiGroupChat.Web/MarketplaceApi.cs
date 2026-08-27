using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 智能体 / 技能市场 API（3.3）：
///   GET  /ag-ui/marketplace —— 可选角色包目录
///   POST /ag-ui/marketplace/import/{packId} —— 一键导入为当前用户的智能体（agentId 冲突自动改 ID）
/// </summary>
public static class MarketplaceApi
{
    public static void MapMarketplaceApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/marketplace");

        // ---- 目录 ----
        root.MapGet("/", (HttpContext ctx, MarketplaceService market) =>
        {
            var userId = WebIdentity.UserId(ctx)!; // 身份已由 RequireIdentityFilter 解析校验
            var packs = market.Packs().Select(p => new
            {
                p.PackId,
                p.Name,
                p.Description,
                agentCount = p.Agents.Count,
                agents = p.Agents.Select(a => new { agentId = a.AgentId, a.Nickname, a.Description }).ToList(),
            });
            return Results.Ok(packs);
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 一键导入 ----
        root.MapPost("/import/{packId}", (string packId, HttpContext ctx, MarketplaceService market) =>
        {
            var userId = WebIdentity.UserId(ctx)!; // 身份已由 RequireIdentityFilter 解析校验
            var result = market.ImportPack(packId, userId);
            return Results.Ok(new { ok = true, result.PackId, result.PackName, result.AgentsCreated });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }
}
