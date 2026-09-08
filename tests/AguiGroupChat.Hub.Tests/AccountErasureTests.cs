using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using AguiGroupChat.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>账号注销 / 数据擦除编排（AccountErasureService）语义测试：
/// 创建知聚的转让 / 解散、加入知聚的退群、语义记忆与个人知识库清除、账号行 / 会话 / TOTP 移除。</summary>
public sealed class AccountErasureTests
{
    // ================= 夹具 =================

    private sealed class Harness : IDisposable
    {
        public InMemoryGroupStore Store { get; } = new(new GroupChatOptions().MessageHistoryLimit);
        public InMemoryUserStore Users { get; } = new();
        public ConnectionManager Connections { get; } = new();
        public AgentRegistry Agents { get; } = new();
        public AgentTriggerService Triggers { get; }
        public NoopAgentGateway Gateway { get; }
        public GroupHub Hub { get; }
        public AuthService Auth { get; }
        public TotpService Totp { get; } = new();
        public FakeMemory Memory { get; } = new();
        public KnowledgeBaseCatalog Kbs { get; }
        public AuditLogService Audit { get; } = new();
        public AccountErasureService Erasure { get; }

        public Harness()
        {
            Triggers = new AgentTriggerService(Agents);
            Gateway = new NoopAgentGateway(NullLogger<NoopAgentGateway>.Instance);
            Hub = new GroupHub(Store, Users, Connections, Agents, Triggers, Gateway,
                new GroupChatOptions { MessageHistoryLimit = 200 }, TimeProvider.System,
                NullLogger<GroupHub>.Instance, memory: Memory);
            Auth = new AuthService(Users, new AuthOptions(), TimeProvider.System,
                NullLogger<AuthService>.Instance);
            Kbs = new KnowledgeBaseCatalog(new AgentOptions(),
                new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance);
            Erasure = new AccountErasureService(Auth, Totp, Hub, Kbs, Memory, Audit,
                NullLogger<AccountErasureService>.Instance);
        }

        public UserAccount AddUser(string id, string username, PlatformRole role = PlatformRole.User, string password = "secret123")
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var (salt, hash) = PasswordHasher.Hash(password);
            var user = new UserAccount
            {
                UserId = id,
                Username = username,
                PasswordSalt = salt,
                PasswordHash = hash,
                Nickname = username,
                CreatedAt = now,
                UpdatedAt = now,
                PlatformRole = role,
            };
            Assert.True(Users.AddUser(user));
            return user;
        }

        public void Dispose() => Hub.Dispose();

