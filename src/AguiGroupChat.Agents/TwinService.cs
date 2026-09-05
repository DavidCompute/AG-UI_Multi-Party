using System.Collections.Concurrent;
using System.Text;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 用户「AI 分身」：根据用户在各公开群的发言记录生成人设（Instructions），
/// 以独立智能体（agentId = twin_{userId}，私密、归属用户）加入用户所在的所有公开群，
/// 按用户设定的触发方式回复。停用即删除分身（目录 + 退出全部群）。
/// 同时实现 <see cref="ITwinAgentSync"/>：公开群新增 / 移除用户成员时自动跟随加入 / 退出。
/// </summary>
public sealed class TwinService : ITwinAgentSync
{
    /// <summary>人设生成时聚合的用户公开群发言条数上限。</summary>
    private const int CorpusMaxMessages = 120;

    /// <summary>人设生成时的发言总字符上限（控制单次模型输入）。</summary>
    private const int CorpusMaxChars = 8000;

    /// <summary>per-user 互斥：同一用户的分身操作（启用 / 停用 / 改触发 / 同步群）串行化，
    /// 防止并发触发互相覆盖定义 / 重复加群 / 停用与启用交错（static：任何实例下同一用户都互斥）。</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    private readonly AgentCatalog _catalog;
    private readonly AgentOptions _options;
    private readonly ILogger<TwinService> _logger;
    private readonly Lazy<GroupHub> _hub;
    private readonly Lazy<AuthService> _auth;

    public TwinService(AgentOptions options, IServiceProvider services, ILogger<TwinService> logger)
    {
        _options = options;
        _logger = logger;
        _catalog = services.GetRequiredService<AgentCatalog>();
        _hub = new Lazy<GroupHub>(() => services.GetRequiredService<GroupHub>());
        _auth = new Lazy<AuthService>(() => services.GetService<AuthService>()
            ?? throw new InvalidOperationException("AuthService 未注册（分身需要用户资料）"));
    }

    /// <summary>分身的智能体 ID。</summary>
    public static string AgentIdOf(string userId) => AgentIdPrefix + userId;

    /// <summary>分身智能体 ID 前缀（系统保留：仅经 /ag-ui/twin 管理，不出现在智能体管理目录）。</summary>
    public const string AgentIdPrefix = "twin_";

    /// <summary>触发模式线上小驼峰名（与前端 select / 协议一致，如 mentioned / allMessages）。</summary>
    private static string ModeString(AgentTriggerMode mode) => TriggerModeWire.ToWire(mode);

    /// <summary>获取 / 创建该用户的互斥信号量并等待获取（方法体用 try/finally Release）。</summary>
    private static async Task<SemaphoreSlim> AcquireUserLockAsync(string userId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return gate;
    }

    /// <inheritdoc />
    public TwinAgentInfo? GetTwinAgent(string userId)
    {
        var def = _catalog.GetDefinition(AgentIdOf(userId));
        return def is null ? null : new TwinAgentInfo(def.AgentId, def.Nickname);
    }

    /// <summary>当前用户的分身状态；未启用返回 null。</summary>
    public TwinStatus? GetStatus(string userId)
    {
        var def = _catalog.GetDefinition(AgentIdOf(userId));
        return def is null ? null : new TwinStatus(true, def.AgentId, def.Nickname, ModeString(def.TriggerMode));
    }

    /// <summary>修改分身触发方式：更新定义并同步到分身所在全部公开群的触发规则。</summary>
    public async Task<TwinStatus?> UpdateTriggerAsync(string userId, AgentTriggerMode triggerMode, CancellationToken ct = default)
    {
        var gate = await AcquireUserLockAsync(userId, ct);
        try
        {
            var twinId = AgentIdOf(userId);
            var def = _catalog.GetDefinition(twinId);
            if (def is null) return null;

            var hub = _hub.Value;
            _catalog.Upsert(new AgentDefinition
            {
                AgentId = def.AgentId,
                Nickname = def.Nickname,
                Description = def.Description,
                Instructions = def.Instructions,
                Avatar = def.Avatar,
                TriggerMode = triggerMode,
                Keywords = [],
                Model = def.Model,
                BridgeEndpoint = def.BridgeEndpoint,
                BridgeMode = def.BridgeMode,
                BridgeToken = def.BridgeToken,
                PersonalMemoryEnabled = def.PersonalMemoryEnabled,
                IsPrivate = def.IsPrivate,
                OwnerId = def.OwnerId,
            });
            foreach (var g in hub.Store.GroupsOf(userId).Where(g => !g.IsPrivate && hub.Store.IsMember(g.GroupId, twinId)).ToList())
            {
                hub.RegisterAgent(new AgentRegisterRequest
                {
                    AgentId = twinId,
                    Nickname = def.Nickname,
                    GroupIds = [g.GroupId],
                    TriggerMode = triggerMode,
                    Keywords = [],
                });
            }
            _logger.LogInformation("分身触发方式已更新 {UserId} → {Mode}", userId, triggerMode);
            return new TwinStatus(true, twinId, def.Nickname, ModeString(triggerMode));
        }
        finally { gate.Release(); }
    }

