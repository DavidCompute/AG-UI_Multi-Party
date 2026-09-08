using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 账号注销（数据主体权利 · 企业合规）HTTP API：
///   DELETE /ag-ui/account —— 注销当前登录账号：要求提交登录密码确认（防会话劫持 / 误删），
///   注销执行完整数据擦除（创建的知聚转让 / 解散、加入的知聚退群、发言记忆与个人知识库清除、账号行删除），
///   随后该账号全部会话即时失效。最后一名超级管理员不可注销（防平台失去最高管理入口）。
/// </summary>
public static class AccountApi
{
    public static void MapAccountApi(this WebApplication app)
    {
        app.MapDelete("/ag-ui/account", async ([Microsoft.AspNetCore.Mvc.FromBody] AccountDeleteHttpRequest req, HttpContext ctx,
            [Microsoft.AspNetCore.Mvc.FromServices] AccountErasureService erasure,
            [Microsoft.AspNetCore.Mvc.FromServices] AuthService auth, CancellationToken ct) =>
        {
            try
            {
                var userId = WebIdentity.UserId(ctx)!;
                if (!auth.VerifyPassword(userId, req.Password ?? ""))
                    return Results.Json(new AguiError(ErrorCodes.UserPasswordInvalid, "密码不正确，无法注销账户"),
                        statusCode: StatusCodes.Status401Unauthorized);
                var report = await erasure.EraseAsync(userId, userId, "用户自助注销", ct);
                return Results.Ok(new
                {
                    ok = true,
                    accountRemoved = report.AccountRemoved,
                    groupsHandled = report.GroupsHandled,
                    memoriesErased = report.MemoriesErased,
                    knowledgeBasesRemoved = report.KnowledgeBasesRemoved,
                });
            }
            catch (AguiProtocolException ex) { return MapError(ex); }
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }

    private static IResult MapError(AguiProtocolException ex) => ex.ErrorCode switch
    {
        ErrorCodes.UserNotFound => Results.NotFound(new AguiError(ex.ErrorCode, ex.Message)),
        ErrorCodes.UserUnauthorized or ErrorCodes.UserPasswordInvalid
            => Results.Json(new AguiError(ex.ErrorCode, ex.Message), statusCode: StatusCodes.Status401Unauthorized),
        ErrorCodes.GroupPermissionDenied
            => Results.Json(new AguiError(ex.ErrorCode, ex.Message), statusCode: StatusCodes.Status403Forbidden),
        _ => Results.BadRequest(new AguiError(ex.ErrorCode, ex.Message)),
    };
}

/// <summary>注销账户请求体：需提交当前登录密码确认。</summary>
public sealed record AccountDeleteHttpRequest(string? Password);
