using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

public sealed class AgentTriggerTests
{
    [Fact]
    public void Mentioned_TriggersOnlyWhenMentionedOrMentionAll()
    {
        var registry = new AgentRegistry();
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_prd",
            Nickname = "需求助手",
            GroupIds = ["group_1"],
            TriggerMode = AgentTriggerMode.Mentioned,
        });
        var service = new AgentTriggerService(registry);

        var msg = new GroupMessage
        {
            MessageId = "msg_1",
            GroupId = "group_1",
            ThreadId = "thread_group_1",
            SenderId = "user_1",
            SenderType = MemberType.User,
            SenderNickname = "张三",
            Content = "帮我生成大纲",
            Mentions = ["agent_prd"],
            Timestamp = 1,
        };
        var hits = service.Evaluate(msg);
        Assert.Equal("agent_prd", Assert.Single(hits).AgentId);

        // 未提及则不触发
        msg = msg with { MessageId = "msg_2", Mentions = [] };
        Assert.Empty(service.Evaluate(msg));

        // mentionAll 触发
        msg = msg with { MessageId = "msg_3", MentionAll = true };
        Assert.Single(service.Evaluate(msg));
    }

    [Fact]
    public void AllMessages_AlwaysTriggersExceptSelf()
    {
        var registry = new AgentRegistry();
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_listener",
            Nickname = "全量监听",
            GroupIds = ["group_1"],
            TriggerMode = AgentTriggerMode.AllMessages,
        });
        var service = new AgentTriggerService(registry);

        var msg = new GroupMessage
        {
            MessageId = "msg_1",
            GroupId = "group_1",
            ThreadId = "t",
            SenderId = "user_1",
            SenderType = MemberType.User,
            SenderNickname = "u",
            Content = "任意消息",
            Timestamp = 1,
        };
        Assert.Single(service.Evaluate(msg));

        // 智能体自己发的消息不触发自己
        msg = msg with { SenderId = "agent_listener" };
        Assert.Empty(service.Evaluate(msg));
    }

    [Fact]
    public void Keyword_TriggersOnCaseInsensitiveHit()
    {
        var registry = new AgentRegistry();
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_code",
            Nickname = "代码助手",
            GroupIds = ["group_1"],
            TriggerMode = AgentTriggerMode.Keyword,
            Keywords = ["代码", "实现"],
        });
        var service = new AgentTriggerService(registry);

        var msg = new GroupMessage
        {
            MessageId = "msg_1",
            GroupId = "group_1",
            ThreadId = "t",
            SenderId = "user_1",
            SenderType = MemberType.User,
            SenderNickname = "u",
            Content = "这段代码怎么实现？",
            Timestamp = 1,
        };
        Assert.Single(service.Evaluate(msg));

        msg = msg with { Content = "随便聊聊" };
        Assert.Empty(service.Evaluate(msg));
    }

    [Fact]
    public void KeywordAgent_AtMentioned_TriggersRegardlessOfKeywords()
    {
        var registry = new AgentRegistry();
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_kw",
            Nickname = "关键词助手",
            GroupIds = ["group_1"],
            TriggerMode = AgentTriggerMode.Keyword,
            Keywords = ["报表"],
        });
        var service = new AgentTriggerService(registry);

        var msg = new GroupMessage
        {
            MessageId = "msg_1",
            GroupId = "group_1",
            ThreadId = "t",
            SenderId = "user_1",
            SenderType = MemberType.User,
            SenderNickname = "u",
            Content = "帮我看看这个群聊", // 未命中关键词，但被 @ → 必触发
            Mentions = ["agent_kw"],
            Timestamp = 1,
        };
        Assert.Single(service.Evaluate(msg));

        // @全体同样强制触发（不受触发模式限制）
        msg = msg with { Mentions = [], MentionAll = true };
        Assert.Single(service.Evaluate(msg));

        // 既未 @ 也未命中关键词 → 不触发
        msg = msg with { Mentions = [], MentionAll = false };
        Assert.Empty(service.Evaluate(msg));
    }

    [Fact]
    public void MentionAll_ForcesTrigger_OfAllRegisteredAgents()
    {
        var registry = new AgentRegistry();
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_kw",
            Nickname = "关键词助手",
            GroupIds = ["group_1"],
            TriggerMode = AgentTriggerMode.Keyword,
            Keywords = ["报表"],
        });
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_mentioned",
            Nickname = "提及助手",
            GroupIds = ["group_1"],
            TriggerMode = AgentTriggerMode.Mentioned,
        });
        var service = new AgentTriggerService(registry);

        var msg = new GroupMessage
        {
            MessageId = "msg_1",
            GroupId = "group_1",
            ThreadId = "t",
            SenderId = "user_1",
            SenderType = MemberType.User,
            SenderNickname = "u",
            Content = "没有任何关键词的普通消息",
            MentionAll = true,
            Timestamp = 1,
        };
        Assert.Equal(2, service.Evaluate(msg).Count);
    }

    [Fact]
    public void Contextual_AlwaysEvaluatedExceptSelf()
    {
        var registry = new AgentRegistry();
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_ctx",
            Nickname = "语境助手",
            GroupIds = ["group_1"],
            TriggerMode = AgentTriggerMode.Contextual,
        });
        var service = new AgentTriggerService(registry);

        // 无需 @、无需关键词，任意消息都进入评估（是否发言由网关按语境决定）
        var msg = new GroupMessage
        {
            MessageId = "msg_1",
            GroupId = "group_1",
            ThreadId = "t",
            SenderId = "user_1",
            SenderType = MemberType.User,
            SenderNickname = "u",
            Content = "随便聊聊",
            Timestamp = 1,
        };
        Assert.Single(service.Evaluate(msg));

        // 智能体自己发的消息不触发自己
        msg = msg with { SenderId = "agent_ctx" };
        Assert.Empty(service.Evaluate(msg));
    }

    [Fact]
    public void Evaluate_ScopesToGroup()
    {
        var registry = new AgentRegistry();
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_a",
            Nickname = "a",
            GroupIds = ["group_1"],
            TriggerMode = AgentTriggerMode.AllMessages,
        });
        var service = new AgentTriggerService(registry);

        var msg = new GroupMessage
        {
            MessageId = "msg_1",
            GroupId = "group_2",
            ThreadId = "t",
            SenderId = "user_1",
            SenderType = MemberType.User,
            SenderNickname = "u",
            Content = "任意消息",
            Timestamp = 1,
        };
        Assert.Empty(service.Evaluate(msg));
    }

    [Fact]
    public void SameAgent_CanHaveDifferentTriggerModePerGroup()
    {
        var registry = new AgentRegistry();
        // 群 1：跟随角色默认（提及触发，未覆盖）；群 2：群内显式覆盖为全量监听
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_a",
            Nickname = "a",
            GroupIds = ["group_1"],
            TriggerMode = AgentTriggerMode.Mentioned,
        });
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_a",
            Nickname = "a",
            GroupIds = ["group_2"],
            TriggerMode = AgentTriggerMode.AllMessages,
            Override = true,
        });
        var service = new AgentTriggerService(registry);

        var msg = new GroupMessage
        {
            MessageId = "msg_1",
            GroupId = "group_1",
            ThreadId = "t",
            SenderId = "user_1",
            SenderType = MemberType.User,
            SenderNickname = "u",
            Content = "任意消息",
            Mentions = [],
            Timestamp = 1,
        };
        // 群 1 按提及触发：未提及 → 不触发
        Assert.Empty(service.Evaluate(msg));

        // 群 2 按全量监听：同一条消息 → 触发
        Assert.Single(service.Evaluate(msg with { GroupId = "group_2", MessageId = "msg_2" }));

        // 覆盖标记随注册保存，供角色编辑时区分「群内覆盖」与「跟随默认」
        Assert.False(registry.ForGroupAgent("group_1", "agent_a")!.IsOverridden);
        Assert.True(registry.ForGroupAgent("group_2", "agent_a")!.IsOverridden);
    }
}
