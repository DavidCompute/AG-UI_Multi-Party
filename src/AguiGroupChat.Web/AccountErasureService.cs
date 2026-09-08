using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 账号注销 / 数据擦除编排服务（企业合规 · 数据主体权利）：
/// 把「删除一个账号」涉及的多处数据在调用侧组合成<b>一次性、可汇报</b>的操作，供两个入口复用：
///   1. 管理员「彻底删除」某个用户（AdminApi DELETE /ag-ui/admin/users/{userId}）；
///   2. 用户本人「注销账户」（AccountApi DELETE /ag-ui/account，需密码确认）。
///
/// 删除语义（合规第一刀）：
///   - 账号行 + 全部会话令牌（在线即登出）+ TOTP 密钥被清除；
///   - 其<b>创建的知聚</b>：存在其他用户成员时群主<b>转让</b>给成员并自行退群（保留其余成员的聊天历史与群），
///     否则（仅剩智能体/本人）整群<b>解散</b>（物理删除该群全部消息 / 语义记忆 / 图谱）；
///   - 其<b>加入的他人知聚</b>：自行退群（不触及其他成员的发言、记忆与群本身）；
///   - 其全部<b>发言的语义记忆</b>物理删除；
///   - 其<b>创建的个人知识库</b>连同文档向量 / 图谱物理删除；
///   - 现存群中该用户<b>发言的正文与附件等内容匿名化</b>（正文清空、昵称改「已注销用户」占位、附件 / 提及 /
///     推理 / 技能链 / 计划清除——保留消息行与时间线，其余成员会话上下文不破坏，但其个人数据不再留存）；
///   - <b>其创建的数字员工 / 技能孤儿清理</b>：仍被现存群成员引用（或仍被保留定义引用）的定义保留
///     （保证他人群功能不悬空），其余（随其解散的知聚、私密/分身等）连同触发注册一并删除。
///   - 保留：其余用户发言。
/// </summary>
public sealed class AccountErasureService
{
    /// <summary>注销用户现存发言的占位昵称（消息行匿名化后展示，标识内容已按数据主体权利清除）。</summary>
    public const string DeletedAccountNickname = "已注销用户";
    private readonly AuthService _auth;
    private readonly TotpService _totp;
    private readonly GroupHub _hub;
    private readonly KnowledgeBaseCatalog _kbs;
    private readonly IMessageMemory? _memory;
    private readonly AuditLogService _audit;
    private readonly ILogger<AccountErasureService> _logger;
    // 孤儿清理用（可选）：其创建的数字员工 / 技能定义与触发注册，未被现存群引用的删除、被引用的保留
    private readonly AgentCatalog? _agentCatalog;
    private readonly AgentSkillCatalog? _agentSkills;
    private readonly AgentRegistry? _agentRegistry;

    public AccountErasureService(
        AuthService auth,
        TotpService totp,
        GroupHub hub,
        KnowledgeBaseCatalog kbs,
        IMessageMemory? memory,
        AuditLogService audit,
        ILogger<AccountErasureService> logger,
        AgentCatalog? agentCatalog = null,
        AgentSkillCatalog? agentSkills = null,
        AgentRegistry? agentRegistry = null)
    {
        _auth = auth;
        _totp = totp;
        _hub = hub;
        _kbs = kbs;
        _memory = memory;
        _audit = audit;
        _logger = logger;
        _agentCatalog = agentCatalog;
        _agentSkills = agentSkills;
        _agentRegistry = agentRegistry;
    }

