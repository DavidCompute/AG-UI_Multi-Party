using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>消息 👍/👎 反馈（#2 偏好画像）：存储 / 提示生成器 / 网关注入链路测试。</summary>
public sealed class AgentFeedbackTests
{
    private static MessageFeedbackEntry Entry(string messageId, string groupId, string agentId, string userId,
        int value, long createdAtMs, params string[] tags)
        => new()
        {
            MessageId = messageId,
            GroupId = groupId,
            AgentId = agentId,
            UserId = userId,
            Value = value,
            Tags = tags,
            CreatedAtMs = createdAtMs,
        };

    // ================= 存储层 =================

    [Fact]
    public void Store_ByUser_OrdersDescending_And_OneEntryPerMessagePerUser()
    {
        var store = new MessageFeedbackStore();
        store.Put(Entry("m1", "g1", "a1", "u1", 1, createdAtMs: 100));
        store.Put(Entry("m2", "g1", "a1", "u1", -1, createdAtMs: 300));
        store.Put(Entry("m2", "g1", "a1", "u2", -1, createdAtMs: 200)); // 其它用户同一条消息，互不覆盖

        var mine = store.ByUser("u1");
        Assert.Equal(2, mine.Count); // 只含 u1 本人两条
        Assert.Equal(300, mine[0].CreatedAtMs); // 倒序
        Assert.Equal(100, mine[1].CreatedAtMs);

        // 同一用户对同一消息重复提交 → 覆盖（一人一条，不产生第二条）
        store.Put(Entry("m1", "g1", "a1", "u1", -1, createdAtMs: 400, "太啰嗦"));
        Assert.Single(store.ByUser("u1"), e => e.MessageId == "m1");
        Assert.Equal(-1, store.Get("m1", "u1")!.Value);
        Assert.Equal("太啰嗦", store.Get("m1", "u1")!.Tags.Single());
        // 其它用户对该消息的评价保持独立
        Assert.Null(store.Get("m1", "u2"));              // u2 未评过 m1
        Assert.Equal(-1, store.Get("m2", "u2")!.Value); // u2 对 m2 的评价不受 u1 覆盖影响
    }

    [Fact]
    public void Store_SnapshotRestore_RoundTrip()
    {
        var store = new MessageFeedbackStore();
        store.Put(Entry("m1", "g1", "a1", "u1", -1, createdAtMs: 100, "太啰嗦", "缺依据"));
        store.Put(Entry("m2", "g1", "a1", "u2", 1, createdAtMs: 200));

        var restored = new MessageFeedbackStore();
        restored.Restore(store.Snapshot());
        Assert.Equal(2, restored.Snapshot().Count);
        Assert.Equal(-1, restored.Get("m1", "u1")!.Value);
        Assert.Equal(["太啰嗦", "缺依据"], restored.Get("m1", "u1")!.Tags);
        Assert.Equal(1, restored.Get("m2", "u2")!.Value);

        // 恢复会清空旧数据（幂等重启语义）
        restored.Restore([]);
        Assert.Empty(restored.Snapshot());
    }

    // ================= BuildFeedbackHint（纯函数） =================

    [Fact]
    public void Hint_IsNull_WhenNoNegativeOrOutOfScopeOrTooOld()
    {
        const long now = 1_000_000_000_000;
        Assert.Null(AgentGateway.BuildFeedbackHint([], "a", "g", now));

        // 👍 不计入负面偏好
        Assert.Null(AgentGateway.BuildFeedbackHint([Entry("m1", "g", "a", "u", 1, now)], "a", "g", now));

        // 别的数字员工 / 别的群不命中
        Assert.Null(AgentGateway.BuildFeedbackHint([Entry("m1", "g", "a", "u", -1, now)], "a_other", "g_other", now));

        // 30 天窗口外
        Assert.Null(AgentGateway.BuildFeedbackHint([Entry("m1", "g", "a", "u", -1, now - 31L * 24 * 3600 * 1000)], "a", "g", now));
    }

