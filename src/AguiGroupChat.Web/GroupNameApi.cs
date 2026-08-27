using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 群名自动生成 API：创建群时用户不填名字，按已选成员昵称由模型生成 6-12 字群名。
/// 复用 AgentApi 的登录校验与错误码约定。
/// </summary>
public static class GroupNameApi
{
    public static void MapGroupNameApi(this WebApplication app)
    {
        app.MapPost("/ag-ui/group/generate-name", async (GroupNameGenerateRequest req, HttpContext ctx,
            AuthService auth, AgentOptions agentOptions, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();

            var names = (req.MemberNames ?? [])
                .Select(n => (n ?? "").Trim())
                .Where(n => n.Length > 0)
                .Take(8)
                .ToList();
            if (names.Count == 0)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "请先选择成员，再自动生成群名"));

            try
            {
                var groupName = await GroupNameGenerator.GenerateAsync(
                    agentOptions, names, loggerFactory.CreateLogger("GroupNameGen"), ct);
                return Results.Ok(new { groupName });
            }
            catch (Exception ex)
            {
                return Results.Json(new AguiError(ErrorCodes.BadRequest, "群名生成失败：" + ex.Message),
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());
    }
}

/// <summary>群名自动生成请求：已选成员昵称列表（用于生成贴切的群名）。</summary>
public sealed record GroupNameGenerateRequest(IReadOnlyList<string>? MemberNames);