    /// <summary>启用分身：聚合公开群发言 → 生成人设 → 创建/更新分身 → 加入全部公开群并注册触发规则。</summary>
    public async Task<TwinStatus> EnableAsync(string userId, AgentTriggerMode triggerMode, CancellationToken ct = default)
    {
        var gate = await AcquireUserLockAsync(userId, ct);
        try
        {
            var hub = _hub.Value;
            var user = _auth.Value.GetUser(userId);
            var displayName = !string.IsNullOrWhiteSpace(user?.Nickname) ? user.Nickname
                : !string.IsNullOrWhiteSpace(user?.Username) ? user.Username
                : userId;

            // 1. 收集用户在公开群的发言（分身只学习公开群内容，私密群不参与）
            var corpus = CollectPublicCorpus(hub, userId);

            // 2. 生成人设
            var instructions = await GeneratePersonaAsync(displayName, corpus, ct);

            // 3. 创建 / 更新分身定义（私密 + 归属用户，其他用户不可见、不可拉取）
            var twinId = AgentIdOf(userId);
            _catalog.Upsert(new AgentDefinition
            {
                AgentId = twinId,
                Nickname = $"「{displayName}」的分身",
                Description = $"{displayName} 的 AI 分身（基于公开群发言自动生成，仅 {displayName} 可管理）",
                Instructions = instructions,
                TriggerMode = triggerMode,
                Keywords = [],
                IsPrivate = true,
                OwnerId = userId,
            });

            // 4. 加入用户所在的所有公开群 + 注册触发规则
            foreach (var g in hub.Store.GroupsOf(userId).Where(g => !g.IsPrivate).ToList())
            {
                if (!hub.Store.IsMember(g.GroupId, twinId))
                {
                    await hub.AddSystemMembersAsync(g.GroupId, [twinId], userId,
                    [
                        new MemberSeed { MemberId = twinId, MemberType = MemberType.Agent, Nickname = $"「{displayName}」的分身" },
                    ], ct);
                }
                hub.RegisterAgent(new AgentRegisterRequest
                {
                    AgentId = twinId,
                    Nickname = $"「{displayName}」的分身",
                    GroupIds = [g.GroupId],
                    TriggerMode = triggerMode,
                    Keywords = [],
                });
            }

            _logger.LogInformation("分身已启用 {UserId} → {TwinId}（触发 {Mode}，公开群 {Count} 个）",
                userId, twinId, triggerMode, hub.Store.GroupsOf(userId).Count(g => !g.IsPrivate));
            return new TwinStatus(true, twinId, $"「{displayName}」的分身", ModeString(triggerMode));
        }
        finally { gate.Release(); }
    }

    /// <summary>同步分身到用户当前所在全部公开群（补齐启用后新建 / 加入的公开群；不重建人设）。</summary>
    public async Task<TwinStatus?> SyncGroupsAsync(string userId, CancellationToken ct = default)
    {
        var gate = await AcquireUserLockAsync(userId, ct);
        try
        {
            var twinId = AgentIdOf(userId);
            var def = _catalog.GetDefinition(twinId);
            if (def is null) return null;

            var hub = _hub.Value;
            var mode = def.TriggerMode;
            foreach (var g in hub.Store.GroupsOf(userId).Where(g => !g.IsPrivate).ToList())
            {
                if (!hub.Store.IsMember(g.GroupId, twinId))
                {
                    await hub.AddSystemMembersAsync(g.GroupId, [twinId], userId,
                    [
                        new MemberSeed { MemberId = twinId, MemberType = MemberType.Agent, Nickname = def.Nickname },
                    ], ct);
                }
                hub.RegisterAgent(new AgentRegisterRequest
                {
                    AgentId = twinId,
                    Nickname = def.Nickname,
                    GroupIds = [g.GroupId],
                    TriggerMode = mode,
                    Keywords = [],
                });
            }
            _logger.LogInformation("分身已同步到全部公开群 {UserId} → {TwinId}", userId, twinId);
            return new TwinStatus(true, twinId, def.Nickname, ModeString(mode));
        }
        finally { gate.Release(); }
    }

