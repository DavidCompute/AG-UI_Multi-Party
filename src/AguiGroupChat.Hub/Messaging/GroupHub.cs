using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Hub.Messaging;

/// <summary>
/// 群聊协议核心枢纽：承载协议中全部群组生命周期 / 成员 / 消息 / 订阅逻辑，
/// 并负责把事件按可见性规则扇出到已订阅的客户端连接。
/// </summary>
public sealed class GroupHub : IDisposable
{
    private readonly IGroupStore _store;
    private readonly IUserStore _users;
    private readonly ConnectionManager _connections;
    private readonly AgentRegistry _agents;
    private readonly AgentTriggerService _triggers;
    private readonly IAgentGateway _agentGateway;
    private readonly GroupChatOptions _options;
    private readonly TimeProvider _time;
    private readonly ChangeHub? _changes;
    private readonly ILogger<GroupHub> _logger;
    private readonly IMessageMemory? _memory;
    private readonly IAgentDefinitionStore? _agentDefinitions;
    private readonly ITwinAgentSync? _twinSync;
    private readonly IGraphMemory? _graph;
    private readonly ConcurrentDictionary<string, byte> _disbanded = new();
    // 客服知聚的非成员参与者（顾客）：key=groupId → 已进入的顾客 id 集合。顾客不是群成员，
    // 各自拥有与客服团队的独立会话（顾客之间彼此隔离）。仅存于内存（会话参与非持久成员）。
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _supportCustomers = new(StringComparer.Ordinal);
    // 顾客参与 TTL（30 分钟无交互自动回收），定时清理防止条目泄漏
    private readonly ConcurrentDictionary<string, long> _supportCustomerTtl = new(StringComparer.Ordinal);
    private const long SupportCustomerTtlMs = 30 * 60 * 1000;
    private const string SupportCustKeySep = "|";
    private readonly ConcurrentDictionary<string, AgentStreamState> _agentStreams = new();
    // 智能体触发调用并发限制（防止语境智能体多 / 消息频繁时打爆模型与桥接服务）
    private readonly SemaphoreSlim _agentInvocationLimiter;
    // 流式消息待落库内容：messageId → (群, 累计文本, 上次写库时间戳)。防抖窗口内合并，消息结束时强制落库（数据库模式）。
    private readonly ConcurrentDictionary<string, PendingMessageContent> _pendingContent = new(StringComparer.Ordinal);
    // 流式内容读-改-写与落库的互斥锁（AppendAgentContentAsync / FlushPendingContent 共用；锁内无 await，同线程可重入）
    private readonly object _pendingLock = new();
    // 孤儿流兜底：周期扫描超时未 End 且长期无活跃交互的流式消息强制收尾（防 _agentStreams / 防抖缓冲泄漏）
    private const long OrphanCleanupIntervalMs = 60 * 1000;    // 扫描周期 60s
    private const long OrphanStreamTimeoutMs = 10 * 60 * 1000; // 创建超过 10 分钟视为可疑孤儿
    private const long OrphanStreamIdleMs = 60 * 1000;         // 最近 60s 无任何追加 / 重置交互视为无活跃
    private readonly Timer? _orphanTimer;

    /// <summary>typing 广播节流：memberId → 上次广播时间戳。1 秒内重复广播忽略，防脚本刷爆扇出与持久化。</summary>
    private const long TypingThrottleMs = 1000;
    private readonly ConcurrentDictionary<string, long> _lastTypingAt = new(StringComparer.Ordinal);

    /// <summary>撤回时限：仅允许撤回发送 3 分钟内的消息（前端按钮同步按时间隐藏，服务端强校验）。</summary>
    private const long RecallWindowMs = 3 * 60 * 1000;

    /// <summary>单条消息 @ 提及成员数量上限（防扇出放大 / 内存膨胀）。</summary>
    private const int MaxMentionsPerMessage = 100;

    /// <summary>单条消息附件数量上限（与前端上传上限一致，服务端强校验）。</summary>
    private const int MaxAttachmentsPerMessage = 9;

    /// <summary>智能体消息流式状态：接收者集合（Start 时确定，Content/End 沿用）+ 创建 / 最近活跃时间（孤儿流兜底判定用）。</summary>
    private sealed record AgentStreamState(string GroupId, HashSet<string> Recipients, long CreatedAt, long LastActivityMs);

    /// <summary>防抖中的流式内容快照：Content 为到当前为止的完整累计文本，LastWriteMs 为上次实际写库时间。</summary>
    private sealed record PendingMessageContent(string GroupId, string Content, long LastWriteMs);