    [Fact]
    public void Hint_AggregatesTopTags_AndTruncatesSnippets()
    {
        const long now = 1_000_000_000_000;
        // 两条同标签 👎 → 标签按次数聚合出现一次；不同群但同数字员工命中
        var entries = new List<MessageFeedbackEntry>
        {
            Entry("m1", "g1", "a", "u", -1, now - 1, "太啰嗦"),
            Entry("m2", "g2", "a", "u", -1, now - 2, "太啰嗦", "缺依据"),
            Entry("m3", "g1", "a", "u", -1, now - 3, "缺依据"),
        };
        var hint = AgentGateway.BuildFeedbackHint(entries, "a", "gx", now);
        Assert.NotNull(hint);
        Assert.Contains("太啰嗦、缺依据", hint);          // 计数并列时按首现顺序聚合
        Assert.DoesNotContain("1 条回复点了 👎", hint); // 有标签时走标签汇总行，不走计数行
    }

    [Fact]
    public void Hint_NoTags_UsesCountLine_AndBindsToGroupOrAgent()
    {
        const long now = 1_000_000_000_000;
        // 同群（不同数字员工也命中：口径为「同群或同数字员工」）
        var sameGroup = AgentGateway.BuildFeedbackHint([Entry("m1", "g", "a", "u", -1, now)], "other_agent", "g", now);
        Assert.NotNull(sameGroup);
        Assert.Contains("近期对 1 条回复点了 👎", sameGroup); // 无标签 → 计数行
    }

    // ================= 网关注入链路（mock 回显可见） =================

    [Fact]
    public async Task Gateway_InjectsPreferenceHint_OnlyAfterUserDislikes()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "反馈偏好测试",
            OwnerId = "user_1",
            MemberIds = ["agent_a"],
            Members =
            [
                new MemberSeed { MemberId = "user_1", Nickname = "张三" },
                new MemberSeed { MemberId = "agent_a", MemberType = MemberType.Agent, Nickname = "测试助手" },
            ],
        });

        var options = new AgentOptions
        {
            Provider = "mock",
            Agents =
            [
                new AgentDefinition { AgentId = "agent_a", Nickname = "测试助手", Description = "测试", Instructions = "你是测试助手", TriggerMode = AgentTriggerMode.Mentioned },
            ],
        };
        var store = new MessageFeedbackStore();
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).AddSingleton(store).BuildServiceProvider();
        var gateway = new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);

        async Task<AgentInvocationResult> TriggerAsync(string triggerId, string content)
            => await gateway.InvokeAsync(new AgentInvocationContext(
                GroupId: group.GroupId,
                ThreadId: "thread_" + group.GroupId,
                AgentId: "agent_a",
                AgentNickname: "测试助手",
                TriggerMessageId: triggerId,
                TriggerUserId: "user_1",
                Content: content,
                Mentions: [],
                MentionAll: false,
                TriggerMode: AgentTriggerMode.Mentioned), CancellationToken.None);

        // 1) 首次触发（尚无任何反馈）→ mock 回显的上下文中不应出现偏好提示
        var first = await TriggerAsync("msg_t1", "请帮我起草一份周报框架");
        Assert.True(first.Accepted, "网关第一次运行失败: " + first.ErrorCode);
        var reply1 = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.DoesNotContain("近期对回复的反馈", reply1.Content);

        // 2) 用户对该回复点 👍 → 不构成负面偏好，第二次触发仍不应注入
        store.Put(Entry(reply1.MessageId, group.GroupId, "agent_a", "user_1", 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var second = await TriggerAsync("msg_t2", "再帮我补充一下执行计划");
        Assert.True(second.Accepted, "网关第二次运行失败: " + second.ErrorCode);
        var reply2 = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.DoesNotContain("近期对回复的反馈", reply2.Content);

        // 3) 用户把评价改成 👎（同一条消息覆盖）→ 第三次触发的上下文注入偏好改进提示
        store.Put(Entry(reply1.MessageId, group.GroupId, "agent_a", "user_1", -1,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "太啰嗦"));
        var third = await TriggerAsync("msg_t3", "请按我习惯的风格重新组织");
        Assert.True(third.Accepted, "网关第三次运行失败: " + third.ErrorCode);
        var reply3 = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.Contains("主要反映：太啰嗦", reply3.Content); // 负面标签被聚合为改进提示，随上下文进入模型（mock 回显可见）
    }
}
