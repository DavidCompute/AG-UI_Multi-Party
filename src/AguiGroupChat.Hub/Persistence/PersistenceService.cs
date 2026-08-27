using System.Text;
using System.Text.Json;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Hub.Persistence;

/// <summary>
/// 状态持久化服务：把所有运行态（用户 / 会话 / 群组 / 成员 / 消息 / 触发规则，
/// 以及上层注册的扩展区如智能体定义）周期性地快照写入单个 JSON 文件，启动时恢复。
///
/// 写入策略：各存储变更经 <see cref="ChangeHub"/> 标记脏位，后台定时器合并落盘
/// （默认 5 秒一次），关闭时再强制冲刷一次；写入采用临时文件 + 原子替换，避免半写损坏。
/// </summary>
public sealed class PersistenceService : IDisposable
{
    private readonly IUserStore _users;
    private readonly IGroupStore _groups;
    private readonly AuthService _auth;
    private readonly AgentRegistry _registry;
    private readonly PersistenceOptions _options;
    private readonly ILogger<PersistenceService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, (Func<object?> Snapshot, Action<JsonElement> Restore)> _sections = new(StringComparer.Ordinal);
    private Timer? _timer;
    private bool _dirty; // 脏位：全部读写经 Volatile.Read / Volatile.Write（见 Flush / MarkDirty）
    private int _flushInProgress;