    public GroupHub(
        IGroupStore store,
        IUserStore users,
        ConnectionManager connections,
        AgentRegistry agents,
        AgentTriggerService triggers,
        IAgentGateway agentGateway,
        GroupChatOptions options,
        TimeProvider time,
        ILogger<GroupHub> logger,
        ChangeHub? changes = null,
        IMessageMemory? memory = null,
        IAgentDefinitionStore? agentDefinitions = null,
        ITwinAgentSync? twinSync = null,
        IGraphMemory? graph = null)
    {
        _store = store;
        _users = users;
        _connections = connections;
        _agents = agents;
        _triggers = triggers;
        _agentGateway = agentGateway;
        _options = options;
        _time = time;
        _logger = logger;
        _changes = changes;
        _memory = memory;
        _agentDefinitions = agentDefinitions;
        _twinSync = twinSync;
        _graph = graph;
        _agentInvocationLimiter = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentAgentInvocations));
        // 孤儿流兜底定时器：周期清理 End 丢失 / 智能体进程崩溃的流式消息（方法内 try/catch，Timer 随 Dispose 释放）
        _orphanTimer = new Timer(_ => CleanupOrphanStreams(), null, OrphanCleanupIntervalMs, OrphanCleanupIntervalMs);
    }

    public long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public IGroupStore Store => _store;

    public void Dispose() => _orphanTimer?.Dispose(); // 兜底定时器随宿主生命周期释放（DI 单例自动销毁）

    // ================= 群组生命周期（协议 4.2 / 5.2） =================

    public async Task<Group> CreateGroupAsync(GroupCreateRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.OwnerId))
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "ownerId 不能为空");
        if (string.IsNullOrWhiteSpace(req.GroupName))
            throw new AguiProtocolException(ErrorCodes.BadRequest, "groupName 不能为空");

        var groupId = "group_" + IdGenerator.NewId();
        var now = NowMs;

        var detailMap = new Dictionary<string, MemberSeed>(StringComparer.Ordinal);
        if (req.Members is not null)
            foreach (var m in req.Members) detailMap.TryAdd(m.MemberId, m); // 重复 MemberId 容错（取首个，不抛异常）
        var allIds = new List<string> { req.OwnerId };
        allIds.AddRange((req.MemberIds ?? []).Where(id => id != req.OwnerId));
        allIds = allIds.Distinct(StringComparer.Ordinal).ToList();

        // 私密智能体：仅创建者可将它拉进群（创建者 = 群创建者 req.OwnerId）
        EnsureCanAddAgents(req.OwnerId, allIds);

        if (allIds.Count > _options.MaxGroupMembers)
            throw new AguiProtocolException(ErrorCodes.GroupFull, $"群成员数量超过上限（{_options.MaxGroupMembers}）");

        var members = allIds.Select(id =>
        {
            detailMap.TryGetValue(id, out var detail);
            var type = detail?.MemberType ?? ResolveMemberType(id);
            var role = id == req.OwnerId ? GroupRole.Owner : GroupRole.Normal;
            // 客服知聚：创建者拉入的团队成员（真人 / 数字员工）均为客服（可看全部会话）；
            // 自动加入的普通用户为顾客（只能看到自己的会话），由 enter/auto-join 以 Normal 身份进入。
            if (req.Kind == GroupKind.Support && id != req.OwnerId) role = GroupRole.Admin;
            return new GroupMember
            {
                MemberId = id,
                MemberType = type,
                Nickname = !string.IsNullOrWhiteSpace(detail?.Nickname) ? detail!.Nickname! : DefaultNickname(id),
                // 未显式指定头像时回退到用户账号头像（智能体由前端随 MemberSeed 携带）
                Avatar = detail?.Avatar ?? (type == MemberType.User ? _users.GetUserById(id)?.Avatar : null),
                Role = role,
                // 按实际连接状态初始化（不能一律 Offline：在线成员入群应立即显示在线，否则分身互斥/状态点显示错误）
                OnlineStatus = _connections.MemberConnectionCount(id) > 0 ? OnlineStatus.Online : OnlineStatus.Offline,
                JoinTime = now,
            };
        }).ToList();

        // 客服知聚不支持私密（需对所有用户可见、可进入）；显式覆盖 IsPrivate=false 防止误用
        var isSupport = req.Kind == GroupKind.Support;
        var extra = req.Extra is { } e ? new Dictionary<string, object?>(e) : new Dictionary<string, object?>();
        extra["kind"] = isSupport ? "support" : "normal";

        var group = new Group
        {
            GroupId = groupId,
            GroupName = req.GroupName,
            GroupAvatar = req.GroupAvatar,
            OwnerId = req.OwnerId,
            IsPrivate = isSupport ? false : req.IsPrivate,
            MemberCount = members.Count,
            CreateTime = now,
            Extra = extra,
        };

        // 事务性建群：群 + 首批成员一次写入（数据库模式单事务、失败回滚，防半建状态）
        if (!_store.CreateGroupWithMembers(group, members))
            throw new AguiProtocolException(ErrorCodes.GroupNotFound, "群组创建失败（ID 冲突）");

        // 分身跟随：群主启用分身时，分身自动加入新建的公开群
        await SyncTwinMembersInAsync(groupId, [req.OwnerId], ct);

        var createdEvt = new GroupCreatedEvent
        {
            GroupId = groupId,
            GroupInfo = group,
            Members = members,
            Timestamp = now,
        };
        await FanOutAsync(groupId, createdEvt, ct: ct);
        // 通知全部新成员（含创建者）的活跃连接刷新群列表：新群刚创建无订阅者，常规扇出其他成员收不到
        await NotifyMemberConnectionsAsync(allIds, createdEvt, ct);

        _logger.LogInformation("群组已创建 {GroupId}（{Name}，{Count} 人）", groupId, req.GroupName, members.Count);
        return group;
    }

    // ================= 数字员工单聊（user ↔ agent 的 1:1 私有会话） =================

    /// <summary>
    /// 「数字员工列表 → 私聊」：幂等地为 (ownerId, agentId) 建立/复用一**个确定且彼此独立**的私有双人群（kind=direct）。
    /// 不同用户与同一数字员工的单聊各自有独立群（互不可见），同一用户反复进入返回已存在群。
    /// 群主 = 真人用户 ownerId；另一端 = 数字员工 agentId（注册 mentioned 触发规则；普通消息即视为对对端“直达”触发，见 TriggerAgents）。
    /// 单聊默认私密（isPrivate=true），语义记忆仅在本私群内可被检索，隔离于其它群。</summary>
    public async Task<Group> TryEnsureDirectChatAsync(string ownerId, string agentId, string? agentNickname, string? agentAvatar, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "缺少单聊发起者（ownerId）");
        if (string.IsNullOrWhiteSpace(agentId))
            throw new AguiProtocolException(ErrorCodes.BadRequest, "缺少单聊对象（agentId）");
        if (string.Equals(ownerId, agentId, StringComparison.Ordinal))
            throw new AguiProtocolException(ErrorCodes.BadRequest, "不能与自己单聊");

        var groupId = DirectChatGroupId(ownerId, agentId);
        if (_store.GetGroup(groupId) is { } existing)
        {
            // 幂等：群已存在但发起者不在其中（理论上只可能由极端 ID 冲突造成）→ 拒绝，不改写他人会话
            if (!_store.IsMember(groupId, ownerId))
                throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "该会话已存在且你不属于其中");
            return existing;
        }

        EnsureCanAddAgents(ownerId, [agentId]);
        var now = NowMs;
        var ownerUser = _users.GetUserById(ownerId);
        var members = new List<GroupMember>
        {
            new()
            {
                MemberId = ownerId,
                MemberType = MemberType.User,
                Nickname = ownerUser?.Nickname ?? ownerUser?.Username ?? DefaultNickname(ownerId),
                Avatar = ownerUser?.Avatar,
                Role = GroupRole.Owner,
                OnlineStatus = _connections.MemberConnectionCount(ownerId) > 0 ? OnlineStatus.Online : OnlineStatus.Offline,
                JoinTime = now,
            },
            new()
            {
                MemberId = agentId,
                MemberType = MemberType.Agent, // 单聊对端只能是数字员工
                Nickname = !string.IsNullOrWhiteSpace(agentNickname) ? agentNickname! : DefaultNickname(agentId),
                Avatar = agentAvatar,
                Role = GroupRole.Normal,
                OnlineStatus = _connections.MemberConnectionCount(agentId) > 0 ? OnlineStatus.Online : OnlineStatus.Offline,
                JoinTime = now,
            },
        };
        var extra = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = "direct" };
        var group = new Group
        {
            GroupId = groupId,
            // 私有双人单聊：名字直接用它展示的会话名（前端渲染时可用对端昵称覆盖显示标题）
            GroupName = string.IsNullOrWhiteSpace(agentNickname) ? $"与数字员工 {agentId} 的单聊" : $"与 {agentNickname!.Trim()} 的单聊",
            OwnerId = ownerId,
            IsPrivate = true,
            MemberCount = members.Count,
            CreateTime = now,
            Extra = extra,
        };

        // 事务性建群（失败回滚，防半建）：确定性群 ID 已存在即视为并发/幂等冲突
        if (!_store.CreateGroupWithMembers(group, members))
        {
            var raced = _store.GetGroup(groupId);
            if (raced is not null && _store.IsMember(groupId, ownerId))
                return raced; // 同一时刻已建成：直接复用
            throw new AguiProtocolException(ErrorCodes.GroupFull, "单聊创建失败，请重试");
        }

        // 注册该数字员工在本群的触发规则（默认 mentioned；群内显式覆盖保留语义）
        RegisterAgent(new AgentRegisterRequest
        {
            AgentId = agentId,
            Nickname = members[1].Nickname,
            GroupIds = [groupId],
            TriggerMode = AgentTriggerMode.Mentioned,
        });

        _changes?.Notify();
        _logger.LogInformation("数字员工单聊已创建 {GroupId}（{Owner} ↔ {Agent}）", groupId, ownerId, agentId);
        return group;
    }

    /// <summary>确定性单聊群 ID：由 (ownerId, agentId) 取 SHA-256 前 20 位十六进制，保证统一对幂等复用、不同用户之间独立。</summary>
    internal static string DirectChatGroupId(string ownerId, string agentId)
    {
        var raw = string.Join("\u001F", ownerId, agentId);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
        return "group_direct_" + hash[..20].ToLowerInvariant();
    }

    public async Task<Group> UpdateGroupAsync(GroupUpdateRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        EnsureCanManage(req.OperatorId, group);

        var fields = req.UpdateFields.Distinct(StringComparer.Ordinal).ToList();
        if (fields.Contains("groupName", StringComparer.Ordinal))
        {
            var name = GetString(req.GroupInfo, "groupName");
            if (!string.IsNullOrWhiteSpace(name)) group.GroupName = name;
        }
        if (fields.Contains("groupAvatar", StringComparer.Ordinal))
            group.GroupAvatar = GetString(req.GroupInfo, "groupAvatar");
        if (fields.Contains("isPrivate", StringComparer.Ordinal))
            group.IsPrivate = GetBool(req.GroupInfo, "isPrivate");

        await FanOutAsync(group.GroupId, new GroupUpdatedEvent
        {
            GroupId = group.GroupId,
            UpdateFields = fields,
            GroupInfo = new Dictionary<string, JsonElement>(req.GroupInfo),
            OperatorId = req.OperatorId,
            Timestamp = NowMs,
        }, ct: ct);
        _store.UpdateGroup(group); // 群名 / 头像为原地修改，落库（数据库模式）
        _changes?.Notify();
        return group;
    }

    public async Task DisbandGroupAsync(GroupDisbandRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        if (req.OperatorId != group.OwnerId)
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅群主可解散群组");

        // 先封禁再广播：解散标记先行，防止广播期间并发的发消息 / 订阅继续产生孤儿行或重新激活群
        _disbanded.TryAdd(group.GroupId, 0);

        await FanOutAsync(group.GroupId, new GroupDisbandedEvent
        {
            GroupId = group.GroupId,
            OperatorId = req.OperatorId,
            Timestamp = NowMs,
        }, ct: ct);

        _store.RemoveGroup(group.GroupId);
        _memory?.RemoveGroup(group.GroupId); // 解散群：同步物理删除该群全部语义记忆（含 pgvector 向量）
        _graph?.RemoveGroup(group.GroupId); // 解散群：同步删除该群图谱（实体 + 边）
        // 清理该群残留的流式状态（_agentStreams / 防抖待落库内容 / typing 节流表），避免内存泄漏与后续误写
        foreach (var kv in _agentStreams.Where(kv => kv.Value.GroupId == group.GroupId).ToList())
            _agentStreams.TryRemove(kv.Key, out _);
        foreach (var kv in _pendingContent.Where(kv => kv.Value.GroupId == group.GroupId).ToList())
            _pendingContent.TryRemove(kv.Key, out _);
        foreach (var kv in _lastTypingAt.Where(kv => _store.GetMember(group.GroupId, kv.Key) is null).ToList())
            _lastTypingAt.TryRemove(kv.Key, out _);
        foreach (var c in _connections.SubscribersOf(group.GroupId).ToList())
            _connections.Unsubscribe(c, group.GroupId);

        _logger.LogInformation("群组已解散 {GroupId}", group.GroupId);
    }

    // ================= 群成员（协议 4.3 / 5.2） =================

    public async Task<IReadOnlyList<GroupMember>> AddMembersAsync(GroupMemberAddRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        EnsureCanManage(req.OperatorId, group);
        return await AddMembersCoreAsync(group, req.OperatorId, req.MemberIds, req.MemberDetails, ct);
    }

    /// <summary>
    /// 系统内部添加成员（分身跟随 / 同步用）：不校验操作者群管理权限（操作者是普通成员也可由系统代加入），
    /// 仍校验私密智能体归属（分身归属者即操作者，天然通过）。
    /// </summary>
    public async Task<IReadOnlyList<GroupMember>> AddSystemMembersAsync(
        string groupId, IReadOnlyList<string> memberIds, string operatorId,
        IReadOnlyList<MemberSeed>? details, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(groupId);
        EnsureCanAddAgents(operatorId, memberIds);
        return await AddMembersCoreAsync(group, operatorId, memberIds, details, ct);
    }

    private async Task<IReadOnlyList<GroupMember>> AddMembersCoreAsync(
        Group group, string operatorId, IReadOnlyList<string> memberIds,
        IReadOnlyList<MemberSeed>? memberDetails, CancellationToken ct)
    {
        var now = NowMs;
        var detailMap = new Dictionary<string, MemberSeed>(StringComparer.Ordinal);
        if (memberDetails is not null)
            foreach (var m in memberDetails) detailMap.TryAdd(m.MemberId, m); // 重复 MemberId 容错
        var added = new List<GroupMember>();

        foreach (var id in memberIds.Distinct(StringComparer.Ordinal))
        {
            if (_store.IsMember(group.GroupId, id)) continue;
            if (_store.MemberCount(group.GroupId) + added.Count >= _options.MaxGroupMembers)
                throw new AguiProtocolException(ErrorCodes.GroupFull, "群成员数量达上限");

            detailMap.TryGetValue(id, out var detail);
            var type = detail?.MemberType ?? ResolveMemberType(id);
            var member = new GroupMember
            {
                MemberId = id,
                MemberType = type,
                Nickname = !string.IsNullOrWhiteSpace(detail?.Nickname) ? detail!.Nickname! : DefaultNickname(id),
                // 未显式指定头像时回退到用户账号头像（智能体由前端随 MemberSeed 携带）
                Avatar = detail?.Avatar ?? (type == MemberType.User ? _users.GetUserById(id)?.Avatar : null),
                Role = GroupRole.Normal,
                // 按实际连接状态初始化（在线成员入群立即显示在线；离线则显示离线）
                OnlineStatus = _connections.MemberConnectionCount(id) > 0 ? OnlineStatus.Online : OnlineStatus.Offline,
                JoinTime = now,
            };
            _store.AddMember(group.GroupId, member);
            added.Add(member);
        }

        if (added.Count == 0) return added;

        group.MemberCount = _store.MemberCount(group.GroupId);
        var joinedEvt = new GroupMemberJoinedEvent
        {
            GroupId = group.GroupId,
            Members = added,
            OperatorId = operatorId,
            Timestamp = now,
        };
        await FanOutAsync(group.GroupId, joinedEvt, ct: ct);
        // 通知新成员的活跃连接刷新群列表（新成员通常未订阅本群，常规扇出收不到）
        await NotifyMemberConnectionsAsync(added.Select(m => m.MemberId), joinedEvt, ct);

        // 入群快照（协议 4.7：成员入群成功后推送状态快照；订阅成功路径同样会推送）
        // 快照按入群成员身份过滤可见性（定向 / 私聊消息只对发送者与目标成员可见）
        foreach (var newMember in added)
        {
            var snapshot = await BuildSnapshotAsync(group.GroupId, newMember.MemberId, ct);
            await FanOutAsync(group.GroupId, snapshot, onlyTo: new HashSet<string> { newMember.MemberId }, ct: ct);
        }

        // 分身跟随：新增的用户成员若已启用分身，分身自动加入该公开群
        await SyncTwinMembersInAsync(group.GroupId, added.Where(m => m.MemberType == MemberType.User).Select(m => m.MemberId), ct);

        return added;
    }

    public async Task<IReadOnlyList<string>> RemoveMembersAsync(GroupMemberRemoveRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        EnsureCanManage(req.OperatorId, group);

        var removed = new List<string>();
        foreach (var id in req.MemberIds.Distinct(StringComparer.Ordinal))
        {
            if (id == group.OwnerId)
                throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "不能移除群主");
            var target = _store.GetMember(group.GroupId, id)
                ?? throw new AguiProtocolException(ErrorCodes.GroupMemberNotExist, $"成员 {id} 不在群组内");
            if (target.Role == GroupRole.Admin && req.OperatorId != group.OwnerId)
                throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅群主可移除管理员");

            _store.RemoveMember(group.GroupId, id);
            removed.Add(id);
        }

        if (removed.Count == 0) return removed;

        group.MemberCount = _store.MemberCount(group.GroupId);

        // 先广播，再解除被移出成员的订阅（保证其能收到 GROUP_MEMBER_LEFT）
        await FanOutAsync(group.GroupId, new GroupMemberLeftEvent
        {
            GroupId = group.GroupId,
            MemberIds = removed,
            LeaveType = LeaveType.Kick,
            OperatorId = req.OperatorId,
            Timestamp = NowMs,
        }, ct: ct);

        foreach (var c in _connections.SubscribersOf(group.GroupId).ToList())
            if (removed.Contains(c.MemberId))
                _connections.Unsubscribe(c, group.GroupId);

        // 分身跟随：被移除的用户其分身一并退出该群
        await SyncTwinMembersOutAsync(group.GroupId, removed, ct);

        return removed;
    }

    public async Task LeaveGroupAsync(string groupId, string memberId, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(groupId);
        if (memberId == group.OwnerId)
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "群主不能直接退群，请先转让群主或解散群组");
        _ = _store.GetMember(groupId, memberId)
            ?? throw new AguiProtocolException(ErrorCodes.GroupMemberNotExist, "成员不在群组内");

        _store.RemoveMember(groupId, memberId);
        group.MemberCount = _store.MemberCount(groupId);

        await FanOutAsync(groupId, new GroupMemberLeftEvent
        {
            GroupId = groupId,
            MemberIds = [memberId],
            LeaveType = LeaveType.Voluntary,
            OperatorId = memberId,
            Timestamp = NowMs,
        }, ct: ct);

        foreach (var c in _connections.SubscribersOf(groupId).ToList())
            if (c.MemberId == memberId)
                _connections.Unsubscribe(c, groupId);

        // 分身跟随：用户主动退群时其分身一并退出
        await SyncTwinMembersOutAsync(groupId, [memberId], ct);
    }

    /// <summary>进入客服知聚：客服知聚对所有用户可见、可进入。客服（成员）直接进入；
    /// 非成员普通用户作为一个<b>顾客参与者</b>进入——<b>不加入成员</b>，但获得与本客服团队聊天的独立会话
    /// （每位顾客的会话彼此隔离）。普通知聚不支持自行进入（保持成员制）。返回群信息。</summary>
    public async Task<Group> EnterSupportCircleAsync(string groupId, string memberId, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(groupId);
        if (!group.IsSupportCircle)
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅客服知聚可自行进入");
        if (_store.IsMember(groupId, memberId))
            return group; // 客服（成员）直接进入

        // 非成员：登记为顾客参与者（不入成员表，不改变成员数与成员清单）
        var groupCustomers = _supportCustomers.GetOrAdd(groupId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        groupCustomers[memberId] = 0;
        RefreshSupportCustomer(groupId, memberId);
        return group;
    }

    private static string SupportCustKey(string groupId, string memberId) => groupId + SupportCustKeySep + memberId;

    private void RefreshSupportCustomer(string groupId, string memberId)
        => _supportCustomerTtl[SupportCustKey(groupId, memberId)] = NowMs + SupportCustomerTtlMs;

    /// <summary>是否为客服知聚的顾客参与者（非成员但已进入）。</summary>
    public bool IsSupportCustomer(string groupId, string memberId)
    {
        if (!(_store.GetGroup(groupId)?.IsSupportCircle ?? false)) return false;
        if (!_supportCustomers.TryGetValue(groupId, out var customers)) return false;
        RefreshSupportCustomer(groupId, memberId); // 按需续期
        return customers.ContainsKey(memberId);
    }

    /// <summary>能访问（订阅 / 读写 / 聊天）该客服知聚：客服成员或已进入的顾客参与者。普通知聚保持成员制。</summary>
    public bool CanParticipate(string groupId, string userId)
    {
        var group = _store.GetGroup(groupId);
        if (group is null) return false;
        return _store.IsMember(groupId, userId)
            || (group.IsSupportCircle && _supportCustomers.TryGetValue(groupId, out var customers) && customers.ContainsKey(userId));
    }

    /// <summary>周期清理超时未活动的客服顾客参与记录，防止长期不访问的条目泄漏。</summary>
    private void PurgeExpiredSupportCustomers()
    {
        var now = NowMs;
        foreach (var kv in _supportCustomerTtl)
            if (kv.Value < now)
            {
                var key = kv.Key;
                if (_supportCustomerTtl.TryRemove(key, out _))
                {
                    var sep = key.IndexOf(SupportCustKeySep, StringComparison.Ordinal);
                    if (sep > 0 && _supportCustomers.TryGetValue(key[..sep], out var customers))
                        customers.TryRemove(key[(sep + SupportCustKeySep.Length)..], out _);
                }
            }
    }

    public async Task<GroupMember> UpdateMemberAsync(GroupMemberUpdateRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        var member = _store.GetMember(group.GroupId, req.MemberId)
            ?? throw new AguiProtocolException(ErrorCodes.GroupMemberNotExist, "目标成员不在群组内");
        _ = _store.GetMember(group.GroupId, req.OperatorId)
            ?? throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "操作者不是群成员");

        var fields = req.UpdateFields.Distinct(StringComparer.Ordinal).ToList();

        if (fields.Contains("role", StringComparer.Ordinal))
        {
            if (!CanManage(req.OperatorId, group))
                throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅群主或管理员可修改成员角色");
            if (req.MemberId == group.OwnerId)
                throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "不能修改群主角色（群主转让请用转让接口）");
            // 角色合法值仅为 Normal / Admin：不允许把成员标为 Owner（Owner 由群主转让独占），
            // 也不允许群管理员给他人或自己授予 / 回收 Admin（Admin 管理仅群主可操作，防管理员自治提权）
            if (req.MemberInfo.TryGetValue("role", out var roleJe) && roleJe.ValueKind == JsonValueKind.String
                && Enum.TryParse<GroupRole>(roleJe.GetString(), true, out var requestedRole)
                && requestedRole == GroupRole.Owner)
                throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "Owner 角色仅能通过群主转让得到，不支持在此修改");
            // 设置 / 撤销 Admin：仅群主本人（防止一个管理员把另一个或自己捧成 Admin）
            if (req.MemberInfo.TryGetValue("role", out var roleJe2) && roleJe2.ValueKind == JsonValueKind.String
                && Enum.TryParse<GroupRole>(roleJe2.GetString(), true, out var r)
                && r == GroupRole.Admin
                && req.OperatorId != group.OwnerId)
                throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅群主可授予 / 撤销群管理员角色");
        }
        if (fields.Contains("nickname", StringComparer.Ordinal) || fields.Contains("avatar", StringComparer.Ordinal))
        {
            if (req.MemberId != req.OperatorId && !CanManage(req.OperatorId, group))
                throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅本人或管理员可修改昵称 / 头像");
        }
        // 在线状态是服务端连接管理维护的瞬时量：拒绝客户端伪造任意成员的在线状态（防止污染在场感知 / 已读回执）
        if (fields.Contains("onlineStatus", StringComparer.Ordinal))
            throw new AguiProtocolException(ErrorCodes.BadRequest, "在线状态由服务端维护，不支持手动修改");
        // 频道级 RBAC（4.2）：仅群主 / 管理员可设置他人细粒度权限（谁可触发智能体 / 谁可审批）
        if (fields.Contains("permissions", StringComparer.Ordinal) && !CanManage(req.OperatorId, group))
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅群主或管理员可修改成员的细粒度权限");

        var updated = new Dictionary<string, JsonElement>();
        foreach (var field in fields)
        {
            if (!req.MemberInfo.TryGetValue(field, out var je)) continue;
            switch (field)
            {
                case "role" when je.ValueKind == JsonValueKind.String
                                && Enum.TryParse<GroupRole>(je.GetString(), true, out var role)
                                && role != GroupRole.Owner:
                    member.Role = role;
                    updated["role"] = je;
                    break;
                case "nickname" when je.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(je.GetString()):
                    member.Nickname = je.GetString()!;
                    updated["nickname"] = je;
                    break;
                case "avatar":
                    member.Avatar = je.ValueKind == JsonValueKind.String ? je.GetString() : null;
                    updated["avatar"] = je;
                    break;
                case "permissions" when CanManage(req.OperatorId, group)
                                          && TryParsePermissions(je, out var perms):
                    member.RbacPermissions = perms; // rest of settings 由 RbacPermissions setter 写入 Extra["rbac"]
                    _store.UpdateMember(group.GroupId, member);
                    updated["permissions"] = je;
                    break;
                case "onlineStatus" when je.ValueKind == JsonValueKind.String
                                        && Enum.TryParse<OnlineStatus>(je.GetString(), true, out var status):
                    member.OnlineStatus = status;
                    updated["onlineStatus"] = je;
                    break;
            }
        }

        if (updated.Count > 0)
        {
            _store.UpdateMember(group.GroupId, member); // 角色 / 昵称 / 头像为原地修改，落库（数据库模式）
            _changes?.Notify(); // 显式通知持久化
            await FanOutAsync(group.GroupId, new GroupMemberUpdatedEvent
            {
                GroupId = group.GroupId,
                MemberId = req.MemberId,
                UpdateFields = fields.Where(updated.ContainsKey).ToList(),
                MemberInfo = updated,
                OperatorId = req.OperatorId,
                Timestamp = NowMs,
            }, ct: ct);
        }
        return member;
    }

    /// <summary>
    /// 群主转让（RBAC 群级）：仅当前群主可转让；目标须为群内非群主成员。
    /// 转让后：目标成员成为 Owner，原群主降级为群管理员（Admin）保留管理权；群 OwnerId 与成员角色同步更新并广播。
    /// </summary>
    public async Task<Group> TransferOwnershipAsync(string groupId, string operatorId, string newOwnerId, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(groupId);
        if (group.OwnerId != operatorId)
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅群主可转让群主权限");
        if (newOwnerId == operatorId)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "不能把群主转让给自己");
        var target = _store.GetMember(group.GroupId, newOwnerId)
            ?? throw new AguiProtocolException(ErrorCodes.GroupMemberNotExist, "目标成员不在群组内");
        if (target.MemberType == MemberType.Agent)
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "不能把群主转让给智能体");

        group.OwnerId = newOwnerId; // 群主字段指向新群主
        target.Role = GroupRole.Owner;
        _store.UpdateMember(group.GroupId, target);
        // 原群主降为管理员（保留管理权，避免转让后无人能管群），其余成员不受影响
        GroupMember? oldOwner = null;
        if (operatorId != newOwnerId)
        {
            oldOwner = _store.GetMember(group.GroupId, operatorId);
            if (oldOwner is not null)
            {
                oldOwner.Role = GroupRole.Admin;
                _store.UpdateMember(group.GroupId, oldOwner);
            }
        }
        _store.UpdateGroup(group);
        _changes?.Notify();

        // 广播新群主与新群管理员（原群主）的角色变更
        var ts = NowMs;
        foreach (var role in new (string Mid, GroupRole Role)[] { (newOwnerId, GroupRole.Owner), (operatorId, GroupRole.Admin) })
        {
            if (role.Mid == operatorId && oldOwner is null) continue; // 原群主非群成员（异常态）才跳；正常恒为成员
            var je = System.Text.Json.JsonSerializer.SerializeToElement(role.Role.ToString());
            await FanOutAsync(group.GroupId, new GroupMemberUpdatedEvent
            {
                GroupId = group.GroupId,
                MemberId = role.Mid,
                UpdateFields = ["role"],
                MemberInfo = new Dictionary<string, System.Text.Json.JsonElement> { ["role"] = je },
                OperatorId = operatorId,
                Timestamp = ts,
            }, onlyTo: new HashSet<string> { role.Mid }, ct: ct);
        }
        _logger.LogInformation("群主转让：group={Group} {From} → {To}", groupId, operatorId, newOwnerId);
        return group;
    }

    // ================= 群消息（协议 4.4 / 5.1） =================

    /// <summary>
    /// 发送群消息：写入历史并以 TEXT_MESSAGE_START / CONTENT / END 三元组按可见性扇出，
    /// 然后按协议 §6 评估智能体触发规则（调用预留的 IAgentGateway）。
    /// </summary>
    public async Task<GroupMessage> SendMessageAsync(GroupMessageSendRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        var isCustomer = group.IsSupportCircle && !_store.IsMember(group.GroupId, req.UserId!) && IsSupportCustomer(group.GroupId, req.UserId!);
        if (!_store.IsMember(group.GroupId, req.UserId!) && !isCustomer) // 身份由 WS/HTTP 上层解析覆盖
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "发送者不是群成员");
        var attachments = req.Attachments ?? [];
        if (string.IsNullOrWhiteSpace(req.Content) && attachments.Count == 0)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "消息内容不能为空（可仅发送附件）");
        if (req.Content is not null && req.Content.Length > _options.MaxMessageChars)
            throw new AguiProtocolException(ErrorCodes.BadRequest, $"消息内容超过长度上限（{_options.MaxMessageChars} 字符）");
        // 数组字段数量上限：@提及 / 定向可见成员 / 附件过多会放大扇出与内存占用（防恶意构造超大列表）
        if ((req.Mentions?.Count ?? 0) > MaxMentionsPerMessage)
            throw new AguiProtocolException(ErrorCodes.BadRequest, $"@ 提及成员过多（上限 {MaxMentionsPerMessage}）");
        if ((req.VisibleMemberIds?.Count ?? 0) > _options.MaxGroupMembers)
            throw new AguiProtocolException(ErrorCodes.BadRequest, $"定向可见成员过多（上限 {_options.MaxGroupMembers}）");
        if (attachments.Count > MaxAttachmentsPerMessage)
            throw new AguiProtocolException(ErrorCodes.BadRequest, $"附件数量超过上限（{MaxAttachmentsPerMessage} 个）");

        // 发送者：客服为群成员（取成员行）；非成员顾客（客服知聚参与者）无成员行，合成发送者身份（Role=Normal 视为顾客）
        var senderMembership = _store.GetMember(group.GroupId, req.UserId!);
        var senderId = req.UserId!;
        var senderType = isCustomer ? MemberType.User : senderMembership!.MemberType;
        var senderNickname = senderMembership?.Nickname ?? _users.GetUserById(senderId)?.Nickname ?? _users.GetUserById(senderId)?.Username ?? ResolveMemberType(senderId) switch { MemberType.Agent => "智能体", _ => senderId };
        var topicId = string.IsNullOrWhiteSpace(req.TopicId) ? "main" : req.TopicId!;
        if (topicId != "main" && _store.GetTopic(group.GroupId, topicId) is null)
            throw new AguiProtocolException(ErrorCodes.GroupNotFound, "话题不存在");
        var msg = new GroupMessage
        {
            MessageId = "msg_" + IdGenerator.NewId(),
            GroupId = group.GroupId,
            TopicId = topicId,
            ThreadId = req.ThreadId ?? "thread_" + group.GroupId,
            SenderId = senderId,
            SenderType = senderType,
            SenderNickname = senderNickname,
            ReplyToMessageId = req.ReplyToMessageId,
            Mentions = req.Mentions ?? [],
            MentionAll = req.MentionAll,
            Visibility = req.Visibility ?? MessageVisibility.All,
            VisibleMemberIds = req.VisibleMemberIds ?? [],
            Attachments = attachments,
            BridgeClient = string.IsNullOrWhiteSpace(req.BridgeClient) ? null : req.BridgeClient.Trim(),
            Content = req.Content ?? "", // 协议允许纯附件消息（正文可空），以空串落库满足非空 Content
            Timestamp = NowMs,
        };

        // 客服知聚会话隔离：非客服成员只能看到自己的会话（与客服），客服可见全部。
        // 此处由服务端强制施加，避免前端越权把消息标成 All 泄露到其他顾客。
        ApplySupportCircleScoping(group, senderId, isStaff: !isCustomer, msg);

        if (msg.ReplyToMessageId is not null && _store.GetMessage(group.GroupId, msg.ReplyToMessageId) is null)
            throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "引用的目标消息不存在或已撤回");

        _store.AddMessage(msg);
        RememberMessage(msg); // 语义记忆：消息落库后异步向量化写入（不阻塞发送）

        var recipients = ResolveRecipients(msg.GroupId, msg.Visibility, msg.Mentions, msg.MentionAll, msg.VisibleMemberIds, msg.SenderId);
        var now = NowMs;

        // 发送者回显走成员直连（协议 2.3：发送者恒收到自己的消息，不依赖订阅状态），
        // 避免连接失去订阅（如断线重连后未恢复）时连自己发的消息都看不到。
        recipients.Remove(msg.SenderId);

        var startEvt = new TextMessageStartEvent
        {
            MessageId = msg.MessageId,
            Role = "user",
            ThreadId = msg.ThreadId,
            GroupId = msg.GroupId,
            TopicId = msg.TopicId,
            SenderId = msg.SenderId,
            SenderType = msg.SenderType,
            SenderNickname = msg.SenderNickname,
            ReplyToMessageId = msg.ReplyToMessageId,
            Mentions = msg.Mentions,
            MentionAll = msg.MentionAll,
            Visibility = msg.Visibility,
            VisibleMemberIds = msg.VisibleMemberIds,
            Attachments = msg.Attachments,
            Timestamp = msg.Timestamp,
        };
        var contentEvt = new TextMessageContentEvent { MessageId = msg.MessageId, Delta = msg.Content };
        var endEvt = new TextMessageEndEvent { MessageId = msg.MessageId, GroupId = msg.GroupId, Timestamp = now };

        await FanOutAsync(group.GroupId, startEvt, recipients, ct);
        await FanOutAsync(group.GroupId, contentEvt, recipients, ct);
        await FanOutAsync(group.GroupId, endEvt, recipients, ct);
        await EchoToSenderAsync(msg.SenderId, startEvt, contentEvt, endEvt, ct);

        TriggerAgents(msg);
        return msg;
    }

    public async Task RecallMessageAsync(GroupMessageRecallRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        var msg = _store.GetMessage(group.GroupId, req.MessageId)
            ?? throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在或已撤回");
        if (msg.Recalled)
            throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息已撤回");
        // 操作者必须是当前参与者（客服成员或客服知聚的顾客参与者）；管理员不受参与身份限制
        if (!CanManage(req.OperatorId!, group)
            && (msg.SenderId != req.OperatorId || !CanParticipate(group.GroupId, req.OperatorId!)))
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅发送者本人（且仍在群内）或管理员可撤回消息");
        // 撤回时限：仅允许撤回发送 3 分钟内的消息（服务端强校验，前端按钮同步按时间隐藏）
        if (NowMs - msg.Timestamp > RecallWindowMs)
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "消息发送已超过 3 分钟，无法撤回");

        _store.RecallMessage(group.GroupId, req.MessageId);
        _memory?.Forget(group.GroupId, req.MessageId); // 撤回同步清理语义记忆
        await FanOutAsync(group.GroupId, new GroupMessageRecalledEvent
        {
            GroupId = group.GroupId,
            MessageId = req.MessageId,
            OperatorId = req.OperatorId!,
            Timestamp = NowMs,
        }, ct: ct);
    }

    /// <summary>
    /// 重新回答：仅允许对「该话题最后一条消息」且为智能体消息执行。
    /// 语义：先撤回旧回答（内容隐藏 + 清除记忆），再用其触发消息重新调用同一智能体（显式提及，必答）。
    /// 权限：触发该回答的用户本人或群主 / 管理员。
    /// </summary>
    public async Task RegenerateMessageAsync(GroupMessageRegenerateRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        var msg = _store.GetMessage(group.GroupId, req.MessageId)
            ?? throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在或已撤回");
        if (msg.Recalled)
            throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息已撤回");
        if (msg.SenderType != MemberType.Agent)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "仅智能体消息可重新回答");

        // 必须是该话题最后一条消息：重答后新回答接在原触发消息之后，避免历史顺序错乱
        var topicId = req.TopicId ?? msg.TopicId ?? "main";
        var recent = _store.RecentMessages(group.GroupId, 1, topicId);
        if (recent.Count == 0 || recent[^1].MessageId != msg.MessageId)
            throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "仅该话题最后一条消息可重新回答");

        // 触发消息：引用回复优先，否则向前找最近一条用户消息（触发者据此判定权限）
        var triggerMsg = FindTriggerMessage(group.GroupId, msg);
        if (triggerMsg is null)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "找不到触发该回答的用户消息");
        if (triggerMsg.SenderId != req.OperatorId && !CanManage(req.OperatorId!, group))
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅触发该回答的用户或管理员可重新回答");

        // 1) 撤回旧回答（更新历史记录：内容隐藏 + 清除语义记忆，避免新回答与旧回答并存）
        _store.RecallMessage(group.GroupId, req.MessageId);
        _memory?.Forget(group.GroupId, req.MessageId);
        await FanOutAsync(group.GroupId, new GroupMessageRecalledEvent
        {
            GroupId = group.GroupId,
            MessageId = req.MessageId,
            OperatorId = req.OperatorId!,
            Timestamp = NowMs,
        }, ct: ct);

        // 2) 用触发消息重新调用同一智能体（显式提及语义：必答，不走语境沉默决策）
        var reg = _agents.ForGroupAgent(group.GroupId, msg.SenderId)
            ?? throw new AguiProtocolException(ErrorCodes.BadRequest, "智能体未注册或已移除，无法重新回答");
        _logger.LogInformation("消息 {MessageId} 重新回答：撤回旧回答并以 {TriggerId} 重新调用 {AgentId}（group={GroupId}，operator={OperatorId}）",
            msg.MessageId, triggerMsg.MessageId, reg.AgentId, group.GroupId, req.OperatorId);
        InvokeAgentFor(reg, triggerMsg, mentioned: true, summoned: false);
    }

    /// <summary>找回智能体消息的触发消息：引用回复优先，否则向前取最近一条用户消息。</summary>
    private GroupMessage? FindTriggerMessage(string groupId, GroupMessage agentMsg)
    {
        if (!string.IsNullOrEmpty(agentMsg.ReplyToMessageId))
        {
            var replied = _store.GetMessage(groupId, agentMsg.ReplyToMessageId);
            if (replied is not null && replied.SenderType == MemberType.User && !replied.Recalled)
                return replied;
        }
        var history = _store.MessagesBefore(groupId, agentMsg.MessageId, 50, agentMsg.TopicId ?? "main");
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].SenderType == MemberType.User && !history[i].Recalled)
                return history[i];
        return null;
    }

    /// <summary>停止智能体运行（「停止生成」）：校验群成员 + 触发者本人或群主 / 管理员，命中并取消返回 true。</summary>
    public Task<bool> StopAgentRunAsync(AgentStopRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        if (!_store.IsMember(group.GroupId, req.OperatorId!))
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅群成员可停止智能体运行");
        var isManager = CanManage(req.OperatorId!, group);
        return Task.FromResult(_agentGateway.StopRun(req.RunId, req.OperatorId!, group.GroupId, isManager));
    }

    public async Task BroadcastTypingAsync(GroupTypingRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        // 客服知聚允许顾客参与者（非成员）也发 typing；普通知聚仍要求成员身份。
        var member = _store.GetMember(group.GroupId, req.MemberId!)
            ?? (group.IsSupportCircle && IsSupportCustomer(group.GroupId, req.MemberId!)
                ? null
                : throw new AguiProtocolException(ErrorCodes.GroupMemberNotExist, "成员不在群组内"));

        // typing 节流：开始输入 1 秒内重复广播直接忽略（不扇出）；结束输入总是广播
        if (req.IsTyping)
        {
            var now = NowMs;
            if (_lastTypingAt.TryGetValue(req.MemberId!, out var last) && now - last < TypingThrottleMs)
                return;
            _lastTypingAt[req.MemberId!] = now;
        }
        else
        {
            _lastTypingAt.TryRemove(req.MemberId!, out _);
        }

        var isStaff = member is not null; // 群成员=客服；null=顾客参与者（客服知聚）
        var others = _store.ListMembers(group.GroupId)
            .Where(m => m.MemberId != req.MemberId!)
            .Select(m => m.MemberId)
            .ToHashSet();
        if (group.IsSupportCircle && isStaff)
        {
            // 客服 / 数字员工输入 → 除其他客服外，也让已进入的顾客参与者看到（向等待中的顾客暴露“客服正在输入”）。
            if (_supportCustomers.TryGetValue(group.GroupId, out var customers))
                foreach (var cid in customers.Keys)
                    others.Add(cid);
        }
        // 顾客参与者输入 → 收件人仅为客服（成员），顾客之间互相不可见（与消息隔离一致）。

        var evt = new GroupTypingEvent
        {
            GroupId = group.GroupId,
            MemberId = req.MemberId!,
            MemberType = member?.MemberType ?? MemberType.User,
            IsTyping = req.IsTyping,
            Timestamp = NowMs,
        };

        await FanOutAsync(group.GroupId, evt, others, ct);
    }

    public async Task BroadcastReadAsync(GroupReadRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        var member = _store.GetMember(group.GroupId, req.MemberId!)
            ?? throw new AguiProtocolException(ErrorCodes.GroupMemberNotExist, "成员不在群组内");
        var msg = _store.GetMessage(group.GroupId, req.ReadMessageId)
            ?? throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "已读回执的目标消息不存在");

        // 已读位点落库（按消息归属话题；供群列表 / 话题未读提示与活跃度排序）：
        // 只前进不回退——读一条旧消息（如滚动回看）不会把已读位点倒退、使未读数变大
        var topicId = msg.TopicId ?? "main";
        var current = _store.GetReadAt(member.MemberId, group.GroupId, topicId);
        if (msg.Timestamp > current)
            _store.SetReadAt(member.MemberId, group.GroupId, topicId, msg.Timestamp);

        var others = _store.ListMembers(group.GroupId)
            .Where(m => m.MemberId != member.MemberId)
            .Select(m => m.MemberId)
            .ToHashSet();

        await FanOutAsync(group.GroupId, new GroupMessageReadEvent
        {
            GroupId = group.GroupId,
            MemberId = member.MemberId,
            ReadMessageId = req.ReadMessageId,
            Timestamp = NowMs,
        }, others, ct);
    }

    /// <summary>
    /// 某成员在某群的未读信息（群列表活跃度排序 / 未读提示）：
    /// 最后消息时间、全部话题未读合计、按话题未读数（key "main" 为主话题）。
    /// </summary>
    public (long? LastMessageAt, int UnreadCount, Dictionary<string, int> ByTopic) UnreadInfo(string groupId, string memberId)
    {
        var topics = _store.ListTopics(groupId).Select(t => t.TopicId).Append("main").Distinct().ToList();
        var byTopic = new Dictionary<string, int>();
        var total = 0;
        foreach (var topicId in topics)
        {
            var readAt = _store.GetReadAt(memberId, groupId, topicId);
            var count = _store.CountUnread(groupId, topicId, readAt);
            byTopic[topicId] = count;
            total += count;
        }
        return (_store.LastMessageAt(groupId), total, byTopic);
    }

    /// <summary>
    /// 人机交互决策（协议 4.5）：触发者对 AGENT_INTERACTION_REQUEST 作出批准 / 拒绝，恢复被中断的智能体运行。
    /// 决策者必须是指定交互请求的触发者（网关侧按 TargetMemberId 强校验）；群聊其他用户无权交互。
    /// 返回 false 表示交互请求不存在 / 已过期 / 决策者非触发者。
    /// </summary>
    public async Task<bool> ResolveAgentInteractionAsync(GroupInteractionResolveRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        // 客服知聚：普通顾客以「参与者」身份进入（不在成员表），但需能批准其触发客服执行技能（客服业务核心：
        // 顾客 @ 客服 → 客服请求执行 → 顾客确认）。这里放行客服知聚的顾客参与者；普通知聚仍仅限群成员决策。
        // 安全性不降：网关系在下方按 TargetMemberId（触发者）强校验，顾客只能批准自己触发的那次交互。
        var isSupportCustomer = group.IsSupportCircle && IsSupportCustomer(group.GroupId, req.MemberId!);
        if (!_store.IsMember(group.GroupId, req.MemberId!) && !isSupportCustomer) // 身份由 WS/HTTP 上层解析覆盖
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "决策者不是群成员");
        // 频道级 RBAC（4.2）：被群限制为不可审批（CanApprove=false）的成员无法作出任何交互决策；
        // 顾客参与者无成员行（不受 RBAC 限制），由下方网关的触发者校验兜底，故此处只校验真实成员。
        var member = _store.GetMember(group.GroupId, req.MemberId!);
        if (member is not null && !member.CanApproveInteractions)
            return false; // 与「非触发者 / 已过期」一致地拒绝
        var resolved = await _agentGateway.ResolveInteractionAsync(req.InterruptId, req.MemberId!, req.Approved, req.Input, req.Payload, ct, approveAll: req.ApproveAll, toolResult: req.ToolResult);
        if (resolved)
        {
            // 决策已生效：全群广播，其他成员的卡片同步更新为「已批准 / 已拒绝」（仅触发者可发起决策）
            await FanOutAsync(group.GroupId, new AgentInteractionResolvedEvent
            {
                GroupId = group.GroupId,
                InterruptId = req.InterruptId,
                MemberId = req.MemberId!,
                Approved = req.Approved,
                Input = req.Input,
                Payload = req.Payload,
                Timestamp = NowMs,
            }, ct: ct);
        }
        return resolved;
    }

    // ================= 订阅与快照（协议 4.6 / 4.7） =================

    public async Task SubscribeAsync(HubConnection connection, IReadOnlyList<string> groupIds, CancellationToken ct = default)
    {
        var success = new List<string>();
        var failed = new List<string>();

        foreach (var groupId in groupIds.Distinct(StringComparer.Ordinal))
        {
            if (TrySubscribeOne(connection, groupId)) success.Add(groupId);
            else failed.Add(groupId);
        }

        await connection.SendAsync(AguiJson.Serialize(new GroupSubscribeAckEvent
        {
            SuccessGroupIds = success,
            FailedGroupIds = failed,
            FailReason = failed.Count > 0 ? "无群组访问权限或群组不存在" : null,
            Timestamp = NowMs,
        }), ct);

        foreach (var groupId in success)
        {
            var snapshot = await BuildSnapshotAsync(groupId, connection.MemberId, ct);
            await connection.SendAsync(AguiJson.Serialize(snapshot), ct);
        }
    }

    public Task UnsubscribeAsync(HubConnection connection, IReadOnlyList<string> groupIds, CancellationToken ct = default)
    {
        foreach (var groupId in groupIds.Distinct(StringComparer.Ordinal))
            _connections.Unsubscribe(connection, groupId);
        return Task.CompletedTask;
    }

    public async Task<GroupStateSnapshotEvent> BuildSnapshotAsync(string groupId, string viewerId, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(groupId);
        // 历史快照按查看者可见性过滤：定向 / 私聊消息只对发送者与目标成员可见（与实时扇出 ResolveRecipients 规则一致）
        var messages = _store.RecentMessages(groupId, _options.SnapshotMessageCount)
            .Where(m => !m.Recalled && CanSeeMessageAware(m, viewerId))
            .Select(m => new SnapshotMessage
            {
                MessageId = m.MessageId,
                SenderId = m.SenderId,
                SenderType = m.SenderType.ToString(),
                SenderNickname = m.SenderNickname,
                Content = m.Content,
                TopicId = m.TopicId,
                ReplyToMessageId = m.ReplyToMessageId,
                Attachments = m.Attachments,
                Mentions = m.Mentions,
                MentionAll = m.MentionAll,
                Reasoning = m.Reasoning,
                AgentChain = m.AgentChain,
                PlanJson = m.PlanJson,
                Timestamp = m.Timestamp,
            })
            .ToList();

        // 智能体成员附带群内触发规则（前端按群覆盖角色默认触发方式时回显当前值）
        var registrations = _agents.ForGroup(groupId).ToDictionary(r => r.AgentId, StringComparer.Ordinal);
        var members = _store.ListMembers(groupId).Select(m =>
        {
            if (m.MemberType != MemberType.Agent || !registrations.TryGetValue(m.MemberId, out var reg))
                return m;
            return new GroupMember
            {
                MemberId = m.MemberId,
                MemberType = m.MemberType,
                Nickname = m.Nickname,
                Avatar = m.Avatar,
                Role = m.Role,
                OnlineStatus = m.OnlineStatus,
                JoinTime = m.JoinTime,
                TriggerMode = AguiJson.Element(reg.TriggerMode).GetString(),
                Keywords = reg.Keywords,
                IsTriggerOverridden = reg.IsOverridden,
                Extra = m.Extra,
            };
        }).ToList();

        return new GroupStateSnapshotEvent
        {
            GroupId = groupId,
            GroupInfo = group,
            Members = members,
            Topics = _store.ListTopics(groupId),
            LatestMessages = messages,
            Timestamp = NowMs,
        };
    }

    private bool TrySubscribeOne(HubConnection connection, string groupId)
    {
        var group = _store.GetGroup(groupId);
        if (group is null || _disbanded.ContainsKey(groupId)) return false;
        // 客服知聚允许非成员的顾客参与者订阅（各自只见自己的会话）；普通知聚仅成员可订阅
        if (!CanParticipate(groupId, connection.MemberId)) return false;
        return _connections.Subscribe(connection, groupId);
    }

    // ================= 群话题（Hub 扩展） =================

    /// <summary>群内新建话题：校验成员身份后创建并全群广播；SourceMessageId 非空时把该消息迁移为新话题起点。返回话题对象。</summary>
    public async Task<GroupTopic> CreateTopicAsync(GroupTopicCreateRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        if (!_store.IsMember(group.GroupId, req.OperatorId))
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "操作者不是群成员");
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new AguiProtocolException(ErrorCodes.BadRequest, "话题名称不能为空");
        var name = req.Name.Trim();
        if (name.Length > 30)
            throw new AguiProtocolException(ErrorCodes.BadRequest, "话题名称最长 30 字符");

        // 可选：以某条发言为起点 → 校验该消息存在且未撤回（撤回消息不能作为起点）
        GroupMessage? source = null;
        if (!string.IsNullOrWhiteSpace(req.SourceMessageId))
        {
            source = _store.GetMessage(group.GroupId, req.SourceMessageId!)
                ?? throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "引用的消息不存在");
            if (source.Recalled)
                throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "被撤回的消息不能作为话题起点");
        }

        var topic = new GroupTopic
        {
            TopicId = "topic_" + IdGenerator.NewId(),
            GroupId = group.GroupId,
            Name = name,
            CreatorId = req.OperatorId,
            CreatedAt = NowMs,
        };
        if (!_store.AddTopic(topic))
            throw new AguiProtocolException(ErrorCodes.BadRequest, "话题创建失败（ID 冲突）");

        // 迁移起点消息：TopicId 改为新话题（原话题移除，新话题作为讨论起点）
        if (source is not null)
        {
            lock (_pendingLock)
            {
                // 话题迁移为整行更新：与流式整行写回共用锁，防止并发读-改-写互相覆盖（锁内无 await，FanOut 保持在锁外）
                source.TopicId = topic.TopicId;
                _store.UpdateMessage(source); // 话题迁移为原地修改，落库（数据库模式）
            }
            _changes?.Notify();
            await FanOutAsync(group.GroupId, new GroupMessageTopicMovedEvent
            {
                GroupId = group.GroupId,
                MessageId = source.MessageId,
                TopicId = topic.TopicId,
                OperatorId = req.OperatorId,
                Timestamp = NowMs,
            }, ct: ct);
        }

        await FanOutAsync(group.GroupId, new GroupTopicCreatedEvent
        {
            GroupId = group.GroupId,
            Topic = topic,
            Timestamp = NowMs,
        }, ct: ct);
        _logger.LogInformation("群 {GroupId} 新建话题 {TopicId}（{Name}，创建者 {Creator}，起点消息 {Source}）",
            group.GroupId, topic.TopicId, topic.Name, topic.CreatorId, source?.MessageId ?? "-");
        return topic;
    }

    /// <summary>删除话题（仅群主 / 管理员或话题创建者）：话题下聊天记录与对应记忆一并删除，
    /// 删除话题记录并全群广播 GROUP_TOPIC_DELETED。主话题 main 不可删除。</summary>
    public async Task<bool> DeleteTopicAsync(GroupTopicDeleteRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        if (string.IsNullOrWhiteSpace(req.TopicId) || req.TopicId == "main")
            throw new AguiProtocolException(ErrorCodes.BadRequest, "主话题不可删除");
        var topic = _store.GetTopic(group.GroupId, req.TopicId)
            ?? throw new AguiProtocolException(ErrorCodes.GroupNotFound, "话题不存在");
        // 权限：群主 / 管理员可删任意话题；话题创建者也可删除自己创建的话题
        if (!CanManage(req.OperatorId, group) && topic.CreatorId != req.OperatorId)
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅群主、管理员或话题创建者可删除话题");

        // 1) 删除话题下聊天记录（物理删除），并同步清除对应语义记忆（与撤回一致）
        var messages = _store.AllMessages(group.GroupId).Where(m => m.TopicId == req.TopicId).ToList();
        var removed = _store.RemoveTopicMessages(group.GroupId, req.TopicId);
        if (_memory is not null)
        {
            foreach (var m in messages)
            {
                try { _memory.Forget(group.GroupId, m.MessageId); }
                catch (Exception ex) { _logger.LogDebug(ex, "删除话题消息记忆失败：{MessageId}", m.MessageId); }
            }
        }
        _changes?.Notify();

        // 2) 删除话题记录
        if (!_store.RemoveTopic(group.GroupId, req.TopicId))
            return false;

        // 3) 全群广播
        await FanOutAsync(group.GroupId, new GroupTopicDeletedEvent
        {
            GroupId = group.GroupId,
            TopicId = req.TopicId,
            OperatorId = req.OperatorId,
            Timestamp = NowMs,
        }, ct: ct);
        _logger.LogInformation("群 {GroupId} 删除话题 {TopicId}（{Name}，操作者 {Operator}，清除消息 {Removed} 条与对应记忆）",
            group.GroupId, req.TopicId, topic.Name, req.OperatorId, removed);
        return true;
    }

    /// <summary>清空话题聊天记录（含主话题 main）：仅群主 / 管理员。话题本身保留，
    /// 该话题下的消息物理删除并同步清除对应语义记忆，全群广播 GROUP_TOPIC_CLEARED。</summary>
    public async Task<int> ClearTopicMessagesAsync(GroupTopicClearRequest req, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(req.GroupId);
        if (!CanManage(req.OperatorId, group))
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "仅群主或管理员可清空话题聊天记录");
        var topicId = string.IsNullOrWhiteSpace(req.TopicId) ? "main" : req.TopicId;
        if (topicId != "main" && _store.GetTopic(group.GroupId, topicId) is null)
            throw new AguiProtocolException(ErrorCodes.GroupNotFound, "话题不存在");

        // 物理删除消息 + 同步清除对应语义记忆（与撤回 / 删除话题一致）
        var messages = _store.AllMessages(group.GroupId).Where(m => m.TopicId == topicId).ToList();
        var removed = _store.RemoveTopicMessages(group.GroupId, topicId);
        if (_memory is not null)
        {
            foreach (var m in messages)
            {
                try { _memory.Forget(group.GroupId, m.MessageId); }
                catch (Exception ex) { _logger.LogDebug(ex, "清空话题消息记忆失败：{MessageId}", m.MessageId); }
            }
        }
        _changes?.Notify();

        await FanOutAsync(group.GroupId, new GroupTopicClearedEvent
        {
            GroupId = group.GroupId,
            TopicId = topicId,
            OperatorId = req.OperatorId,
            RemovedCount = removed,
            Timestamp = NowMs,
        }, ct: ct);
        _logger.LogInformation("群 {GroupId} 清空话题 {TopicId} 聊天记录（操作者 {Operator}，清除消息 {Removed} 条与对应记忆）",
            group.GroupId, topicId, req.OperatorId, removed);
        return removed;
    }

    // ================= 在线状态（连接生命周期联动） =================

    public async Task OnMemberConnectedAsync(string memberId, CancellationToken ct = default)
    {
        foreach (var group in _store.GroupsOf(memberId))
        {
            var member = _store.GetMember(group.GroupId, memberId);
            if (member is null || member.OnlineStatus == OnlineStatus.Online) continue;
            _store.UpdateMemberStatus(group.GroupId, memberId, OnlineStatus.Online);
            await FanOutAsync(group.GroupId, new GroupMemberUpdatedEvent
            {
                GroupId = group.GroupId,
                MemberId = memberId,
                UpdateFields = ["onlineStatus"],
                MemberInfo = new() { ["onlineStatus"] = AguiJson.Element("online") },
                OperatorId = memberId,
                Timestamp = NowMs,
            }, ct: ct);
        }
    }

    public async Task OnMemberDisconnectedAsync(string memberId, CancellationToken ct = default)
    {
        if (_connections.MemberConnectionCount(memberId) > 0) return;
        foreach (var group in _store.GroupsOf(memberId))
        {
            var member = _store.GetMember(group.GroupId, memberId);
            if (member is null || member.OnlineStatus == OnlineStatus.Offline) continue;
            _store.UpdateMemberStatus(group.GroupId, memberId, OnlineStatus.Offline);
            await FanOutAsync(group.GroupId, new GroupMemberUpdatedEvent
            {
                GroupId = group.GroupId,
                MemberId = memberId,
                UpdateFields = ["onlineStatus"],
                MemberInfo = new() { ["onlineStatus"] = AguiJson.Element("offline") },
                OperatorId = memberId,
                Timestamp = NowMs,
            }, ct: ct);
        }
    }

    // ================= 智能体应答消息（协议 4.4，IAgentGateway 回灌入口） =================

    /// <summary>
    /// 开启一条智能体应答消息：校验智能体成员身份、落库并广播 TEXT_MESSAGE_START（role=assistant）。
    /// 返回的 messageId 用于后续 AppendAgentContentAsync / EndAgentMessageAsync。
    /// </summary>
    public async Task<GroupMessage> PublishAgentMessageStartAsync(AgentMessageStartInput input, CancellationToken ct = default)
    {
        var group = GetGroupOrThrow(input.GroupId);
        var sender = _store.GetMember(group.GroupId, input.AgentId)
            ?? throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "智能体不是群成员");
        if (sender.MemberType != MemberType.Agent)
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "发送者不是智能体成员");

        var msg = new GroupMessage
        {
            MessageId = "msg_" + IdGenerator.NewId(),
            GroupId = group.GroupId,
            TopicId = input.TopicId,
            ThreadId = "thread_" + group.GroupId,
            SenderId = sender.MemberId,
            SenderType = MemberType.Agent,
            SenderNickname = sender.Nickname,
            ReplyToMessageId = input.ReplyToMessageId,
            Mentions = input.Mentions ?? [],
            MentionAll = input.MentionAll,
            Visibility = input.Visibility,
            VisibleMemberIds = input.VisibleMemberIds ?? [],
            Content = "",
            Timestamp = NowMs,
        };
        _store.AddMessage(msg);

        var recipients = ResolveRecipients(msg.GroupId, msg.Visibility, msg.Mentions, msg.MentionAll, msg.VisibleMemberIds, msg.SenderId);
        var streamNow = NowMs;
        _agentStreams[msg.MessageId] = new AgentStreamState(group.GroupId, recipients, streamNow, streamNow);

        await FanOutAsync(group.GroupId, new TextMessageStartEvent
        {
            MessageId = msg.MessageId,
            Role = "assistant",
            ThreadId = msg.ThreadId,
            RunId = input.RunId,
            GroupId = msg.GroupId,
            TopicId = msg.TopicId,
            SenderId = msg.SenderId,
            SenderType = MemberType.Agent,
            SenderNickname = msg.SenderNickname,
            ReplyToMessageId = msg.ReplyToMessageId,
            Mentions = msg.Mentions,
            MentionAll = msg.MentionAll,
            Visibility = msg.Visibility,
            VisibleMemberIds = msg.VisibleMemberIds,
            Timestamp = msg.Timestamp,
        }, recipients, ct);
        return msg;
    }

    /// <summary>追加智能体应答增量（写入存储并广播 TEXT_MESSAGE_CONTENT）。
    /// 数据库写入按 <see cref="GroupChatOptions.MessageWriteDebounceMs"/> 防抖：窗口内只合并内存对象，
    /// 超过窗口或消息结束时才落库，避免流式逐 token 写库。</summary>
    public async Task AppendAgentContentAsync(string groupId, string messageId, string delta, CancellationToken ct = default)
    {
        if (!_agentStreams.TryGetValue(messageId, out var state) || state.GroupId != groupId)
            throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在或未开启流式灌入");
        if (string.IsNullOrEmpty(delta)) return;

        lock (_pendingLock)
        {
            // 写 _pendingContent 前在锁内二次确认流式状态仍存活：End 可能已并发移除该消息，
            // 防止 End 后迟到的追加增量重新激活防抖缓冲（内容残留不落库 / 已结束消息被再次写库）
            if (!_agentStreams.TryGetValue(messageId, out var live) || live.GroupId != groupId)
                throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在或未开启流式灌入");
            _agentStreams[messageId] = live with { LastActivityMs = NowMs }; // 记录活跃时间（孤儿流兜底判定用）
            // 以内存中的累计内容为准拼接增量（防抖窗口内数据库读取可能落后于已累计的增量，不能以库内内容为基准）
            var pending = _pendingContent.TryGetValue(messageId, out var p) ? p : null;
            var baseContent = pending?.Content
                ?? _store.GetMessage(groupId, messageId)?.Content
                ?? throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在");
            var accumulated = baseContent + delta;

            // 防抖：到防抖窗口（或首条）即把当前累计内容写库；窗口内仅更新内存中的待写快照
            var now = NowMs;
            if (pending is null || now - pending.LastWriteMs >= _options.MessageWriteDebounceMs)
            {
                _pendingContent[messageId] = new PendingMessageContent(groupId, accumulated, now);
                FlushPendingContent(messageId); // 与写库读-改-写共用同一把锁（同线程可重入，锁内无 await）
            }
            else
            {
                _pendingContent[messageId] = pending with { Content = accumulated };
            }
        }

        _changes?.Notify(); // 显式通知持久化（内存模式 JSON 快照在防抖 / 结束时读到的即最新内容）
        await FanOutAsync(groupId, new TextMessageContentEvent { MessageId = messageId, GroupId = groupId, Delta = delta }, state.Recipients, ct);
    }

    /// <summary>追加智能体思考过程增量（AG-UI 思考模式，独立于正文存储与展示）。
    /// 思考内容一般分块到达、总量有限，直接写库并广播 TEXT_MESSAGE_REASONING（与正文防抖通道分离）；
    /// 读-改-写与正文通道共用 <see cref="_pendingLock"/>（锁内无 await，同线程可重入），避免并发丢增量。</summary>
    public async Task AppendAgentReasoningAsync(string groupId, string messageId, string delta, CancellationToken ct = default)
    {
        if (!_agentStreams.TryGetValue(messageId, out var state) || state.GroupId != groupId)
            throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在或未开启流式灌入");
        if (string.IsNullOrEmpty(delta)) return;
        lock (_pendingLock)
        {
            // 记录活跃时间（孤儿流兜底判定用）；仅当流仍存活时更新，防 End 后迟到操作重新激活
            if (_agentStreams.TryGetValue(messageId, out var live) && live.GroupId == groupId)
                _agentStreams[messageId] = live with { LastActivityMs = NowMs };
            var msg = _store.GetMessage(groupId, messageId)
                ?? throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在");
            msg.Reasoning = (msg.Reasoning ?? "") + delta;
            _store.UpdateMessage(msg);
        }
        _changes?.Notify();
        await FanOutAsync(groupId, new TextMessageReasoningEvent { MessageId = messageId, GroupId = groupId, Delta = delta }, state.Recipients, ct);
    }

    /// <summary>为流式中的智能体消息附加技能调用链（链路可视化，于运行结束时写库一次）。
    /// 链为 JSON 文本；消息必须仍在流式开启状态（_agentStreams），与正文通道共用锁。</summary>
    public async Task AttachAgentChainAsync(string groupId, string messageId, string chainJson, CancellationToken ct = default)
    {
        if (!_agentStreams.TryGetValue(messageId, out var state) || state.GroupId != groupId)
            throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在或未开启流式灌入");
        lock (_pendingLock)
        {
            if (_agentStreams.TryGetValue(messageId, out var live) && live.GroupId == groupId)
                _agentStreams[messageId] = live with { LastActivityMs = NowMs };
            var msg = _store.GetMessage(groupId, messageId)
                ?? throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在");
            msg.AgentChain = chainJson;
            _store.UpdateMessage(msg);
        }
        _changes?.Notify();
        await Task.CompletedTask;
    }

    /// <summary>为流式中的智能体消息追加附件（AG-UI 桥接回灌）：按 URL 去重合并写入消息并广播 TEXT_MESSAGE_ATTACHMENTS。
    /// 附件为外部 URL（ext_ 前缀）或本地上传附件；消息必须仍在流式开启状态（_agentStreams）。</summary>
    public async Task AppendAgentAttachmentsAsync(string groupId, string messageId, IReadOnlyList<AttachmentInfo> attachments, CancellationToken ct = default)
    {
        if (!_agentStreams.TryGetValue(messageId, out var state) || state.GroupId != groupId)
            throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在或未开启流式灌入");
        if (attachments.Count == 0) return;
        List<AttachmentInfo> added;
        lock (_pendingLock)
        {
            // 读消息 → 追加附件 → 整行写回：与流式整行写回共用锁，防止并发读-改-写互相覆盖（锁内无 await，FanOut 在锁外）
            var msg = _store.GetMessage(groupId, messageId)
                ?? throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在");
            var known = msg.Attachments.Select(a => a.Url).ToHashSet(StringComparer.Ordinal);
            added = attachments.Where(a => !string.IsNullOrEmpty(a.Url) && known.Add(a.Url)).ToList();
            if (added.Count == 0) return;
            msg.Attachments = msg.Attachments.Concat(added).ToList();
            _store.UpdateMessage(msg);
            // 记录活跃时间（孤儿流兜底判定用）；仅当流仍存活时更新，防 End 后迟到操作重新激活
            if (_agentStreams.TryGetValue(messageId, out var live) && live.GroupId == groupId)
                _agentStreams[messageId] = live with { LastActivityMs = NowMs };
        }
        _changes?.Notify();
        await FanOutAsync(groupId, new TextMessageAttachmentsEvent
        {
            MessageId = messageId,
            GroupId = groupId,
            Attachments = added,
            Timestamp = NowMs,
        }, state.Recipients, ct);
    }

    /// <summary>广播工作型智能体的任务计划（TEXT_MESSAGE_PLAN）：消息结束时回附其工作区 PLAN.md 的结构化步骤（任务规划可视化）。
    /// 按消息流可见范围扇出；消息不存在 / 未在流式状态下则静默跳过（计划可视化是增强，不应阻断主流程）。</summary>
    public Task BroadcastMessagePlanAsync(string groupId, string messageId, string? title, IReadOnlyList<PlanStepInfo> steps, CancellationToken ct = default)
    {
        if (steps is null || steps.Count == 0) return Task.CompletedTask;
        if (!_agentStreams.TryGetValue(messageId, out var state) || state.GroupId != groupId)
            return Task.CompletedTask;
        // 计划随消息落库（刷新 / 重开后历史消息仍可回显计划卡）。持久化是增强，失败不阻断主流程。
        try
        {
            var planJson = AguiJson.Serialize(new { Title = title, Steps = steps });
            var msg = _store.GetMessage(groupId, messageId);
            if (msg is not null)
            {
                msg.PlanJson = planJson;
                _store.UpdateMessage(msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "计划落库失败（已忽略，仅实时广播）：message={MessageId}", messageId);
        }
        _changes?.Notify();
        return FanOutAsync(groupId, new TextMessagePlanEvent
        {
            MessageId = messageId,
            GroupId = groupId,
            Title = title,
            Steps = steps,
            Timestamp = NowMs,
        }, state.Recipients, ct);
    }

    /// <summary>重置智能体应答内容：清空已回灌的中间内容（防抖缓冲 + 数据库）并广播 TEXT_MESSAGE_RESET。
    /// 用于人机交互中断：先清空半截回复，等用户反馈、运行继续结束后，最终结果再一次性回灌到同一消息。</summary>
    public Task ResetAgentContentAsync(string groupId, string messageId, CancellationToken ct = default)
    {
        if (!_agentStreams.TryGetValue(messageId, out var state) || state.GroupId != groupId)
            throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在或未开启流式灌入");
        lock (_pendingLock)
        {
            // 清空防抖缓冲 + 读消息 → 置空 → 整行写回：与流式整行写回共用锁，防并发读写交错（锁内无 await，FanOut 在锁外）
            _pendingContent.TryRemove(messageId, out _);
            var msg = _store.GetMessage(groupId, messageId);
            if (msg is not null)
            {
                msg.Content = "";
                msg.Reasoning = null; // 思考过程同样重置（中断恢复后重新思考 / 直接产出正文）
                _store.UpdateMessage(msg);
            }
            // 记录活跃时间（孤儿流兜底判定用）；仅当流仍存活时更新，防 End 后迟到操作重新激活
            if (_agentStreams.TryGetValue(messageId, out var live) && live.GroupId == groupId)
                _agentStreams[messageId] = live with { LastActivityMs = NowMs };
        }
        _changes?.Notify(); // 通知持久化：内容已清空，快照 / 数据库同步
        return FanOutAsync(groupId, new TextMessageResetEvent
        {
            MessageId = messageId,
            GroupId = groupId,
            Timestamp = NowMs,
        }, state.Recipients, ct);
    }

    /// <summary>结束智能体应答（先把防抖窗口内的内容落库，再广播 TEXT_MESSAGE_END 并清理流式状态）。
    /// <b>二次剥壳</b>：若组装好的整段正文其实就是内部协调 JSON（{"needsMore":…,…,"answer":…}），
    /// 在这里统一把它替换成面向用户的 answer 后才落库 / 收尾广播（即便 JSON 早些被分块发出，也能在完结前纠正）。</summary>
    public async Task EndAgentMessageAsync(string groupId, string messageId, CancellationToken ct = default)
    {
        if (!_agentStreams.TryRemove(messageId, out var state) || state.GroupId != groupId)
            throw new AguiProtocolException(ErrorCodes.GroupMessageNotFound, "消息不存在或未开启流式灌入");
        FlushPendingContent(messageId); // 消息结束：防抖窗口内的内容立即写库（数据库模式）
        _pendingContent.TryRemove(messageId, out _);
        var msg = _store.GetMessage(groupId, messageId);
        var raw = msg?.Content ?? "";
        var cleaned = CleanCoordinationOut(raw);
        if (msg is not null && !string.Equals(cleaned, raw, StringComparison.Ordinal))
        {
            // 整段正文是内部协调 JSON：只用 answer，改库 + 广播纠正后的全文，避免把决策 JSON 留给用户
            msg.Content = cleaned;
            _store.UpdateMessage(msg);
            _changes?.Notify();
            await FanOutAsync(groupId, new TextMessageResetEvent { MessageId = messageId, GroupId = groupId, Timestamp = NowMs }, state.Recipients, ct);
            await FanOutAsync(groupId, new TextMessageContentEvent { MessageId = messageId, GroupId = groupId, Delta = cleaned }, state.Recipients, ct);
        }
        if (_memory is not null && msg is not null)
        {
            // 智能体消息已完成（内容完整）：写入语义记忆（异步向量化，不阻塞广播）
            RememberMessage(msg);
        }
        await FanOutAsync(groupId, new TextMessageEndEvent
        {
            MessageId = messageId,
            GroupId = groupId,
            Reasoning = msg?.Reasoning, // 思考内容完整快照（供前端回放）
            AgentChain = msg?.AgentChain, // 技能调用链（链路可视化）
            PlanJson = msg?.PlanJson, // 任务计划（刷新后回显）
            Timestamp = NowMs,
        }, state.Recipients, ct);
    }

    /// <summary>二次剥壳：整段文本若本身就是内部协调 JSON（含布尔 needsMore + 字符串 answer），仅返回 answer；否则原样。</summary>
    private static string CleanCoordinationOut(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return text;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text.Substring(start, end - start + 1));
            var r = doc.RootElement;
            if (r.ValueKind != System.Text.Json.JsonValueKind.Object) return text;
            var hasFlag = r.TryGetProperty("needsMore", out var flag)
                && (flag.ValueKind == System.Text.Json.JsonValueKind.True || flag.ValueKind == System.Text.Json.JsonValueKind.False);
            if (hasFlag && r.TryGetProperty("answer", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var ans = a.GetString();
                if (!string.IsNullOrWhiteSpace(ans)) return ans.Trim();
            }
        }
        catch { /* 非常规 JSON 原样返回 */ }
        return text;
    }

    /// <summary>把防抖中的流式内容写库：以库内最新消息为准（保留最新话题等字段），仅替换文本。</summary>
    private void FlushPendingContent(string messageId)
    {
        lock (_pendingLock)
        {
            if (!_pendingContent.TryGetValue(messageId, out var pending)) return;
            var msg = _store.GetMessage(pending.GroupId, messageId);
            if (msg is null) return;
            msg.Content = pending.Content;
            _store.UpdateMessage(msg);
        }
    }

    /// <summary>把防抖窗口内的全部流式内容强制落库（宿主关闭前的兜底：防最后一段增量未写库）。</summary>
    public void FlushAllPendingContent()
    {
        lock (_pendingLock)
        {
            foreach (var messageId in _pendingContent.Keys.ToList())
                FlushPendingContent(messageId); // 与写库共用同一把锁（同线程可重入）
        }
    }

    /// <summary>
    /// 孤儿流兜底：周期扫描「创建超过 <see cref="OrphanStreamTimeoutMs"/> 且最近 <see cref="OrphanStreamIdleMs"/>
    /// 无活跃交互」的流式消息，强制 <see cref="EndAgentMessageAsync"/> 收尾（End 丢失 / 智能体进程崩溃时的兜底，
    /// 防流式状态与防抖缓冲泄漏）。定时器回调内全量 try/catch：线程池回调异常会终止进程，必须吞掉并记日志。
    /// </summary>
    private void CleanupOrphanStreams()
    {
        try { PurgeExpiredSupportCustomers(); } catch { /* 参与回收失败不影响孤儿清理 */ }
        var now = NowMs;
        foreach (var kv in _agentStreams
            .Where(kv => now - kv.Value.CreatedAt >= OrphanStreamTimeoutMs
                      && now - kv.Value.LastActivityMs >= OrphanStreamIdleMs)
            .ToList())
        {
            try
            {
                // EndAgentMessageAsync 非 async：正文（TryRemove / Flush / UpdateMessage）同步执行到返回 Task 前，
                // FanOutAsync 返回的 Task 由 SendSafelyAsync 内部吞错，无需观察
                _ = EndAgentMessageAsync(kv.Value.GroupId, kv.Key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "孤儿流收尾失败：{MessageId}", kv.Key);
            }
        }
    }

    // ================= 智能体触发（协议 §6） =================

    public void RegisterAgent(AgentRegisterRequest req) => _agents.Register(req);

    public void UnregisterAgent(AgentUnregisterRequest req) => _agents.Unregister(req.AgentId, req.GroupIds);

    /// <summary>
    /// 【预留】真实 AG-UI 调用应答回灌入口：实现 IAgentGateway 时，把 AG-UI 运行时产出的
    /// TEXT_MESSAGE_* / TOOL_CALL_* 事件通过此方法扇出到群内订阅者。
    /// onlyTo 缺省时推送给该群全部已订阅连接。
    /// </summary>
    public Task BroadcastAsync(string groupId, object evt, IReadOnlySet<string>? onlyTo = null, CancellationToken ct = default)
    {
        GetGroupOrThrow(groupId);
        return FanOutAsync(groupId, evt, onlyTo, ct);
    }

    private void TriggerAgents(GroupMessage msg)
    {
        // 频道级 RBAC（4.2）：被群限制为不可触发智能体的成员（CanInvokeAgents=false）——其消息不唤起任何智能体
        if (msg.SenderType != MemberType.Agent && _store.GetMember(msg.GroupId, msg.SenderId) is { } sender && !sender.CanInvokeAgents)
        {
            _logger.LogInformation("成员 {MemberId} 被群权限限制为不可触发智能体，跳过触发评估（group={GroupId}）", msg.SenderId, msg.GroupId);
            return;
        }

        List<AgentRegistration> triggered;
        try { triggered = _triggers.Evaluate(msg).ToList(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "智能体触发规则评估失败");
            return;
        }
        // 分身仅在归属用户离线时启用（用户在线时分身暂停）
        triggered = triggered.Where(reg => !IsTwinPaused(reg.AgentId)).ToList();

        // 用户 @ 自己且已设置分身 → 显式召唤分身回答（即使在线；分身须为本群成员）。
        // 仅发送者 @ 自己触发召唤（他人 @ 不召唤），召唤时按「提及」语义直接发言（不走语境决策）
        var summoned = new HashSet<string>(StringComparer.Ordinal);
        if (msg.Mentions is { Count: > 0 } && _twinSync is not null)
        {
            foreach (var uid in msg.Mentions)
            {
                if (uid != msg.SenderId) continue;
                var twin = _twinSync.GetTwinAgent(uid);
                if (twin is null || !_store.IsMember(msg.GroupId, twin.AgentId)) continue;
                var reg = _agents.ForGroupAgent(msg.GroupId, twin.AgentId);
                if (reg is null) continue;
                if (!triggered.Any(r => r.AgentId == twin.AgentId))
                    triggered.Add(reg);
                summoned.Add(twin.AgentId);
                _logger.LogInformation("用户 {UserId} @ 自己召唤分身 {TwinId}（group={GroupId}，message={MessageId}）",
                    uid, twin.AgentId, msg.GroupId, msg.MessageId);
            }
        }

        // 数字员工单聊直达（C1）：在 kind=direct 的两人私有单聊群里，真人群主发的任何普通消息都被视为
        // 对另一端那唯一数字员工的“直达”触发（无需手动 @）；已在常规触发内的对端去掉，避免双重调用。
        if (msg.SenderType != MemberType.Agent && _store.GetGroup(msg.GroupId)?.IsDirectChat == true)
        {
            var partnerRegs = _store.ListMembers(msg.GroupId)
                .Where(m => m.MemberType == MemberType.Agent && m.MemberId != msg.SenderId)
                .Select(m => _agents.ForGroupAgent(msg.GroupId, m.MemberId))
                .Where(r => r is not null)
                .Cast<AgentRegistration>()
                .ToList();
            foreach (var reg in partnerRegs)
            {
                triggered.RemoveAll(r2 => r2.AgentId == reg.AgentId);
                InvokeAgentFor(reg, msg, mentioned: true, summoned: false); // 按“提及”语义必发言（C1：普通消息即对其直达）
            }
        }

        if (triggered.Count == 0) return;

        _logger.LogInformation("消息 {MessageId} 命中 {Count} 个智能体触发规则", msg.MessageId, triggered.Count);
        foreach (var reg in triggered)
        {
            // 被 @（或 @全体）的智能体按显式提及语义调用：必发言（跳过语境沉默决策）
            var mentioned = msg.MentionAll || msg.Mentions.Contains(reg.AgentId);
            InvokeAgentFor(reg, msg, mentioned, summoned.Contains(reg.AgentId));
        }
    }

    /// <summary>
    /// 以触发消息为上下文异步调用单个智能体（应答经 BroadcastAsync 回灌，不阻塞消息扇出；
    /// 经 _agentInvocationLimiter 限流并发（超出的排队等待），防止打爆模型 / 桥接服务）。
    /// </summary>
    private void InvokeAgentFor(AgentRegistration reg, GroupMessage msg, bool mentioned, bool summoned)
    {
        // 按客户端路由：使用请求携带的客户端/机器标识（前端经同机回环自动发现），非用户设置项
        var preferredClient = string.IsNullOrWhiteSpace(msg.BridgeClient) ? null : msg.BridgeClient;
        var context = new AgentInvocationContext(
            msg.GroupId, msg.ThreadId, reg.AgentId, reg.Nickname,
            msg.MessageId, msg.SenderId, msg.Content, msg.Mentions, msg.MentionAll,
            msg.Attachments,
            mentioned || summoned ? AgentTriggerMode.Mentioned : reg.TriggerMode, // 被 @ 或召唤：显式提及语义
            msg.TopicId,
            Visibility: msg.Visibility,          // 回复继承触发消息可见性（私密 / 定向内容不外泄）
            VisibleMemberIds: msg.VisibleMemberIds,
            PreferredBridgeClient: preferredClient);
        _ = Task.Run(async () =>
        {
            await _agentInvocationLimiter.WaitAsync();
            try { await _agentGateway.InvokeAsync(context, CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "智能体 {AgentId} 调用异常", reg.AgentId); }
            finally { _agentInvocationLimiter.Release(); }
        });
    }

    /// <summary>
    /// 分身暂停判定：agentId 形如 twin_{userId}，其归属用户当前有活跃连接（在线）时分身不响应。
    /// </summary>
    private bool IsTwinPaused(string agentId)
    {
        if (!agentId.StartsWith("twin_", StringComparison.Ordinal)) return false;
        var userId = agentId["twin_".Length..];
        return _connections.MemberConnectionCount(userId) > 0;
    }

    // ================= 扇出与工具方法 =================

    /// <summary>
    /// 把事件推送给群组的所有已订阅连接（可选按成员过滤）。
    /// </summary>
    private async Task FanOutAsync(string groupId, object evt, IReadOnlySet<string>? onlyTo = null, CancellationToken ct = default)
    {
        var json = AguiJson.Serialize(evt);
        var targets = _connections.SubscribersOf(groupId)
            .Where(c => onlyTo is null || onlyTo.Contains(c.MemberId))
            .ToList();
        if (targets.Count == 0) return;
        await Task.WhenAll(targets.Select(c => SendSafelyAsync(c, json, ct)));
    }

    private async Task SendSafelyAsync(HubConnection connection, string json, CancellationToken ct)
    {
        try { await connection.SendAsync(json, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "向连接 {ConnectionId} 推送事件失败", connection.ConnectionId); }
    }

    /// <summary>
    /// 直发事件给指定成员的<b>全部活跃连接</b>（不经过群订阅索引）：用于「被加入新群 / 新成员入群」
    /// 时通知成员刷新群列表——被加入者此时未订阅新群，常规 FanOut 到群订阅者是收不到的。
    /// 前端对 GROUP_CREATED / GROUP_MEMBER_JOINED 均会刷新群列表（幂等，重复收到无副作用）。
    /// </summary>
    private async Task NotifyMemberConnectionsAsync(IEnumerable<string> memberIds, object evt, CancellationToken ct)
    {
        var targets = memberIds.Distinct(StringComparer.Ordinal)
            .SelectMany(_connections.ConnectionsOf)
            .ToList();
        if (targets.Count == 0) return;
        var json = AguiJson.Serialize(evt);
        await Task.WhenAll(targets.Select(c => SendSafelyAsync(c, json, ct)));
    }

    /// <summary>
    /// 发送者回显：把消息三元组直连推送给发送者的所有活跃连接，不经过订阅索引。
    /// 协议 2.3 承诺发送者恒收到自己的消息；即使连接尚未订阅该群也能看到自己的发送结果。
    /// </summary>
    private async Task EchoToSenderAsync(string memberId, object startEvt, object contentEvt, object endEvt, CancellationToken ct)
    {
        var targets = _connections.ConnectionsOf(memberId);
        if (targets.Count == 0) return;
        foreach (var target in targets)
        {
            await SendSafelyAsync(target, AguiJson.Serialize(startEvt), ct);
            await SendSafelyAsync(target, AguiJson.Serialize(contentEvt), ct);
            await SendSafelyAsync(target, AguiJson.Serialize(endEvt), ct);
        }
    }

    /// <summary>
    /// 按协议 2.3 visibility 规则解析消息接收者：
    ///   all       —— 全群成员；
    ///   mentioned —— mentionAll 或 mentions 命中者；mentions 为空时仅发送者本人；
    ///   private   —— visibleMemberIds 命中者；为空时仅发送者本人。
    /// 发送者恒回显（发送者必为群成员）。
    /// </summary>
    /// <summary>客服知聚的客服（支持团队）成员 id：群为客服知聚时返回 Role != Normal 的成员；否则空集。</summary>
    private HashSet<string> SupportStaffIds(string groupId)
    {
        var group = _store.GetGroup(groupId);
        if (group is null || !group.IsSupportCircle) return new HashSet<string>(StringComparer.Ordinal);
        return _store.ListMembers(groupId)
            .Where(m => m.Role != GroupRole.Normal)
            .Select(m => m.MemberId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private bool IsSupportStaff(string groupId, string memberId) => SupportStaffIds(groupId).Contains(memberId);

    /// <summary>客服知聚的消息隔离：按发送者角色强制作用域，防止跨顾客会话泄露。</summary>
    private void ApplySupportCircleScoping(Group group, string senderId, bool isStaff, GroupMessage msg)
    {
        if (!group.IsSupportCircle) return;
        if (isStaff)
        {
            if (msg.ReplyToMessageId is { } replyId
                && _store.GetMessage(group.GroupId, replyId) is { } replied)
            {
                // 客服回复某顾客的消息 → 定向到该顾客（其它顾客不可见；客服恒可见全部）。
                if (replied.SenderId == senderId)
                {
                    // 客服之间的回复 → 仅客服可见（不发顾客）
                    msg.Visibility = MessageVisibility.Private;
                    msg.VisibleMemberIds = SupportStaffIds(group.GroupId).ToArray();
                }
                else
                {
                    msg.Visibility = MessageVisibility.Private;
                    msg.VisibleMemberIds = new[] { replied.SenderId };
                }
            }
            else
            {
                // 客服未带目标的一般消息 → 仅客服之间可见（默认不广播给顾客，严守“顾客只见自己的会话”）。
                msg.Visibility = MessageVisibility.Private;
                msg.VisibleMemberIds = SupportStaffIds(group.GroupId).ToArray();
            }
        }
        else
        {
            // 顾客发出的消息 → 仅自己与客服可见（会话隔离，顾客之间彼此隔离）
            msg.Visibility = MessageVisibility.Private;
            msg.VisibleMemberIds = new[] { senderId };
        }
    }

    private HashSet<string> ResolveRecipients(
        string groupId,
        MessageVisibility visibility,
        IReadOnlyList<string> mentions,
        bool mentionAll,
        IReadOnlyList<string> visibleMemberIds,
        string senderId)
    {
        var memberIds = _store.ListMembers(groupId).Select(m => m.MemberId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> recipients;
        switch (visibility)
        {
            case MessageVisibility.Mentioned when !mentionAll && mentions.Count > 0:
                recipients = mentions.Where(memberIds.Contains).ToHashSet(StringComparer.Ordinal);
                break;
            case MessageVisibility.Private when visibleMemberIds.Count > 0:
                recipients = visibleMemberIds.Where(memberIds.Contains).ToHashSet(StringComparer.Ordinal);
                break;
            default:
                recipients = memberIds;
                break;
        }
        // 客服知聚：客服（支持团队）恒可见全部会话，实时扇出一并覆盖；
        // 定向消息中的非成员顾客参与者也要收到（顾客不在此群成员表中）。
        foreach (var staff in SupportStaffIds(groupId)) recipients.Add(staff);
        foreach (var vid in visibleMemberIds)
            if (IsSupportCustomer(groupId, vid)) recipients.Add(vid);
        if (memberIds.Contains(senderId)) recipients.Add(senderId);
        return recipients;
    }

    /// <summary>
    /// 客服知聚感知的可见性判定（实例：会按客服知聚中客服恒可见全部处理）。
    /// 历史 / 快照检索过滤用，与 <see cref="ResolveRecipients"/> 实时扇出规则保持一致。
    /// </summary>
    public bool CanSeeMessageAware(GroupMessage m, string viewerId)
    {
        var staffSeesAll = SupportStaffIds(m.GroupId).Contains(viewerId);
        return CanSeeMessageCore(m, viewerId, staffSeesAll);
    }

    /// <summary>无群上下文的核心可见性判定（staffSeesAll 为客服知聚中客服恒可见全部）。
    /// 发送者恒可见；全群可见（All）恒可见；mentioned 未 @ 或 @ 全体按全群处理；
    /// private 未指定成员按全群处理；定向 / 私聊消息仅命中成员可见。</summary>
    public static bool CanSeeMessageCore(GroupMessage m, string viewerId, bool staffSeesAll)
    {
        if (staffSeesAll) return true;
        if (m.SenderId == viewerId) return true;
        return m.Visibility switch
        {
            MessageVisibility.All => true,
            MessageVisibility.Mentioned when m.MentionAll || m.Mentions.Count == 0 => true,
            MessageVisibility.Mentioned => m.Mentions.Contains(viewerId),
            MessageVisibility.Private when m.VisibleMemberIds.Count == 0 => true,
            MessageVisibility.Private => m.VisibleMemberIds.Contains(viewerId),
            _ => false,
        };
    }

    /// <summary>非客服感知的静态可见性判定（不带客服知聚的客服恒可见全部规则；用于无 GroupHub 实例的场景）。</summary>
    public static bool CanSeeMessage(GroupMessage m, string viewerId)
        => CanSeeMessageCore(m, viewerId, staffSeesAll: false);

    private Group GetGroupOrThrow(string groupId)
        => (_store.GetGroup(groupId) is { } g && !_disbanded.ContainsKey(groupId))
            ? g
            : throw new AguiProtocolException(ErrorCodes.GroupNotFound, "群组不存在或已解散");

    private bool CanManage(string operatorId, Group group)
    {
        if (group.OwnerId == operatorId) return true;
        return _store.GetMember(group.GroupId, operatorId) is { Role: GroupRole.Admin };
    }

    private void EnsureCanManage(string operatorId, Group group)
    {
        if (!CanManage(operatorId, group))
            throw new AguiProtocolException(ErrorCodes.GroupPermissionDenied, "无群组操作权限");
    }

    /// <summary>解析成员 RBAC 权限 JSON 对象（{canInvokeAgents?, canApprove?}），非法返回 false。</summary>
    private static bool TryParsePermissions(JsonElement je, out GroupMemberPermissions? perms)
    {
        perms = null;
        if (je.ValueKind != JsonValueKind.Object) return false;
        var parsed = System.Text.Json.JsonSerializer.Deserialize<GroupMemberPermissions>(je.GetRawText(),
            System.Text.Json.JsonSerializerOptions.Default);
        if (parsed is null) return false;
        perms = parsed;
        return true;
    }

    internal static MemberType ResolveMemberType(string memberId)
        => memberId.StartsWith("agent_", StringComparison.Ordinal) ? MemberType.Agent : MemberType.User;

    /// <summary>
    /// 成员默认显示名：注册用户取账号昵称 → 用户名 → 兜底用户 ID；
    /// 未注册用户（如示例身份 / 智能体）直接显示其 ID。
    /// </summary>
    internal string DefaultNickname(string memberId)
    {
        if (_users.GetUserById(memberId) is { } user)
        {
            if (!string.IsNullOrWhiteSpace(user.Nickname)) return user.Nickname;
            if (!string.IsNullOrWhiteSpace(user.Username)) return user.Username;
            return user.UserId;
        }

        return memberId;
    }

    /// <summary>
    /// 用户资料（昵称）变更后，把其所在各群的成员显示名同步为
    /// 账号昵称 → 用户名 → 用户 ID，并广播 GROUP_MEMBER_UPDATED。
    /// </summary>
    public async Task SyncUserDisplayNameAsync(string userId, CancellationToken ct = default)
    {
        var displayName = DefaultNickname(userId);
        foreach (var group in _store.GroupsOf(userId))
        {
            var member = _store.GetMember(group.GroupId, userId);
            if (member is null || member.Nickname == displayName) continue;

            member.Nickname = displayName;
            _store.UpdateMember(group.GroupId, member);
            _changes?.Notify();
            await FanOutAsync(group.GroupId, new GroupMemberUpdatedEvent
            {
                GroupId = group.GroupId,
                MemberId = userId,
                UpdateFields = ["nickname"],
                MemberInfo = new Dictionary<string, JsonElement> { ["nickname"] = AguiJson.Element(displayName) },
                OperatorId = userId,
                Timestamp = NowMs,
            }, ct: ct);
        }
    }

    /// <summary>
    /// 用户头像变更后，把其所在各群的成员头像同步为新值（null 即清除），并广播 GROUP_MEMBER_UPDATED。
    /// </summary>
    public async Task SyncUserAvatarAsync(string userId, CancellationToken ct = default)
    {
        var avatar = _users.GetUserById(userId)?.Avatar;
        foreach (var group in _store.GroupsOf(userId))
        {
            var member = _store.GetMember(group.GroupId, userId);
            if (member is null || member.Avatar == avatar) continue;

            member.Avatar = avatar;
            _store.UpdateMember(group.GroupId, member);
            _changes?.Notify();
            await FanOutAsync(group.GroupId, new GroupMemberUpdatedEvent
            {
                GroupId = group.GroupId,
                MemberId = userId,
                UpdateFields = ["avatar"],
                MemberInfo = new Dictionary<string, JsonElement> { ["avatar"] = AguiJson.Element(avatar) },
                OperatorId = userId,
                Timestamp = NowMs,
            }, ct: ct);
        }
    }

    /// <summary>
    /// 智能体资料（昵称 / 头像）变更后，把其所在各群的成员资料同步并广播 GROUP_MEMBER_UPDATED。
    /// </summary>
    public async Task SyncAgentProfileAsync(string agentId, string nickname, string? avatar, CancellationToken ct = default)
    {
        foreach (var group in _store.GroupsOf(agentId))
        {
            var member = _store.GetMember(group.GroupId, agentId);
            if (member is null || member.MemberType != MemberType.Agent) continue;

            var fields = new List<string>();
            var info = new Dictionary<string, JsonElement>();
            if (member.Nickname != nickname)
            {
                member.Nickname = nickname;
                fields.Add("nickname");
                info["nickname"] = AguiJson.Element(nickname);
            }
            if (member.Avatar != avatar)
            {
                member.Avatar = avatar;
                fields.Add("avatar");
                info["avatar"] = AguiJson.Element(avatar);
            }
            if (fields.Count == 0) continue;

            _store.UpdateMember(group.GroupId, member); // 昵称 / 头像为原地修改，落库（数据库模式）
            _changes?.Notify();
            await FanOutAsync(group.GroupId, new GroupMemberUpdatedEvent
            {
                GroupId = group.GroupId,
                MemberId = agentId,
                UpdateFields = fields,
                MemberInfo = info,
                OperatorId = agentId,
                Timestamp = NowMs,
            }, ct: ct);
        }
    }

    /// <summary>把一条完整消息送入语义记忆（内容为空 / 未启用记忆 / 非全群可见消息时跳过）。
    /// 定向（mentioned / private）消息仅部分成员可见，不写入语义记忆，防止私密内容被后续检索注入。</summary>
    private void RememberMessage(GroupMessage msg)
    {
        if (_memory is null || string.IsNullOrWhiteSpace(msg.Content) || msg.Visibility != MessageVisibility.All) return;
        _memory.Remember(new MessageMemoryEntry(
            msg.MessageId, msg.GroupId, msg.TopicId,
            msg.SenderId, msg.SenderType.ToString(), msg.Content, msg.Timestamp));
        // 图谱记忆：同一消息也送入图抽取队列（仅对全群可见内容，防私密内容进图污染关系）
        _graph?.Remember(new GraphMessageEntry(msg.GroupId, msg.SenderId, msg.Content, msg.Timestamp));
    }

    /// <summary>查询用户是否开启个人记忆（未注册用户 / 未开启返回 false）。供 AgentGateway 决定是否检索注入触发者的个人记忆。</summary>
    public bool IsPersonalMemoryEnabled(string userId)
        => _users.GetUserById(userId)?.PersonalMemoryEnabled ?? false;

    /// <summary>
    /// 私密智能体归属校验：仅创建者可将其加入群；种子（无 OwnerId）智能体不受限。
    /// 未注入 IAgentDefinitionStore（如仅协议 Hub）时不校验，保持兼容。
    /// </summary>
    private void EnsureCanAddAgents(string operatorId, IReadOnlyList<string> memberIds)
    {
        if (_agentDefinitions is null || memberIds.Count == 0) return;
        foreach (var id in memberIds)
        {
            var def = _agentDefinitions.GetDefinition(id);
            if (def?.IsPrivate == true && def.OwnerId != operatorId)
                throw new AguiProtocolException(ErrorCodes.AgentPermissionDenied,
                    $"私密智能体「{def.Nickname}」仅创建者可将其加入群");
        }
    }

    /// <summary>分身跟随（加入）：公开群新增用户成员时，其已启用分身自动加入（私密群不加入；无分身 / 已在群内跳过）。</summary>
    private async Task SyncTwinMembersInAsync(string groupId, IEnumerable<string> userIds, CancellationToken ct)
    {
        if (_twinSync is null) return;
        var group = _store.GetGroup(groupId);
        if (group?.IsPrivate == true) return;
        foreach (var uid in userIds.Distinct(StringComparer.Ordinal))
        {
            var twin = _twinSync.GetTwinAgent(uid);
            if (twin is null || _store.IsMember(groupId, twin.AgentId)) continue;
            try
            {
                await AddSystemMembersAsync(groupId, [twin.AgentId], uid,
                [
                    new MemberSeed { MemberId = twin.AgentId, MemberType = MemberType.Agent, Nickname = twin.Nickname },
                ], ct);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "分身自动加入群失败：{TwinId} → {GroupId}", twin.AgentId, groupId); }
        }
    }

    /// <summary>分身跟随（退出）：用户退出 / 被移除时其分身一并退出该群。</summary>
    private async Task SyncTwinMembersOutAsync(string groupId, IEnumerable<string> memberIds, CancellationToken ct)
    {
        if (_twinSync is null) return;
        foreach (var uid in memberIds.Distinct(StringComparer.Ordinal))
        {
            var twin = _twinSync.GetTwinAgent(uid);
            if (twin is null || !_store.IsMember(groupId, twin.AgentId)) continue;
            try { await LeaveGroupAsync(groupId, twin.AgentId, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "分身自动退群失败：{TwinId} → {GroupId}", twin.AgentId, groupId); }
        }
    }

    private static string? GetString(Dictionary<string, JsonElement> dict, string key)
        => dict.TryGetValue(key, out var je) && je.ValueKind == JsonValueKind.String ? je.GetString() : null;

    private static bool GetBool(Dictionary<string, JsonElement> dict, string key)
        => dict.TryGetValue(key, out var je) && je.ValueKind == JsonValueKind.True;
}
