using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.AspNetCore.Http;

namespace AguiGroupChat.Web;

/// <summary>
/// 输入时「建议 @ 谁」接口：
///   POST /ag-ui/mention-suggest —— body { groupId, text }。
/// 在用户尚未 @ 任何人时，按输入草稿对群内数字员工做本地规则评分（触发词 / 昵称 / 职责描述词项重叠），
/// 返回建议 @ 的数字员工（最多 3 名，供前端在输入框上方展示快捷 @ chips）。
/// 鉴权：登录且为该群成员。单聊（群内仅用户与一个数字员工）不提示——对象已明确。
/// 零模型调用：纯规则（MentionSuggester），即点即回，无额外成本。
/// </summary>
public static class MentionSuggestApi
{
    public static void MapMentionSuggestApi(this WebApplication app)
    {
        app.MapPost("/ag-ui/mention-suggest", (MentionSuggestHttpRequest req, HttpContext ctx,
            IGroupStore groupStore, AgentCatalog catalog) =>
        {
            var userId = WebIdentity.UserId(ctx);
            if (userId is null)
                return Results.Json(new { error = "未登录" }, statusCode: StatusCodes.Status401Unauthorized);
            var groupId = (req.GroupId ?? "").Trim();
            var text = (req.Text ?? "").Trim();
            if (groupId.Length == 0)
                return Results.BadRequest(new { error = "参数不合法：需 groupId / text" });
            if (!groupStore.GroupsOf(userId).Any(g => string.Equals(g.GroupId, groupId, StringComparison.Ordinal)))
                return Results.Json(new { error = "仅群成员可获得 @ 建议" }, statusCode: StatusCodes.Status403Forbidden);

            // 文本过短：不提示（避免边打字边打扰）
            if (text.Length < MentionSuggester.MinTextChars)
                return Results.Ok(new { suggestions = Array.Empty<object>(), reason = "text-short" });

            var members = groupStore.ListMembers(groupId);
            var agentMembers = members.Where(m => m.MemberType == MemberType.Agent).ToList();
            var otherHumans = members.Any(m => m.MemberType != MemberType.Agent && m.MemberId != userId);
            // 与唯一数字员工的 1:1 单聊：对象明确，不提示
            if (agentMembers.Count == 1 && !otherHumans)
                return Results.Ok(new { suggestions = Array.Empty<object>(), reason = "direct" });

            var profiles = new List<MentionCandidateProfile>();
            foreach (var agentMember in agentMembers)
            {
                var def = catalog.GetDefinition(agentMember.MemberId);
                if (def is null || def.IsSkillTarget) continue;
                profiles.Add(new MentionCandidateProfile(def.AgentId, def.Nickname, def.Description, def.Keywords));
            }

            var suggestions = MentionSuggester.Suggest(text, profiles)
                .Select(s => new { agentId = s.AgentId, nickname = s.Nickname, reason = s.Reason })
                .ToList();
            return Results.Ok(new { suggestions });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }
}

public sealed record MentionSuggestHttpRequest(string? GroupId, string? Text);