    /// <summary>停用分身：删除定义、注销触发规则、退出所有群。返回是否曾启用。</summary>
    public async Task<bool> DisableAsync(string userId, CancellationToken ct = default)
    {
        var gate = await AcquireUserLockAsync(userId, ct);
        try
        {
            var twinId = AgentIdOf(userId);
            var def = _catalog.GetDefinition(twinId);
            if (def is null) return false;

            var hub = _hub.Value;
            _catalog.Remove(twinId);
            hub.UnregisterAgent(new AgentUnregisterRequest { AgentId = twinId, GroupIds = null });

            foreach (var g in hub.Store.AllGroups().Where(g => hub.Store.IsMember(g.GroupId, twinId)).ToList())
            {
                await hub.LeaveGroupAsync(g.GroupId, twinId, ct);
            }
            _logger.LogInformation("分身已停用 {UserId} → {TwinId}", userId, twinId);
            return true;
        }
        finally { gate.Release(); }
    }

    /// <summary>聚合用户在各公开群的发言（仅该用户自己的发言，按时间倒序，含群名标注）。</summary>
    private static string CollectPublicCorpus(GroupHub hub, string userId)
    {
        var sb = new StringBuilder();
        var total = 0;
        foreach (var g in hub.Store.GroupsOf(userId).Where(g => !g.IsPrivate).ToList())
        {
            var rows = hub.Store.AllMessages(g.GroupId)
                .Where(m => !m.Recalled && m.SenderId == userId && !string.IsNullOrWhiteSpace(m.Content))
                .OrderByDescending(m => m.Timestamp)
                .Take(CorpusMaxMessages)
                .Reverse();
            foreach (var m in rows)
            {
                if (total >= CorpusMaxMessages || sb.Length >= CorpusMaxChars) break;
                sb.Append('[').Append(g.GroupName).Append(']').Append(' ').Append(m.Content).Append('\n');
                total++;
            }
            if (total >= CorpusMaxMessages || sb.Length >= CorpusMaxChars) break;
        }
        return sb.ToString();
    }

    /// <summary>调模型生成分身人设：从发言记录提炼风格 / 主题 / 语气（mock 提供方返回固定文本）。</summary>
    private async Task<string> GeneratePersonaAsync(string displayName, string corpus, CancellationToken ct)
    {
        IChatClient client;
        if (string.Equals(_options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            client = new MockChatClient(new AgentDefinition { AgentId = "twin_gen", Nickname = displayName });
        }
        else
        {
            var isDeepSeek = string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            client = AgentCatalog.BuildOpenAIChatClient(_options, new AgentDefinition { AgentId = "twin_gen", Nickname = displayName }, isDeepSeek).AsIChatClient();
        }

        try
        {
            var prompt = new StringBuilder();
            prompt.Append("你是 ").Append(displayName).AppendLine(" 的 AI 分身人设生成器。");
            prompt.AppendLine("请根据 TA 在公开群中的发言记录，提炼 TA 的说话风格、常用语气、关注主题与表达习惯。");
            prompt.AppendLine("直接输出一段「分身人设」设定（200 字以内，以「你是「" + displayName + "」的 AI 分身。」开头，第二人称「你」描述自己的风格与立场，不要任何解释或前缀）：");
            if (!string.IsNullOrWhiteSpace(corpus))
            {
                prompt.AppendLine("\n【发言记录】");
                // 发言记录是用户原始文本，可能含恶意指令（prompt injection）：包上不可信边界
                prompt.Append(UntrustedBoundary.Wrap(corpus));
            }
            else
            {
                prompt.AppendLine("\n（暂无公开群发言记录，请按通用助手风格生成人设，并说明可先让用户多发言以完善人设）");
            }

            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt.ToString())], cancellationToken: ct);
            var text = resp.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("人设生成返回为空");
            return text;
        }
        finally
        {
            client.Dispose();
        }
    }
}

/// <summary>分身状态。</summary>
public sealed record TwinStatus(bool Enabled, string TwinAgentId, string Nickname, string TriggerMode);
