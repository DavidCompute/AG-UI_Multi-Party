using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 数据导出 / 导入 HTTP API：
///   GET  /ag-ui/export         —— 导出账号 + 智能体 + 聊天记录（含附件文件）为 zip
///   POST /ag-ui/import/preview —— 上传 zip，返回账号 / 智能体存在性检查与群清单（供前端勾选）
///   POST /ag-ui/import         —— 上传 zip + selectedGroupIds，执行导入并返回结果报告
/// 导入完整性：账号按 username 自动检查（缺失则创建并保留密码哈希），智能体按 agentId 自动检查
/// （缺失则创建），消息发送者 / 提及 / 可见列表按账号映射重写，附件文件随 zip 还原。
/// </summary>
public static class ExportImportApi
{
    private const string ManifestName = "manifest.json";

    /// <summary>导出 / 导入请求体上限：附件可能较大，放宽到 200MB（与 Program.cs 的 MultipartBodyLengthLimit 一致）。</summary>
    private const long MaxBodyBytes = 200L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void MapExportImportApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui");

        // ---- 导出：全量数据 zip（账号 + 智能体定义/触发规则 + 群/话题/消息 + 附件文件）----
        root.MapGet("/export", (HttpContext ctx, AuthService auth, IGroupStore store,
            IUserStore userStore, AgentCatalog catalog, AgentRegistry registry, AttachmentStore attachments,
            AgentSkillCatalog skillCatalog, OrgTeamStore orgTeams,
            AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var actorId = WebIdentity.UserId(ctx)!;

            var manifest = BuildManifest(store, userStore, catalog, registry, skillCatalog, orgTeams);
            var bytes = BuildExportZip(manifest, store, userStore, catalog, attachments);
            var name = $"agui-data-export-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            audit.Record("data.export", actorId, auth.GetUser(actorId)?.Username,
                detail: $"导出全部数据（账号 {manifest.Accounts.Count} / 智能体 {manifest.Agents.Count} / 群 {manifest.Groups.Count}）");
            return Results.File(bytes, "application/zip", fileDownloadName: name);
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 导入预览：解析 zip 的 manifest，返回账号 / 智能体存在性检查与群清单 ----
        root.MapPost("/import/preview", async (HttpContext ctx,
            AgentCatalog catalog, IUserStore userStore, AgentSkillCatalog skillCatalog) =>
        {
            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "请上传导出的 zip 文件（表单字段 file）"));

            using var zip = OpenZip(ctx.Request.Form.Files[0]);
            if (zip is null) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "无法解析上传的 zip 文件"));
            var manifest = await ReadManifest(zip);
            if (manifest is null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "zip 中缺少 manifest.json（不是本系统导出的数据包）"));

            return Results.Ok(new
            {
                exportedAt = manifest.ExportedAt,
                accounts = manifest.Accounts.Select(a => new
                {
                    username = a.Username,
                    nickname = a.Nickname,
                    exists = userStore.GetUserByUsername(a.Username) is not null,
                }),
                agents = manifest.Agents.Select(a => new
                {
                    agentId = a.Definition.AgentId,
                    nickname = a.Definition.Nickname,
                    exists = catalog.GetDefinition(a.Definition.AgentId) is not null,
                }),
                groups = manifest.Groups.Select(g => new
                {
                    groupId = g.Group.GroupId,
                    groupName = g.Group.GroupName,
                    memberCount = g.Members.Count,
                    messageCount = g.Messages.Count,
                    avatar = g.Group.GroupAvatar,
                }),
                skills = manifest.Skills.Select(s => new
                {
                    skillId = s.SkillId,
                    kind = s.Kind.ToString(),
                    exists = skillCatalog.Contains(s.SkillId),
                }),
                orgTeams = manifest.OrgTeams.Select(t => new
                {
                    key = t.Key,
                    title = t.Title,
                    agents = t.Agents,
                    skillsList = t.Skills,
                }),
            });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 导入执行：selectedGroupIds 为 JSON 数组字符串（勾选的群，按导出 groupId）----
        root.MapPost("/import", async (HttpContext ctx, AuthService auth,
            IGroupStore store, IUserStore userStore, AgentCatalog catalog, AgentRegistry registry,
            GroupHub hub, AttachmentStore attachments, AgentSkillCatalog skillCatalog, OrgTeamStore orgTeams,
            AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var actorId = WebIdentity.UserId(ctx)!;
            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "请上传导出的 zip 文件（表单字段 file）"));

            var rawGroups = ctx.Request.Form["selectedGroupIds"].ToString();
            var selected = string.IsNullOrWhiteSpace(rawGroups)
                ? new HashSet<string>()
                : new HashSet<string>(JsonSerializer.Deserialize<List<string>>(rawGroups, Json) ?? [], StringComparer.Ordinal);

            using var zip = OpenZip(ctx.Request.Form.Files[0]);
            if (zip is null) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "无法解析上传的 zip 文件"));
            var manifest = await ReadManifest(zip);
            if (manifest is null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "zip 中缺少 manifest.json（不是本系统导出的数据包）"));

            try
            {
                var report = await ImportAsync(manifest, zip, selected, store, userStore, catalog, registry, hub, attachments, skillCatalog, orgTeams);
                audit.Record("data.import", actorId, auth.GetUser(actorId)?.Username,
                    detail: $"导入数据包（勾选群 {selected.Count} 个 / 账号 {manifest.Accounts.Count} / 智能体 {manifest.Agents.Count}）");
                return Results.Ok(report);
            }
            catch (AguiProtocolException ex)
            {
                // 导入中途校验失败（如消息超长 / 解压总量超限）：以 400 返回，不把异常冒泡成 500
                return Results.BadRequest(new AguiError(ex.ErrorCode, ex.Message));
            }
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());
    }

    // ================= 导出 =================

    private static ExportManifest BuildManifest(IGroupStore store, IUserStore userStore, AgentCatalog catalog, AgentRegistry registry, AgentSkillCatalog skillCatalog, OrgTeamStore orgTeams)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var manifest = new ExportManifest { ExportedAt = now };

        // 技能库实体：库内全部技能（含会随数字员工 SkillDefIds 引用、需在目标侧还原的 org_design / org_deploy 等）
        manifest.Skills = skillCatalog.ListAll().ToList();
        // 组织覆盖簿记：key → 该批 agent/skill，跨机还原后“同一 key 覆盖只留最新”仍成立
        manifest.OrgTeams = orgTeams.SnapshotAll().ToList();

        // 账号：全部（含密码哈希 / 盐，保证导入后原密码可直接登录）
        manifest.Accounts = userStore.ListUsers()
            .Select(u => new ExportAccount
            {
                UserId = u.UserId,
                Username = u.Username,
                PasswordHash = u.PasswordHash,
                PasswordSalt = u.PasswordSalt,
                Nickname = u.Nickname,
                Avatar = u.Avatar,
                PersonalMemoryEnabled = u.PersonalMemoryEnabled,
                CreatedAt = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
            })
            .ToList();

        // 智能体：排除 AI 分身（twin_*）与技能目标（IsSkillTarget，系统自生成子代理）
        var defs = catalog.ListDefinitions()
            .Where(d => !d.IsSkillTarget && !d.AgentId.StartsWith(TwinService.AgentIdPrefix, StringComparison.Ordinal))
            .ToList();
        var allRegs = registry.AllRegistrations();
        manifest.Agents = defs.Select(def => new ExportAgent
        {
            Definition = def,
            Registrations = allRegs.Where(r => r.AgentId == def.AgentId).ToList(),
        }).ToList();

        // 群：群 / 成员 / 话题 / 消息（含撤回标记与附件元信息）
        manifest.Groups = store.AllGroups().Select(g =>
        {
            var messages = store.AllMessages(g.GroupId)
                .Select(m => new PersistedMessage
                {
                    MessageId = m.MessageId,
                    GroupId = m.GroupId,
                    ThreadId = m.ThreadId,
                    TopicId = m.TopicId,
                    SenderId = m.SenderId,
                    SenderType = m.SenderType,
                    SenderNickname = m.SenderNickname,
                    ReplyToMessageId = m.ReplyToMessageId,
                    Mentions = m.Mentions.ToList(),
                    MentionAll = m.MentionAll,
                    Visibility = m.Visibility,
                    VisibleMemberIds = m.VisibleMemberIds.ToList(),
                    Attachments = m.Attachments.ToList(),
                    Content = m.Content,
                    Reasoning = m.Reasoning,
                    Timestamp = m.Timestamp,
                    Recalled = m.Recalled,
                })
                .ToList();
            return new ExportGroup
            {
                Group = g,
                Members = store.ListMembers(g.GroupId).ToList(),
                Topics = store.ListTopics(g.GroupId).ToList(),
                Messages = messages,
            };
        }).ToList();

        return manifest;
    }

    private static byte[] BuildExportZip(ExportManifest manifest, IGroupStore store, IUserStore userStore,
        AgentCatalog catalog, AttachmentStore attachments)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(ManifestName, CompressionLevel.Fastest);
            using (var s = entry.Open())
            {
                var json = JsonSerializer.Serialize(manifest, Json);
                s.Write(Encoding.UTF8.GetBytes(json));
            }

            // 附件文件：全部消息附件 + 账号 / 智能体 / 群头像引用的站内附件（/ag-ui/files/att_xxx/...）
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in manifest.Groups)
                foreach (var m in g.Messages)
                    foreach (var a in m.Attachments) wanted.Add(a.AttachmentId);
            foreach (var u in manifest.Accounts) TryAddAvatarRef(u.Avatar, wanted);
            foreach (var ag in manifest.Agents) TryAddAvatarRef(ag.Definition.Avatar, wanted);
            foreach (var g in manifest.Groups) TryAddAvatarRef(g.Group.GroupAvatar, wanted);

            var files = attachments.ListAllFiles();
            foreach (var (id, path) in files)
            {
                if (!wanted.Contains(id)) continue;
                var name = Path.GetFileName(path);
                var fileEntry = zip.CreateEntry($"files/{id}/{name}", CompressionLevel.Fastest);
                using var src = File.OpenRead(path);
                using var dst = fileEntry.Open();
                src.CopyTo(dst);
            }
        }
        return ms.ToArray();
    }

    /// <summary>从头像 URL 提取站内附件 ID（/ag-ui/files/att_xxx/... → att_xxx）。</summary>
    private static void TryAddAvatarRef(string? url, HashSet<string> wanted)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var m = System.Text.RegularExpressions.Regex.Match(url, @"/ag-ui/files/(att_[A-Za-z0-9_-]+)/");
        if (m.Success) wanted.Add(m.Groups[1].Value);
    }

    // ================= 导入 =================

    private sealed record ImportResult(
        int AccountsCreated, int AccountsUpdated,
        int AgentsCreated, int AgentsSkipped,
        int AttachmentsRestored, int AttachmentsSkipped,
        List<ImportedGroup> Groups);

    private sealed record ImportedGroup(string NewGroupId, string GroupName, int MemberCount, int MessageCount);

    private static async Task<object> ImportAsync(ExportManifest manifest, ZipArchive zip, HashSet<string> selectedGroupIds,
        IGroupStore store, IUserStore userStore, AgentCatalog catalog, AgentRegistry registry,
        GroupHub hub, AttachmentStore attachments, AgentSkillCatalog skillCatalog, OrgTeamStore orgTeams)
    {
        var accountMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var accountsCreated = 0;
        var accountsUpdated = 0;

        // ---- 1. 账号：按 username 检查；缺失则创建（保留密码哈希 / 盐，原密码可直接登录）；
        //         已存在则用导入数据更新资料（昵称 / 头像 / 个人记忆开关；密码保留现有，不覆盖）----
        foreach (var ea in manifest.Accounts)
        {
            var existing = userStore.GetUserByUsername(ea.Username);
            if (existing is not null)
            {
                existing.Nickname = ea.Nickname;
                existing.Avatar = ea.Avatar;
                existing.PersonalMemoryEnabled = ea.PersonalMemoryEnabled;
                existing.UpdatedAt = ea.UpdatedAt;
                userStore.UpdateUser(existing);
                accountMap[ea.UserId] = existing.UserId;
                accountsUpdated++;
                continue;
            }
            var uid = ea.UserId;
            var added = false;
            for (var attempt = 0; attempt < 3 && !added; attempt++)
            {
                added = userStore.AddUser(new UserAccount
                {
                    UserId = uid,
                    Username = ea.Username,
                    PasswordHash = ea.PasswordHash,
                    PasswordSalt = ea.PasswordSalt,
                    Nickname = ea.Nickname,
                    Avatar = ea.Avatar,
                    PersonalMemoryEnabled = ea.PersonalMemoryEnabled,
                    CreatedAt = ea.CreatedAt,
                    UpdatedAt = ea.UpdatedAt,
                });
                if (!added) uid = "user_" + IdGenerator.NewId(); // userId 冲突 → 换新 id（消息发送者走映射）
            }
            accountMap[ea.UserId] = uid;
            accountsCreated++;
        }
        string MapAccount(string id) => accountMap.TryGetValue(id, out var mapped) ? mapped : id;

        // ---- 1.5 技能库实体（含 org_design / org_deploy 等，供下方数字员工 SkillDefIds 引用还原）：缺失则补齐 ----
        var skillsCreated = 0;
        foreach (var s in manifest.Skills)
        {
            if (string.IsNullOrWhiteSpace(s.SkillId)) continue;
            if (!skillCatalog.Contains(s.SkillId))
            {
                skillCatalog.Upsert(s); // 与账号一致：已存在的技能不覆盖（目标侧沿用其库内定义）
                skillsCreated++;
            }
        }

        // ---- 2. 智能体定义：按 agentId 检查；缺失则创建（OwnerId 走账号映射）----
        var agentsCreated = 0;
        var agentsSkipped = 0;
        foreach (var ea in manifest.Agents)
        {
            var def = ea.Definition;
            if (catalog.GetDefinition(def.AgentId) is not null)
            {
                agentsSkipped++;
                continue;
            }
            def.OwnerId = string.IsNullOrEmpty(def.OwnerId) ? null : MapAccount(def.OwnerId);
            catalog.Upsert(def);
            agentsCreated++;
        }

        // ---- 2.5 组织覆盖簿记（导出 key=该批 agent/skill）：缺失则还原 ----
        var orgTeamsRestored = 0;
        foreach (var rec in manifest.OrgTeams)
        {
            if (string.IsNullOrWhiteSpace(rec.Key)) continue;
            if (orgTeams.Get(rec.Key) is not null) continue; // 不覆盖目标侧已存在簿记
            orgTeams.Upsert(rec.Key, rec.Title, new List<string>(rec.Agents), new List<string>(rec.Skills), rec.SupportCircleGroupId);
            orgTeamsRestored++;
        }

        // ---- 3. 附件文件还原（仅选中的群涉及的附件，避免还原无关文件）----
        var wantedAttachments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in manifest.Groups)
        {
            if (selectedGroupIds.Count > 0 && !selectedGroupIds.Contains(g.Group.GroupId)) continue;
            foreach (var m in g.Messages)
                foreach (var a in m.Attachments) wantedAttachments.Add(a.AttachmentId);
            // 头像也是站内附件（/ag-ui/files/att_xxx/...）：群头像 / 群成员头像一并还原
            TryAddAvatarRef(g.Group.GroupAvatar, wantedAttachments);
            foreach (var mem in g.Members) TryAddAvatarRef(mem.Avatar, wantedAttachments);
        }
        // 账号 / 智能体头像（登录后用户菜单与智能体目录引用）
        foreach (var ea in manifest.Accounts) TryAddAvatarRef(ea.Avatar, wantedAttachments);
        foreach (var ea in manifest.Agents) TryAddAvatarRef(ea.Definition.Avatar, wantedAttachments);
        var attachmentsRestored = 0;
        var attachmentsSkipped = 0;
        var decompressedTotal = 0L; // 实际解压累计字节（zip 炸弹第二道防线：总体积上限）
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("files/", StringComparison.Ordinal) || entry.FullName.Length <= 6) continue;
            var rel = entry.FullName["files/".Length..];
            var slash = rel.IndexOf('/');
            if (slash <= 0) continue;
            var id = rel[..slash];
            if (!wantedAttachments.Contains(id)) continue;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            // 边复制边计数：超过单附件上限（20MB，RestoreFile 的校验值）立即中断，不再整段缓冲进内存，
            // 防 zip 炸弹的恶意条目在 20MB 校验之前撑爆内存（单条目元数据上限 64MB 在 OpenZip 已拦第一道）
            var size = await CopyWithLimitAsync(stream, buffer, AttachmentStore.MaxFileBytes);
            if (size < 0)
            {
                attachmentsSkipped++;
                continue;
            }
            decompressedTotal += size;
            if (decompressedTotal > MaxZipTotalBytes)
                throw new AguiProtocolException(ErrorCodes.BadRequest,
                    $"zip 解压总大小超过上限（{MaxZipTotalBytes / 1024 / 1024}MB），导入中止");
            var ok = attachments.RestoreFile(id, rel[(slash + 1)..], buffer.ToArray());
            if (ok) attachmentsRestored++; else attachmentsSkipped++;
        }

        // ---- 4. 群导入（仅勾选的群；群 id 重新生成避免冲突）----
        var oldGroupMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var imported = new List<ImportedGroup>();
        foreach (var eg in manifest.Groups)
        {
            if (selectedGroupIds.Count > 0 && !selectedGroupIds.Contains(eg.Group.GroupId)) continue;

            var newGroupId = "group_" + IdGenerator.NewId();
            oldGroupMap[eg.Group.GroupId] = newGroupId;

            // 4.1 群
            var group = new Group
            {
                GroupId = newGroupId,
                GroupName = eg.Group.GroupName,
                GroupAvatar = eg.Group.GroupAvatar,
                IsPrivate = eg.Group.IsPrivate,
                OwnerId = MapAccount(eg.Group.OwnerId),
                MemberCount = 0,
                CreateTime = eg.Group.CreateTime,
                Extra = eg.Group.Extra,
            };
            store.AddGroup(group);

            // 4.2 成员（保留角色 / 触发信息；在线状态重置为离线）
            var memberMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in eg.Members)
            {
                var memberId = m.MemberType == MemberType.User ? MapAccount(m.MemberId) : m.MemberId;
                memberMap[m.MemberId] = memberId;
                store.AddMember(newGroupId, new GroupMember
                {
                    MemberId = memberId,
                    MemberType = m.MemberType,
                    Nickname = m.Nickname,
                    Avatar = m.Avatar,
                    Role = m.Role,
                    OnlineStatus = OnlineStatus.Offline,
                    JoinTime = m.JoinTime,
                    TriggerMode = m.TriggerMode,
                    Keywords = m.Keywords,
                    IsTriggerOverridden = m.IsTriggerOverridden,
                });
            }
            group.MemberCount = store.MemberCount(newGroupId);
            store.UpdateGroup(group);

            // 4.3 话题
            foreach (var t in eg.Topics)
            {
                store.AddTopic(new GroupTopic
                {
                    TopicId = t.TopicId,
                    GroupId = newGroupId,
                    Name = t.Name,
                    CreatorId = MapAccount(t.CreatorId),
                    CreatedAt = t.CreatedAt,
                });
            }

            // 4.4 消息（直接落库不广播、不触发智能体；发送者 / 提及 / 可见列表按账号映射）
            // 导入校验：与正常发送路径一致的长度 / 数量上限，防恶意 manifest 注入超大内容（存储 DoS）
            if (eg.Messages.Count > MaxImportedMessagesPerGroup)
                throw new AguiProtocolException(ErrorCodes.BadRequest,
                    $"群「{group.GroupName}」消息数 {eg.Messages.Count} 超过导入上限（{MaxImportedMessagesPerGroup}）");
            var msgCount = 0;
            foreach (var pm in eg.Messages)
            {
                if (pm.Content is not null && pm.Content.Length > MaxImportedMessageChars)
                    throw new AguiProtocolException(ErrorCodes.BadRequest,
                        $"群「{group.GroupName}」存在超过长度上限（{MaxImportedMessageChars} 字符）的消息，导入中止");
                if ((pm.Mentions?.Count ?? 0) > 100 || (pm.VisibleMemberIds?.Count ?? 0) > 500 || (pm.Attachments?.Count ?? 0) > 9)
                    throw new AguiProtocolException(ErrorCodes.BadRequest,
                        $"群「{group.GroupName}」消息的提及 / 定向成员 / 附件数量超出上限，导入中止");
                store.AddMessage(new GroupMessage
                {
                    MessageId = pm.MessageId,
                    GroupId = newGroupId,
                    ThreadId = pm.ThreadId,
                    TopicId = pm.TopicId,
                    SenderId = pm.SenderType == MemberType.User ? MapAccount(pm.SenderId) : pm.SenderId,
                    SenderType = pm.SenderType,
                    SenderNickname = pm.SenderNickname,
                    ReplyToMessageId = pm.ReplyToMessageId,
                    Mentions = (pm.Mentions ?? []).Select(x => pm.SenderType == MemberType.User ? MapAccount(x) : x).ToList(),
                    MentionAll = pm.MentionAll,
                    Visibility = pm.Visibility,
                    VisibleMemberIds = (pm.VisibleMemberIds ?? []).Select(x => MapAccount(x)).ToList(),
                    Attachments = (pm.Attachments ?? []).Select(a => new AttachmentInfo
                    {
                        AttachmentId = a.AttachmentId,
                        Name = a.Name,
                        ContentType = a.ContentType,
                        Size = a.Size,
                        Url = $"/ag-ui/files/{a.AttachmentId}/{Uri.EscapeDataString(a.Name)}",
                        Kind = a.Kind,
                    }).ToList(),
                    Content = pm.Content ?? "", // 导入消息无正文时以空串落库（协议允许纯附件消息）
                    Reasoning = pm.Reasoning,
                    Timestamp = pm.Timestamp,
                    Recalled = pm.Recalled,
                });
                msgCount++;
            }

            // 4.5 智能体触发规则：该群导出的智能体成员 → 注册（GroupId 重写为新 id）
            foreach (var ea in manifest.Agents)
            {
                var reg = ea.Registrations.FirstOrDefault(r => r.GroupId == eg.Group.GroupId);
                if (reg is null) continue;
                hub.RegisterAgent(new AgentRegisterRequest
                {
                    AgentId = ea.Definition.AgentId,
                    Nickname = ea.Definition.Nickname,
                    GroupIds = [newGroupId],
                    TriggerMode = reg.TriggerMode,
                    Keywords = reg.Keywords.ToList(),
                    Override = reg.IsOverridden,
                });
            }

            imported.Add(new ImportedGroup(newGroupId, group.GroupName, group.MemberCount, msgCount));
        }

        return new
        {
            accountsCreated,
            accountsUpdated,
            agentsCreated,
            agentsSkipped,
            skillsCreated,
            orgTeamsRestored,
            attachmentsRestored,
            attachmentsSkipped,
            groupsImported = imported,
        };
    }

    // ================= zip 工具 =================

    private const int MaxImportedMessagesPerGroup = 20000;   // 单群导入消息数上限
    private const int MaxImportedMessageChars = 50000;       // 单条导入消息内容长度上限（与 MaxMessageChars 一致）

    // ---- zip 炸弹防御阈值（导入包由不可信来源提供，须限制解压规模）----
    private const int MaxZipEntries = 2000;                  // 单包条目数上限
    private const long MaxZipTotalBytes = 512L * 1024 * 1024; // 解压总体积上限（512MB）
    private const long MaxZipEntryBytes = 64L * 1024 * 1024;  // 单条目解压体积上限（64MB）
    private const long MaxManifestBytes = 4L * 1024 * 1024;   // manifest.json 读取上限（4MB）

    private static ZipArchive? OpenZip(IFormFile file)
    {
        try
        {
            var stream = file.OpenReadStream();
            var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            // 第一道防线：中央目录元数据校验（不实际解压）。条目数 / 声明的单条目大小 / 声明的总大小
            // 任一超限即拒绝（恶意包可在中央目录谎报大小，故真正解压时仍须用带上限的流复制兜底）。
            if (zip.Entries.Count > MaxZipEntries)
            {
                zip.Dispose();
                return null;
            }
            long declaredTotal = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.Length > MaxZipEntryBytes)
                {
                    zip.Dispose();
                    return null;
                }
                declaredTotal += entry.Length;
                if (declaredTotal > MaxZipTotalBytes)
                {
                    zip.Dispose();
                    return null;
                }
            }
            return zip;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<ExportManifest?> ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestName);
        if (entry is null) return null;
        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            // 带上限读取（4MB）：替代无界 ReadToEnd，防恶意 manifest 条目把内存撑爆
            if (await CopyWithLimitAsync(stream, buffer, MaxManifestBytes) < 0) return null;
            return JsonSerializer.Deserialize<ExportManifest>(
                Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>带上限的流复制：边复制边计数，超过上限立即中止并返回 -1，否则返回实际复制的字节数。</summary>
    private static async Task<long> CopyWithLimitAsync(Stream source, Stream destination, long limit, CancellationToken ct = default)
    {
        var buffer = new byte[16 * 1024];
        var total = 0L;
        while (true)
        {
            var n = await source.ReadAsync(buffer, ct);
            if (n == 0) return total;
            total += n;
            if (total > limit) return -1; // 超限：中止（不写入超限部分）
            await destination.WriteAsync(buffer.AsMemory(0, n), ct);
        }
    }

    // ================= 导出数据模型 =================

    private sealed class ExportManifest
    {
        public string App { get; set; } = "agui-group-chat";
        public int FormatVersion { get; set; } = 1;
        public long ExportedAt { get; set; }
        public List<ExportAccount> Accounts { get; set; } = [];
        public List<ExportAgent> Agents { get; set; } = [];
        public List<AgentSkillDefinition> Skills { get; set; } = [];       // 技能库实体（含 org_design / org_deploy 等），保证挂载引用可还原
        public List<OrgTeamRecord> OrgTeams { get; set; } = [];             // 组织覆盖簿记（key→该批 agent/skill），保证“只留最新”语义跨机一致
        public List<ExportGroup> Groups { get; set; } = [];
    }

    private sealed class ExportAccount
    {
        public required string UserId { get; set; }
        public required string Username { get; set; }
        public required string PasswordHash { get; set; }
        public required string PasswordSalt { get; set; }
        public string Nickname { get; set; } = "";
        public string? Avatar { get; set; }
        public bool PersonalMemoryEnabled { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
    }

    private sealed class ExportAgent
    {
        public required AgentDefinition Definition { get; set; }
        public List<AgentRegistration> Registrations { get; set; } = [];
    }

    private sealed class ExportGroup
    {
        public required Group Group { get; set; }
        public List<GroupMember> Members { get; set; } = [];
        public List<GroupTopic> Topics { get; set; } = [];
        public List<PersistedMessage> Messages { get; set; } = [];
    }
}