    public PersistenceService(
        IUserStore users,
        IGroupStore groups,
        AuthService auth,
        AgentRegistry registry,
        PersistenceOptions options,
        ChangeHub changeHub,
        ILogger<PersistenceService> logger)
    {
        _users = users;
        _groups = groups;
        _auth = auth;
        _registry = registry;
        _options = options;
        _logger = logger;

        if (IsEnabled)
        {
            changeHub.Subscribe(MarkDirty);
            var interval = TimeSpan.FromSeconds(Math.Max(1, options.FlushIntervalSeconds));
            _timer = new Timer(_ => Flush(), null, interval, interval);
        }
    }

    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.FilePath);

    /// <summary>
    /// 注册扩展区读写回调（如 Web 层的智能体定义）。须在 <see cref="Load"/> 之前调用。
    /// </summary>
    public void AddSection(string name, Func<object?> snapshot, Action<JsonElement> restore)
    {
        lock (_gate) _sections[name] = (snapshot, restore);
    }

    private void MarkDirty() => Volatile.Write(ref _dirty, true);

    /// <summary>
    /// 从磁盘恢复全部状态。返回是否成功载入（false = 无文件 / 文件损坏 / 未启用）。
    /// 载入成功时调用方应跳过示例数据播种，避免重复。
    /// </summary>
    public bool Load()
    {
        if (!IsEnabled) return false;
        var path = _options.FilePath!;
        if (!File.Exists(path)) return false;

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var snapshot = JsonSerializer.Deserialize<HubSnapshot>(json, AguiJson.Options);
            if (snapshot is null) return false;

            RestoreUsers(snapshot.Users);
            _auth.RestoreSessions(snapshot.Sessions);
            RestoreGroups(snapshot.Groups);
            RestoreRegistry(snapshot.Registrations);

            foreach (var (name, section) in _sections)
            {
                if (snapshot.Sections.TryGetValue(name, out var element))
                {
                    try { section.Restore(element); }
                    catch (Exception ex) { _logger.LogWarning(ex, "扩展区「{Name}」恢复失败，已跳过", name); }
                }
            }

            _logger.LogInformation("已从 {Path} 恢复状态：{Users} 用户 / {Sessions} 会话 / {Groups} 群 / {Agents} 智能体 / {Registrations} 触发规则",
                path, snapshot.Users.Count, snapshot.Sessions.Count, snapshot.Groups.Count,
                snapshot.Sections.TryGetValue("agents", out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0,
                snapshot.Registrations.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "持久化文件读取失败，将使用空状态启动：{Path}", path);
            return false;
        }
    }

    /// <summary>有变更时立即快照写入（关闭前由 ApplicationStopping 调用一次）。</summary>
    public void Flush()
    {
        if (!IsEnabled) return;
        // 已有落盘进行中：本轮让位（脏位由进行中那轮保留，清位只发生在真正干活的那一轮，防并发丢变更）
        if (Interlocked.Exchange(ref _flushInProgress, 1) == 1) return;
        // 先原子清位再干活：清位后若有新变更会重新置脏，由下一轮定时器再写，数据不丢（去掉结尾的读-清双检）
        if (Interlocked.Exchange(ref _dirty, false) == false)
        {
            Interlocked.Exchange(ref _flushInProgress, 0); // 无变更：释放落盘标记
            return;
        }

        try
        {
            var snapshot = BuildSnapshot();
            var json = JsonSerializer.Serialize(snapshot, AguiJson.Options);
            var path = _options.FilePath!;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            // 优先原子替换（Windows 上目标存在时为原子操作，避免半写文件）；目标不存在 / 平台不支持时回退为移动覆盖
            try
            {
                File.Replace(tmp, path, null);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            {
                File.Move(tmp, path, overwrite: true);
            }
            _logger.LogDebug("状态已落盘：{Path}（{Groups} 群 / {Messages} 消息）", path, snapshot.Groups.Count, snapshot.Groups.Sum(g => g.Messages.Count));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "状态落盘失败：{Path}", _options.FilePath);
        }
        finally
        {
            Interlocked.Exchange(ref _flushInProgress, 0);
        }
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>清空持久化快照（系统初始化用）：删除磁盘快照文件，下一次 Flush 不再恢复旧数据。</summary>
    public void Reset()
    {
        if (!IsEnabled) return;
        try
        {
            if (File.Exists(_options.FilePath!)) File.Delete(_options.FilePath!);
            Volatile.Write(ref _dirty, true); // 强制下次落盘空快照（数据已清空的当前状态）
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "持久化快照清理失败：{Path}", _options.FilePath);
        }
    }

    // ================= 快照构建 =================

    private HubSnapshot BuildSnapshot()
    {
        var snapshot = new HubSnapshot
        {
            SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Users = _users.ListUsers().ToList(),
            Sessions = _auth.SnapshotSessions().ToList(),
            Groups = _groups.AllGroups().Select(g => new PersistedGroup
            {
                Group = g,
                Members = _groups.ListMembers(g.GroupId).ToList(),
                Messages = _groups.AllMessages(g.GroupId).Select(ToPersisted).ToList(),
                Topics = _groups.ListTopics(g.GroupId).ToList(),
            }).ToList(),
            Registrations = _registry.AllRegistrations().ToList(),
        };

        foreach (var (name, section) in _sections)
        {
            var value = section.Snapshot();
            if (value is null) continue;
            snapshot.Sections[name] = AguiJson.Element(value);
        }
        return snapshot;
    }

    private static PersistedMessage ToPersisted(GroupMessage m) => new()
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
        AgentChain = m.AgentChain,
        PlanJson = m.PlanJson,
        Timestamp = m.Timestamp,
        Recalled = m.Recalled,
    };

    // ================= 恢复 =================

    private void RestoreUsers(IEnumerable<UserAccount> users)
    {
        foreach (var user in users)
        {
            if (!_users.AddUser(user))
                _logger.LogDebug("恢复用户冲突，跳过：{UserId}", user.UserId);
        }
    }

    private void RestoreGroups(IEnumerable<PersistedGroup> groups)
    {
        foreach (var pg in groups)
        {
            var group = pg.Group;
            if (!_groups.AddGroup(group))
            {
                _logger.LogDebug("恢复群组冲突，跳过：{GroupId}", group.GroupId);
                continue;
            }
            foreach (var member in pg.Members)
            {
                member.OnlineStatus = OnlineStatus.Offline; // 在线状态为连接态，重启后一律离线
                _groups.AddMember(group.GroupId, member);
            }
            foreach (var pm in pg.Messages)
                _groups.AddMessage(FromPersisted(pm));
            foreach (var topic in pg.Topics)
                _groups.AddTopic(topic); // 恢复话题（AddTopic 自带查重，旧快照无 Topics 字段时默认为空列表）
            group.MemberCount = _groups.MemberCount(group.GroupId);
        }
    }

    private static GroupMessage FromPersisted(PersistedMessage m)
    {
        var msg = new GroupMessage
        {
            MessageId = m.MessageId,
            GroupId = m.GroupId,
            ThreadId = m.ThreadId,
            TopicId = m.TopicId,
            SenderId = m.SenderId,
            SenderType = m.SenderType,
            SenderNickname = m.SenderNickname,
            ReplyToMessageId = m.ReplyToMessageId,
            Mentions = m.Mentions,
            MentionAll = m.MentionAll,
            Visibility = m.Visibility,
            VisibleMemberIds = m.VisibleMemberIds,
            Attachments = m.Attachments,
            Content = m.Content,
            Reasoning = m.Reasoning,
            AgentChain = m.AgentChain,
            PlanJson = m.PlanJson,
            Timestamp = m.Timestamp,
        };
        msg.Recalled = m.Recalled;
        return msg;
    }

    private void RestoreRegistry(IEnumerable<AgentRegistration> registrations)
        => _registry.RestoreAll(registrations);
}
