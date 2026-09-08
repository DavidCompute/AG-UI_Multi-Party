using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Storage;
using Microsoft.AspNetCore.Http;

namespace AguiGroupChat.Web;

/// <summary>
/// 话题滚动小结的读取接口（长话题接续记忆）：
///   GET /ag-ui/group/{groupId}/topic-summary?topicId=main —— 返回该话题最近一次自动生成的小结正文。
/// 小结由数字员工在“话题新增消息达阈值后的下一次回复”时自动生成/更新；本端点仅读取（前端展示）。
/// 鉴权：登录且为群成员（客服知聚的顾客参与者不提供——小结按普通群会话隔离生成）。
/// </summary>
public static class TopicSummaryApi
{
    public static void MapTopicSummaryApi(this WebApplication app)
    {
        app.MapGroup("/ag-ui").MapGet("/group/{groupId}/topic-summary",
            (string groupId, HttpContext ctx, IGroupStore groupStore, TopicSummaryStore store) =>
        {
            var userId = WebIdentity.UserId(ctx);
            if (userId is null)
                return Results.Json(new { error = "未登录" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!groupStore.GroupsOf(userId).Any(g => string.Equals(g.GroupId, groupId, StringComparison.Ordinal)))
                return Results.Json(new { error = "仅群成员可查看该话题小结" }, statusCode: StatusCodes.Status403Forbidden);

            var topicId = (ctx.Request.Query["topicId"].ToString() ?? "main").Trim();
            if (string.IsNullOrEmpty(topicId)) topicId = "main";
            var rec = store.Get(groupId, topicId);
            return Results.Ok(new
            {
                found = rec is not null,
                summary = rec?.Summary ?? "",
                updatedAtMs = rec?.UpdatedAtMs,
                messageCount = rec?.MessageCount,
                groupId,
                topicId,
            });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }
}