        /// <summary>向指定群直接写入一条历史消息（SenderId/昵称自定义；用于断言匿名化）。</summary>
        internal void SeedMessage(string messageId, string groupId, string senderId, string content, int attachments = 0)
        {
            Store.AddMessage(new GroupMessage
            {
                MessageId = messageId,
                GroupId = groupId,
                TopicId = "main",
                ThreadId = "thread_" + groupId,
                SenderId = senderId,
                SenderType = MemberType.User,
                SenderNickname = senderId,
                Content = content,
                Mentions = [],
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Attachments = attachments > 0
                    ? [new AttachmentInfo { AttachmentId = "att_" + messageId, Name = "f.txt", ContentType = "text/plain", Size = 0, Url = "/ag-ui/files/att_x/f.txt", Kind = "file" }]
                    : [],
            });
        }
    }

    private sealed class FakeMemory : IMessageMemory
    {
        public List<MessageMemoryItem> Items { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<string> RemovedGroups { get; } = [];

        public void Add(string messageId, string groupId, string senderId) => Items.Add(new MessageMemoryItem(
            messageId, groupId, "main", senderId, "user", $"内容 {messageId}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, null));

        public void Remember(MessageMemoryEntry entry) { }
        public void Forget(string groupId, string messageId) { }
        public void RemoveGroup(string groupId)
        {
            RemovedGroups.Add(groupId);
            Items.RemoveAll(i => i.GroupId == groupId);
        }
        public Task<IReadOnlyList<MessageMemoryHit>> SearchAsync(string groupId, string agentId, string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MessageMemoryHit>>([]);
        public Task<IReadOnlyList<MessageMemoryHit>> SearchPersonAsync(string personId, string currentGroupId, string query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MessageMemoryHit>>([]);

        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset)
            => Items.Where(i => (groupId is null || i.GroupId == groupId) && (senderId is null || i.SenderId == senderId))
                .OrderByDescending(i => i.Timestamp).Skip(offset).Take(limit).ToList();

        public long CountMessages(string? groupId, string? senderId, string? keyword)
            => ListMessages(groupId, senderId, keyword, int.MaxValue, 0).Count;

        public IReadOnlyList<MessageMemoryGroupStat> GroupStats() => [];
        public bool DeleteByMessageId(string messageId)
        {
            Deleted.Add(messageId);
            return Items.RemoveAll(i => i.MessageId == messageId) > 0;
        }
        public bool UpdateImportance(string messageId, int importance) => false;
        public int ForgetGroup(string? groupId, double? retentionHours) => 0;
        public int PruneExpired() => 0;
    }

    // ================= 用例 =================

    [Fact]
    public async Task Erase_MemberOfOthersGroup_LeavesOnlyThatGroup_AndRemovesAccount()
    {
        var h = new Harness();
        var owner = h.AddUser("user_1", "owner");
        var target = h.AddUser("user_2", "victim");
        var bystander = h.AddUser("user_3", "bystander");

        // owner 建的共享群：victim 是普通成员（不拥有）
        var g = await HubFixture.CreateGroupAsync(h.Hub, "共享群", "user_1", "user_2", "user_3");
        // bystander 建的群：victim 也是普通成员
        var g2 = await HubFixture.CreateGroupAsync(h.Hub, "旁观者群", "user_3", "user_2");

        // 语义记忆：victim 在共享群有 2 条、bystander 在共享群有 1 条（不应被删）
        h.Memory.Add("m_v1", g.GroupId, "user_2");
        h.Memory.Add("m_v2", g.GroupId, "user_2");
        h.Memory.Add("m_b1", g.GroupId, "user_3");

        // 发言历史：victim 2 条（含附件）与 bystander 1 条（均保留在共享群）
        h.SeedMessage("m_txt_v1", g.GroupId, "user_2", "受害者的敏感发言内容", attachments: 1);
        h.SeedMessage("m_txt_v2", g.GroupId, "user_2", "另一条要清除的发言");
        h.SeedMessage("m_txt_b1", g.GroupId, "user_3", "旁观者的发言应原样保留");

        var report = await h.Erasure.EraseAsync("user_2", "user_1", "测试注销");

        Assert.True(report.AccountRemoved);
        Assert.Null(h.Users.GetUserById("user_2"));
        Assert.Equal(2, report.MemoriesErased);
        Assert.Contains("m_v1", h.Memory.Deleted);
        Assert.Contains("m_v2", h.Memory.Deleted);
        Assert.DoesNotContain("m_b1", h.Memory.Deleted); // 他人记忆不受影响

        // 现存群中 victim 的发言被匿名化（正文 / 附件清空、昵称占位），bystander 发言原样保留
        var victimMsg = Assert.Single(h.Store.AllMessages(g.GroupId), m => m.MessageId == "m_txt_v1");
        Assert.Equal("", victimMsg.Content);
        Assert.Equal(AccountErasureService.DeletedAccountNickname, victimMsg.SenderNickname);
        Assert.Empty(victimMsg.Attachments);
        Assert.Equal("", h.Store.AllMessages(g.GroupId).First(m => m.MessageId == "m_txt_v2").Content);
        Assert.Equal(2, report.MessagesAnonymized);
        var bystanderMsg = h.Store.AllMessages(g.GroupId).First(m => m.MessageId == "m_txt_b1");
        Assert.Equal("旁观者的发言应原样保留", bystanderMsg.Content);
        Assert.Equal("user_3", bystanderMsg.SenderNickname);

        // 共享群保留：群主仍是 owner、bystander 仍在，victim 已退群
        Assert.NotNull(h.Store.GetGroup(g.GroupId));
        Assert.Equal("user_1", h.Store.GetGroup(g.GroupId)!.OwnerId);
        Assert.True(h.Store.IsMember(g.GroupId, "user_1"));
        Assert.True(h.Store.IsMember(g.GroupId, "user_3"));
        Assert.False(h.Store.IsMember(g.GroupId, "user_2"));
        Assert.NotNull(h.Store.GetGroup(g2.GroupId));

        // 其他账号完好
        Assert.NotNull(h.Users.GetUserById("user_1"));
        Assert.NotNull(h.Users.GetUserById("user_3"));
    }

    [Fact]
    public async Task Erase_OwnerOfGroup_WithOtherUserMembers_TransfersThenLeaves()
    {
        var h = new Harness();
        h.AddUser("user_1", "owner_of_victim_group");
        var target = h.AddUser("user_2", "victim");
        h.AddUser("user_3", "heir");

        // victim 拥有的群：含另一用户成员 heir
        var g = await HubFixture.CreateGroupAsync(h.Hub, "受害者的群", "user_2", "user_3");

        var report = await h.Erasure.EraseAsync("user_2", "user_1", "管理员删除");

        Assert.True(report.AccountRemoved);
        // 群保留且群主已转让给 heir；victim 不再是成员
        var group = h.Store.GetGroup(g.GroupId);
        Assert.NotNull(group);
        Assert.Equal("user_3", group!.OwnerId);
        Assert.Equal(GroupRole.Owner, h.Store.GetMember(g.GroupId, "user_3")!.Role);
        Assert.False(h.Store.IsMember(g.GroupId, "user_2"));
        // 群仍可用（未进 _disbanded 墓碑）
        Assert.True(h.Store.IsMember(g.GroupId, "user_3"));
    }

    [Fact]
    public async Task Erase_OwnerOfAgentsOnlyGroup_DisbandsGroup()
    {
        var h = new Harness();
        h.AddUser("user_1", "admin");
        var target = h.AddUser("user_2", "victim");

        // victim 拥有的群：仅智能体成员 → 无可转让用户成员，整群解散
        var g = await HubFixture.CreateGroupAsync(h.Hub, "纯智能体群", "user_2", "agent_loop");
        h.Memory.Add("m_in_agents_group", g.GroupId, "user_2");

        var report = await h.Erasure.EraseAsync("user_2", "user_1", "");

        Assert.True(report.AccountRemoved);
        Assert.Null(h.Store.GetGroup(g.GroupId)); // 群与全部消息 / 成员已删除
        Assert.Empty(h.Store.GroupsOf("user_2"));
        // 解散时已连带清掉该群记忆（GroupHub RemoveGroup），服务级记忆清理不再重复计数
        Assert.Contains(g.GroupId, h.Memory.RemovedGroups);
    }

    [Fact]
    public async Task Erase_OwnedKnowledgeBases_AreRemoved_OthersKept()
    {
        var h = new Harness();
        h.AddUser("user_1", "admin");
        var target = h.AddUser("user_2", "victim");
        h.AddUser("user_3", "bystander");

        var mine = h.Kbs.CreateKb("受害者知识库", "desc", "user_2");
        var others = h.Kbs.CreateKb("他人知识库", "desc", "user_3");
        var system = h.Kbs.CreateKb("系统知识库", "desc", null);

        var report = await h.Erasure.EraseAsync("user_2", "user_1", "");

        Assert.Equal(1, report.KnowledgeBasesRemoved);
        Assert.Null(h.Kbs.GetKb(mine.KbId));      // 本人创建 → 删除
        Assert.NotNull(h.Kbs.GetKb(others.KbId)); // 他人创建 → 保留
        Assert.NotNull(h.Kbs.GetKb(system.KbId)); // 系统级 → 保留
    }

    [Fact]
    public async Task Erase_LastSuperAdmin_IsRejected()
    {
        var h = new Harness();
        h.AddUser("root", "root", PlatformRole.SuperAdmin);

        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() => h.Erasure.EraseAsync("root", "root", ""));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, ex.ErrorCode);
        Assert.NotNull(h.Users.GetUserById("root")); // 账号保留
    }

    [Fact]
    public async Task Erase_RemovesTotpAndSessions_AndRecordsAudit()
    {
        var h = new Harness();
        h.AddUser("user_1", "admin");
        h.AddUser("user_2", "victim");

        h.Totp.Enroll("user_2");
        h.Auth.Login("victim", "secret123"); // 签发一个会话
        Assert.NotEmpty(h.Auth.SnapshotSessions());

        var report = await h.Erasure.EraseAsync("user_2", "user_1", "合规测试");

        Assert.True(report.AccountRemoved);
        Assert.False(h.Totp.IsEnabled("user_2"));               // TOTP 密钥清除
        Assert.Empty(h.Auth.SnapshotSessions());                // 全部会话吊销
        Assert.Equal("user.account.delete", h.Audit.Query(1).Single().Action);
    }
}
