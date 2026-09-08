using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.AspNetCore.Http;

namespace AguiGroupChat.Web;

/// <summary>
/// 消息反馈（👍/👎 → 偏好画像）写入接口：
///   POST /ag-ui/message-feedback —— body { groupId, messageId, value(1|-1), tags?[] }。
/// 鉴权：登录且为该群成员；仅允许对数字员工回复反馈；同一用户对同一消息重复提交覆盖。
/// 反馈记录跨重启保持（扩展区 msgFeedback），网关注入侧按“该用户近期负面反馈（同群/同数字员工）”
/// 自动附加改进提示（见 AgentGateway.BuildFeedbackHint）。
/// </summary>
public static class MessageFeedbackApi
{
    public static void MapMessageFeedbackApi(this WebApplication app)
    {
        app.MapPost("/ag-ui/message-feedback", (MessageFeedbackHttpRequest req, HttpContext ctx,
            IGroupStore groupStore, MessageFeedbackStore store, AguiGroupChat.Hub.Messaging.GroupHub hub,
            AguiGroupChat.Hub.Agents.IMessageMemory? memory) =>
        {
            var userId = WebIdentity.UserId(ctx);
            if (userId is null)
                return Results.Json(new { error = "未登录" }, statusCode: StatusCodes.Status401Unauthorized);
            var groupId = (req.GroupId ?? "").Trim();
            var messageId = (req.MessageId ?? "").Trim();
            if (groupId.Length == 0 || messageId.Length == 0 || (req.Value != 1 && req.Value != -1))
                return Results.BadRequest(new { error = "参数不合法：需 groupId / messageId / value(1|-1)" });
            if (!groupStore.GroupsOf(userId).Any(g => string.Equals(g.GroupId, groupId, StringComparison.Ordinal)))
                return Results.Json(new { error = "仅群成员可评价该消息" }, statusCode: StatusCodes.Status403Forbidden);

            var msg = hub.Store.GetMessage(groupId, messageId);
            if (msg is null || msg.SenderType != MemberType.Agent)
                return Results.BadRequest(new { error = "仅支持对数字员工的回复进行评价" });

            var tags = (req.Tags ?? [])
                .Select(t => (t ?? "").Trim()).Where(t => t.Length > 0 && t.Length <= 12).Distinct().Take(6).ToArray();
            var snippet = (msg.Content ?? "").Trim();
            if (snippet.Length > 120) snippet = snippet[..120] + "…";

            store.Put(new MessageFeedbackEntry
            {
                MessageId = messageId,
                GroupId = groupId,
                TopicId = msg.TopicId ?? "main",
                AgentId = msg.SenderId,
                UserId = userId,
                Value = req.Value,
                Tags = tags,
                Snippet = snippet,
                CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            // 👍 反馈 = 该条回复对用户有用：把其语义记忆单调升到「重要」（已是关键级不降级），让同类回复在 RAG 里权重更高
            if (req.Value > 0 && memory is not null)
            {
                try { memory.PromoteImportance(messageId, MemoryImportance.Important); }
                catch { /* 记忆未启用 / 升级失败静默（评价本身已成功） */ }
            }
            return Results.Ok(new { saved = true, value = req.Value });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }
}

public sealed record MessageFeedbackHttpRequest(string? GroupId, string? MessageId, int Value, string[]? Tags);