    /// <summary>
    /// 执行账号注销 + 数据擦除。任一前置校验失败（账号不存在 / 最后一名超级管理员）抛 <see cref="AguiProtocolException"/>；
    /// 群处理中途失败则中止删除（不删除账号行，保留已完成的群处置，便于管理员介入后重试）。
    /// </summary>
    public async Task<AccountErasureReport> EraseAsync(string targetUserId, string operatorUserId, string? reason, CancellationToken ct = default)
    {
        // 1) 前置防呆：账号存在；最后一名超级管理员不可删除
        _auth.EnsureUserCanBeRemoved(targetUserId);

        // 2) 立即停用 + 吊销全部会话 + 终止在线连接：防止删除过程中该账号继续产生新数据 / 维持实时会话
        _auth.SetUserDisabled(targetUserId, true);
        _totp.Remove(targetUserId); // TOTP 密钥 / 限速状态一并清除（注销即不可再用于二次验证）

        var groupsHandled = 0;
        try
        {
            // 3) 知聚处置（先处理群，再清记忆 / 知识库：解散群已连带物理删除该群记忆，避免重复遍历）
            var owned = _hub.Store.AllGroups().Where(g => g.OwnerId == targetUserId).ToList();
            foreach (var group in owned)
            {
                ct.ThrowIfCancellationRequested();
                var successor = PickSuccessorOwner(group.GroupId, targetUserId);
                if (successor is null)
                {
                    // 无可转让用户成员（仅本人 / 智能体）：整群解散（连带消息 / 记忆 / 图谱删除）
                    await _hub.DisbandGroupAsync(new GroupDisbandRequest { GroupId = group.GroupId, OperatorId = targetUserId }, ct);
                }
                else
                {
                    // 群主转让给其他用户成员，原群主（本人）随后退群——保留其余成员的群与历史
                    await _hub.TransferOwnershipAsync(group.GroupId, targetUserId, successor, ct);
                    if (_hub.Store.IsMember(group.GroupId, targetUserId))
                        await _hub.LeaveGroupAsync(group.GroupId, targetUserId, ct);
                }
                groupsHandled++;
            }

            // 非群主但仍是成员的群：直接退群（不触及其他成员的发言 / 记忆 / 群）
            foreach (var group in _hub.Store.GroupsOf(targetUserId).Where(g => g.OwnerId != targetUserId).ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (!_hub.Store.IsMember(group.GroupId, targetUserId)) continue; // 并发已被移出
                await _hub.LeaveGroupAsync(group.GroupId, targetUserId, ct);
                groupsHandled++;
            }

            // 4) 该用户发言的语义记忆：跨全部群按发送者物理删除
            var memoriesErased = EraseOwnMemories(targetUserId);

            // 5) 个人知识库：连同文档向量 / 图谱删除
            var kbsRemoved = _kbs.ListAll().Where(k => k.OwnerId == targetUserId)
                .Count(k => _kbs.RemoveKb(k.KbId));

            // 5b) 现存群中该用户的发言匿名化：正文 / 附件 / 提及 / 推理 / 链 / 计划清空、昵称改占位。
            //     仅影响其本人消息（不触碰他人发言）；其拥有且已解散的群消息已被物理删除，不在此列。
            var messagesAnonymized = _hub.Store.AnonymizeSender(targetUserId, DeletedAccountNickname);

            // 5c) 其创建的数字员工 / 技能孤儿清理：未被现存群引用（成员身份 / 保留定义的交接引用）的定义删除，
            //     含分身（twin_*）；仍被引用的保留（保证他人知聚功能不悬空）。
            var (agentsRemoved, skillsRemoved) = CleanupOrphanedDefinitions(targetUserId);

            // 6) 删除账号行（4-6 步均成功才删除：半途失败时保留账号行（已停用）便于管理员恢复 / 重试）
            var accountRemoved = _auth.DeleteAccountRow(targetUserId);

            var operatorName = _auth.GetUser(operatorUserId)?.Username ?? operatorUserId;
            _audit.Record("user.account.delete", operatorUserId, operatorName, targetType: "user",
                targetId: targetUserId,
                detail: $"数据擦除：群处置 {groupsHandled} 个、记忆 {memoriesErased} 条、知识库 {kbsRemoved} 个、匿名化发言 {messagesAnonymized} 条、清理数字员工 {agentsRemoved} / 技能 {skillsRemoved}{(string.IsNullOrWhiteSpace(reason) ? "" : $"；原因：{reason}")}");

            _logger.LogInformation("账号数据擦除完成：target={UserId} operator={Operator}（群 {Groups} / 记忆 {Memories} / 知识库 {Kbs} / 匿名化发言 {Messages} / 清理数字员工 {Agents} 技能 {Skills}）",
                targetUserId, operatorUserId, groupsHandled, memoriesErased, kbsRemoved, messagesAnonymized, agentsRemoved, skillsRemoved);
            return new AccountErasureReport(accountRemoved, groupsHandled, memoriesErased, kbsRemoved, messagesAnonymized, agentsRemoved, skillsRemoved);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "账号数据擦除失败（账号保留并保持停用，需管理员介入）：target={UserId}", targetUserId);
            throw;
        }
    }

    /// <summary>在被删除用户拥有的群中选择群主继承人：优先群管理员，其次任一用户成员；仅剩智能体时返回 null（解散）。</summary>
    private string? PickSuccessorOwner(string groupId, string ownerId)
    {
        var members = _hub.Store.ListMembers(groupId)
            .Where(m => m.MemberId != ownerId && m.MemberType == MemberType.User)
            .OrderBy(m => m.Role == GroupRole.Admin ? 0 : 1) // 群管理员优先承接
            .ThenBy(m => m.JoinTime)
            .ToList();
        return members.Count == 0 ? null : members[0].MemberId;
    }

    /// <summary>该用户创建的知聚 / 技能定义孤儿清理：仅删除<b>未被引用</b>的（现存群成员身份 / 任一现存定义的中继、
    /// 升级引用），避免他人知聚因删除定义而悬空。同时清理被删数字员工的群内触发注册。返回 (删除智能体, 删除技能)。</summary>
    private (int AgentsRemoved, int SkillsRemoved) CleanupOrphanedDefinitions(string userId)
    {
        if (_agentCatalog is null && _agentSkills is null) return (0, 0);

        var allDefs = _agentCatalog?.ListDefinitions() ?? [];
        if (allDefs.Count == 0 && _agentSkills is not null) return (0, RemoveOrphanSkills(userId));

        // 现存群中以成员身份引用的智能体：删除会悬空他人知聚 → 保留
        var memberAgentIds = _hub.Store.AllGroups()
            .SelectMany(g => _hub.Store.ListMembers(g.GroupId))
            .Where(m => m.MemberType == MemberType.Agent)
            .Select(m => m.MemberId)
            .ToHashSet(StringComparer.Ordinal);
        // 被任一现存定义以「中继 / 升级」引用的智能体同样保留（避免链路悬空）
        var referencedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in allDefs)
        {
            if (!string.IsNullOrWhiteSpace(d.RelayToAgentId)) referencedIds.Add(d.RelayToAgentId!);
            if (!string.IsNullOrWhiteSpace(d.EscalationAgentId)) referencedIds.Add(d.EscalationAgentId!);
        }

        var agentsRemoved = 0;
        if (_agentCatalog is not null)
        {
            foreach (var d in allDefs.Where(d => string.Equals(d.OwnerId, userId, StringComparison.Ordinal)).ToList())
            {
                if (memberAgentIds.Contains(d.AgentId) || referencedIds.Contains(d.AgentId)) continue; // 仍被引用 → 保留
                if (_agentCatalog.Remove(d.AgentId)) agentsRemoved++;
                _agentRegistry?.Unregister(d.AgentId, null); // 同步清空其全部群内触发注册（含被解散的知聚遗留）
            }
        }

        var skillsRemoved = RemoveOrphanSkills(userId);
        _logger.LogInformation("注销清理数字员工 / 技能：user={UserId} 删除智能体 {Agents} / 技能 {Skills}", userId, agentsRemoved, skillsRemoved);
        return (agentsRemoved, skillsRemoved);
    }

    /// <summary>删除该用户创建且未被任何<b>现存</b>数字员工引用（SkillDefIds / Skills）的技能定义。</summary>
    private int RemoveOrphanSkills(string userId)
    {
        if (_agentSkills is null) return 0;
        var usedSkills = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in _agentCatalog?.ListDefinitions() ?? [])
        {
            foreach (var sid in def.SkillDefIds ?? [])
                if (!string.IsNullOrWhiteSpace(sid)) usedSkills.Add(sid);
            foreach (var s in def.Skills ?? [])
                if (!string.IsNullOrWhiteSpace(s.SkillId)) usedSkills.Add(s.SkillId);
        }

        var removed = 0;
        foreach (var s in _agentSkills.ListAll().Where(s => string.Equals(s.OwnerId, userId, StringComparison.Ordinal)).ToList())
        {
            if (usedSkills.Contains(s.SkillId)) continue;
            if (_agentSkills.Remove(s.SkillId)) removed++;
        }
        return removed;
    }

    /// <summary>按发送者物理删除该用户的全部语义记忆（跨全部群）。删除为物理移除，后续条目前移：
    /// 反复从 offset 0 取页并删除，直到无推进（页内全部删除失败 / 已被见过）为止；最多清理 5 万条防死循环。</summary>
    private int EraseOwnMemories(string userId)
    {
        if (_memory is null) return 0;
        const int pageSize = 500;
        const int maxAffected = 50000;
        var affected = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 0; round < 200; round++)
        {
            var items = _memory.ListMessages(null, userId, null, pageSize, 0);
            if (items.Count == 0) break;
            var progressed = false;
            foreach (var item in items)
            {
                if (!seen.Add(item.MessageId)) continue; // 删除失败遗留项：跳过防死循环
                try
                {
                    if (_memory.DeleteByMessageId(item.MessageId)) { affected++; progressed = true; }
                }
                catch (Exception ex)
                {
                    // 单条记忆删除失败不阻断整体擦除（记录后继续），避免一条脏数据卡死注销流程
                    _logger.LogWarning(ex, "记忆删除失败（继续擦除）：message={MessageId} user={UserId}", item.MessageId, userId);
                }
            }
            if (!progressed) break;   // 本页全部删除失败 / 重复：无法再推进，终止
            if (affected >= maxAffected) break;
        }
        return affected;
    }
}

/// <summary>账号数据擦除结果汇报（返回给调用端）。</summary>
public sealed record AccountErasureReport(
    bool AccountRemoved,
    int GroupsHandled,
    int MemoriesErased,
    int KnowledgeBasesRemoved,
    int MessagesAnonymized = 0,
    int AgentsRemoved = 0,
    int SkillsRemoved = 0);
