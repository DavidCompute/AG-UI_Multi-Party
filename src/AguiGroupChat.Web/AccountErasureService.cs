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
///   - 其<b>创建的个人知识库</b>连同文档向量 / 图谱物理删除。
///   - 保留：他人群聊历史中该用户发送的消息正文（属共享群历史；如需彻底抹除可另行使用
///     记忆治理 / 管理员介入），以及其创建的数字员工与技能（可能仍被他人群引用，删除会导致悬空成员）。
/// </summary>
public sealed class AccountErasureService
{
    private readonly AuthService _auth;
    private readonly TotpService _totp;
    private readonly GroupHub _hub;
    private readonly KnowledgeBaseCatalog _kbs;
    private readonly IMessageMemory? _memory;
    private readonly AuditLogService _audit;
    private readonly ILogger<AccountErasureService> _logger;

    public AccountErasureService(
        AuthService auth,
        TotpService totp,
        GroupHub hub,
        KnowledgeBaseCatalog kbs,
        IMessageMemory? memory,
        AuditLogService audit,
        ILogger<AccountErasureService> logger)
    {
        _auth = auth;
        _totp = totp;
        _hub = hub;
        _kbs = kbs;
        _memory = memory;
        _audit = audit;
        _logger = logger;
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

            // 6) 删除账号行（4-6 步均成功才删除：半途失败时保留账号行（已停用）便于管理员恢复 / 重试）
            var accountRemoved = _auth.DeleteAccountRow(targetUserId);

            var operatorName = _auth.GetUser(operatorUserId)?.Username ?? operatorUserId;
            _audit.Record("user.account.delete", operatorUserId, operatorName, targetType: "user",
                targetId: targetUserId,
                detail: $"数据擦除：群处置 {groupsHandled} 个、记忆 {memoriesErased} 条、知识库 {kbsRemoved} 个{(string.IsNullOrWhiteSpace(reason) ? "" : $"；原因：{reason}")}");

            _logger.LogInformation("账号数据擦除完成：target={UserId} operator={Operator}（群 {Groups} / 记忆 {Memories} / 知识库 {Kbs}）",
                targetUserId, operatorUserId, groupsHandled, memoriesErased, kbsRemoved);
            return new AccountErasureReport(accountRemoved, groupsHandled, memoriesErased, kbsRemoved);
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
    int KnowledgeBasesRemoved);
