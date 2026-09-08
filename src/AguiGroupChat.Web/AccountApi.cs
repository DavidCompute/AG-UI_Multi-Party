using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AguiGroupChat.Web;

/// <summary>
/// 账号（数据主体权利 · 企业合规）HTTP API：
///   DELETE /ag-ui/account —— 注销当前登录账号：要求提交登录密码确认（防会话劫持 / 误删），
///   注销执行完整数据擦除（创建的知聚转让 / 解散、加入的知聚退群、发言记忆与个人知识库清除、账号行删除），
///   随后该账号全部会话即时失效。最后一名超级管理员不可注销（防平台失去最高管理入口）。
///   GET /ag-ui/account/export —— 「我的数据」导出（数据可携权）：本人可随时把账号资料、知聚清单、
///   本人发言（含附件元信息）、本人语义记忆、个人知识库元数据、本人创建的数字员工与技能定义导出为一个
///   JSON 文件下载；与「注销账户」并排放在资料弹窗危险区，供注销前留存。
/// </summary>
public static class AccountApi
{
    public static void MapAccountApi(this WebApplication app)
    {
        app.MapDelete("/ag-ui/account", async ([FromBody] AccountDeleteHttpRequest req, HttpContext ctx,
            [FromServices] AccountErasureService erasure,
            [FromServices] AuthService auth, CancellationToken ct) =>
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
                    messagesAnonymized = report.MessagesAnonymized,
                    agentsRemoved = report.AgentsRemoved,
                    skillsRemoved = report.SkillsRemoved,
                });
            }
            catch (AguiProtocolException ex) { return MapError(ex); }
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 「我的数据」导出（数据可携权）：仅本人可见本人数据 ----
        app.MapGet("/ag-ui/account/export", (HttpContext ctx, AuthService auth,
            IGroupStore store, KnowledgeBaseCatalog kbs,
            AgentCatalog agents, AgentSkillCatalog skills,
            AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var userId = WebIdentity.UserId(ctx)!;
            var user = auth.GetUser(userId)
                ?? throw new AguiProtocolException(ErrorCodes.UserNotFound, "用户不存在");
            // 语义记忆服务未启用时 DI 占位返回 null：运行期解析，避免端点因可选服务缺失而 500
            var memory = ctx.RequestServices.GetService<IMessageMemory>();

            // 账号资料
            var profile = new
            {
                user.UserId,
                user.Username,
                user.Nickname,
                user.Avatar,
                user.CreatedAt,
                personalMemoryEnabled = user.PersonalMemoryEnabled,
                platformRole = PlatformRoleUtil.Name(auth.ResolveRole(userId)),
            };

            // 知聚清单（本人所在：成员 / 群主；角色标注便于迁移）
            var groups = store.GroupsOf(userId)
                .Select(g => new
                {
                    g.GroupId,
                    g.GroupName,
                    g.IsPrivate,
                    kind = g.Kind.ToString(),
                    isSupportCircle = g.IsSupportCircle,
                    isDirectChat = g.IsDirectChat,
                    g.OwnerId,
                    myRole = store.GetMember(g.GroupId, userId)?.Role.ToString(),
                    g.MemberCount,
                    g.CreateTime,
                    lastMessageAt = store.LastMessageAt(g.GroupId),
                })
                .OrderByDescending(g => g.CreateTime)
                .ToList();

            // 本人发言（跨全部所在知聚，含附件元信息；已撤回不导出正文）。每群最多回溯 300 条本人发言，总量上限 3000。
            var messages = new List<object>();
            foreach (var g in groups)
            {
                var own = store.AllMessages(g.GroupId)
                    .Where(m => m.SenderId == userId)
                    .OrderByDescending(m => m.Timestamp)
                    .Take(300)
                    .ToList();
                foreach (var m in own)
                {
                    messages.Add(new
                    {
                        groupId = g.GroupId,
                        groupName = g.GroupName,
                        m.MessageId,
                        m.TopicId,
                        m.Timestamp,
                        recalled = m.Recalled,
                        content = m.Recalled ? null : m.Content,
                        attachments = m.Recalled ? null : m.Attachments.Select(a => new { a.AttachmentId, a.Name, a.ContentType, a.Url }).ToList(),
                    });
                    if (messages.Count >= 3000) break;
                }
                if (messages.Count >= 3000) break;
            }

            // 本人发言的语义记忆（记忆启用时）
            var memoryItems = memory is null ? [] : PageOwnMemories(memory, userId);

            // 个人知识库（元数据 + 文档清单）
            var ownedKbs = kbs.ListAll()
                .Where(k => k.OwnerId == userId)
                .Select(k => new
                {
                    k.KbId,
                    k.Name,
                    k.Description,
                    sharedGroupIds = k.SharedGroupIds,
                    updatedAtMs = k.UpdatedAtMs,
                    docs = k.Documents.Select(d => new { d.DocId, d.FileName, d.Status, d.Error, d.ChunkCount, d.AddedAtMs }).ToList(),
                })
                .ToList();

            // 本人创建的数字员工 / 技能定义（完整定义，可迁移到新平台）
            var ownedAgents = agents.ListDefinitions()
                .Where(d => string.Equals(d.OwnerId, userId, StringComparison.Ordinal))
                .Select(d => (object)d)
                .ToList();
            var ownedSkills = skills.ListAll()
                .Where(s => string.Equals(s.OwnerId, userId, StringComparison.Ordinal))
                .Select(s => (object)s)
                .ToList();

            var doc = new
            {
                schema = "agui-group-chat/my-data/v1",
                exportedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                account = profile,
                groups,
                messages,
                memories = memoryItems,
                knowledgeBases = ownedKbs,
                agents = ownedAgents,
                skills = ownedSkills,
            };

            audit.Record("data.export", userId, user.Username, targetType: "user", targetId: userId,
                detail: $"导出本人数据（知聚 {groups.Count} / 消息 {messages.Count} / 记忆 {memoryItems.Count} / 知识库 {ownedKbs.Count} / 数字员工 {ownedAgents.Count}）");

            var bytes = JsonSerializer.SerializeToUtf8Bytes(doc, DataJson);
            return Results.File(bytes, "application/json; charset=utf-8",
                $"agui-my-data-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }

    /// <summary>分页读取某人全部语义记忆（上限 2000 条；跨全部群）。</summary>
    private static IReadOnlyList<object> PageOwnMemories(IMessageMemory memory, string userId)
    {
        var all = new List<object>();
        var offset = 0;
        while (offset < 2000)
        {
            var page = memory.ListMessages(null, userId, null, 500, offset);
            if (page.Count == 0) break;
            foreach (var item in page)
            {
                all.Add(new
                {
                    item.GroupId,
                    item.TopicId,
                    item.MessageId,
                    item.Timestamp,
                    item.Importance,
                    item.Content,
                });
                if (all.Count >= 2000) return all;
            }
            offset += page.Count;
        }
        return all;
    }

    private static readonly JsonSerializerOptions DataJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
    };

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
