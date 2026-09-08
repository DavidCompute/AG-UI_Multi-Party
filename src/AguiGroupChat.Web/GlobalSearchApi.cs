using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.DependencyInjection;

namespace AguiGroupChat.Web;

/// <summary>
/// 全局智能检索（跨知聚统一搜索）：一次搜遍「我所在各知聚」的消息、语义记忆与可读知识库。
///   GET /ag-ui/search?q=&amp;limit=40
/// 权限：仅返回当前用户<b>可见</b>内容——
///   - 消息：其所在知聚（含客服成员与顾客参与者）中，普通知聚成员可见全公开消息 / @提及 / 定向给 ta / 本人发言；
///     客服知聚成员（staff）可见全部；顾客参与者仅见本人会话相关消息（与聊天可见性一致，不泄露他人顾客会话）；
///   - 记忆：仅其所在（成员身份）知聚的群记忆（与记忆治理页可见范围一致，未启用记忆时无此项）；
///   - 知识库：仅其可读（系统级 / 本人创建 / 管理员 / 其所在群共享）知识库，含文档切片命中。
/// </summary>
public static class GlobalSearchApi
{
    /// <summary>搜索关键词最短长度（过短命中噪音大）。</summary>
    private const int MinKeyword = 2;
    private const int SnippetChars = 300;

    public static void MapGlobalSearchApi(this WebApplication app)
    {
        app.MapGet("/ag-ui/search", (HttpContext ctx, GroupHub hub, AuthService auth,
            KnowledgeBaseCatalog kbs, string? q, int limit = 40) =>
        {
            var userId = WebIdentity.UserId(ctx)!;
            var keyword = (q ?? "").Trim();
            if (keyword.Length < MinKeyword)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"关键词至少 {MinKeyword} 个字符"));
            var cap = Math.Clamp(limit, 1, 100);
            var memory = ctx.RequestServices.GetService<IMessageMemory>(); // 语义记忆未启用时为 null

            // 所在知聚 + 访问角色（支持客服知聚顾客参与者以非成员访问）
            var memberGroups = new HashSet<string>(StringComparer.Ordinal);
            var groupAccess = new List<(Group Group, GroupMember? Member)>();
            foreach (var g in hub.Store.AllGroups())
            {
                var member = hub.Store.GetMember(g.GroupId, userId);
                if (member is not null)
                {
                    memberGroups.Add(g.GroupId);
                    groupAccess.Add((g, member));
                }
                else if (g.IsSupportCircle && hub.IsSupportCustomer(g.GroupId, userId))
                {
                    groupAccess.Add((g, null)); // 顾客参与者（非成员）
                }
            }

            // ---- 消息 ----
            var messages = new List<MsgHit>();
            foreach (var (g, member) in groupAccess)
            {
                var staffAll = g.IsSupportCircle && member is { Role: GroupRole.Owner or GroupRole.Admin };
                foreach (var m in hub.Store.SearchMessages(g.GroupId, keyword, null, 40))
                {
                    if (m.Recalled || !CanSeeMessage(userId, m, member is not null, staffAll)) continue;
                    messages.Add(new MsgHit(
                        m.MessageId, g.GroupId, g.GroupName, m.TopicId, m.Timestamp,
                        m.SenderId, m.SenderNickname, m.SenderType.ToString(), Snippet(m.Content)));
                }
            }

            // ---- 记忆（仅成员知聚，与记忆治理可见范围一致；未启用记忆时不返回该组） ----
            var memories = new List<MemoryHit>();
            if (memory is not null)
            {
                foreach (var gid in memberGroups)
                {
                    foreach (var item in memory.ListMessages(gid, null, keyword, 20, 0))
                    {
                        memories.Add(new MemoryHit(
                            item.MessageId, item.GroupId,
                            hub.Store.GetGroup(item.GroupId)?.GroupName ?? item.GroupId,
                            item.TopicId, item.Timestamp, item.Importance, Snippet(item.Content)));
                    }
                }
            }

            // ---- 知识库（可读范围：系统级 / 本人创建 / 管理员 / 所在群共享），含文档切片命中 ----
            var isAdmin = auth.IsAdmin(userId);
            var kbHits = new List<KbHit>();
            foreach (var kb in kbs.ListAll().Where(kb => kbs.CanRead(kb, userId, memberGroups, isAdmin)))
            {
                var nameHit = kb.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                              || kb.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                if (nameHit)
                {
                    kbHits.Add(new KbHit(kb.KbId, kb.Name, "kb",
                        Snippet(kb.Description.Length > 0 ? kb.Description : kb.Name)));
                }
                if (memory is null) continue;
                foreach (var item in memory.ListMessages(KnowledgeBaseCatalog.KbGroupPrefix + kb.KbId, null, keyword, 20, 0))
                {
                    kbHits.Add(new KbHit(kb.KbId, kb.Name, "chunk", Snippet(item.Content)));
                }
            }

            return Results.Ok(new
            {
                q = keyword,
                messages = messages.OrderByDescending(m => m.Timestamp).Take(Math.Max(1, cap * 3 / 2)),
                memories = memories.OrderByDescending(m => m.Timestamp).Take(Math.Max(1, cap * 3 / 5)),
                knowledgeBases = kbHits.Take(Math.Max(1, cap / 2)),
            });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }

    /// <summary>消息对该用户是否可见（与聊天扇出一致：本人 / 群全员（成员）/ @提及 / 定向可见；客服成员看全部）。</summary>
    private static bool CanSeeMessage(string userId, GroupMessage m, bool isMember, bool staffAll)
        => staffAll
           || m.SenderId == userId
           || (isMember && m.Visibility == MessageVisibility.All)
           || (m.Mentions?.Contains(userId) ?? false)
           || (m.VisibleMemberIds?.Contains(userId) ?? false);

    /// <summary>正文摘要（截断防超大响应，压缩换行；UI 自行高亮）。</summary>
    private static string Snippet(string text)
    {
        var t = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= SnippetChars ? t : t[..SnippetChars] + "…";
    }
}

/// <summary>全局检索 - 消息命中（返回给前端：messageId 用于跳转 / 定位）。</summary>
internal sealed record MsgHit(
    string MessageId, string GroupId, string GroupName, string TopicId, long Timestamp,
    string SenderId, string SenderNickname, string SenderType, string Snippet);

/// <summary>全局检索 - 语义记忆命中。</summary>
internal sealed record MemoryHit(
    string MessageId, string GroupId, string GroupName, string TopicId, long Timestamp,
    int Importance, string Snippet);

/// <summary>全局检索 - 知识库命中（name 名称命中 / chunk 文档切片命中）。</summary>
internal sealed record KbHit(string KbId, string Name, string Kind, string Snippet);
