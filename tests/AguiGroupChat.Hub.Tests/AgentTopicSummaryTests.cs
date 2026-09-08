using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>话题滚动小结（#1 长话题接续记忆）：存储 / 生成器 / 网关触发链路测试。</summary>
public sealed class AgentTopicSummaryTests
{
    // ---- S1 存储 ----
    [Fact]
    public void Store_RoundTrip_And_ConcurrencyGuard()
    {
        var store = new TopicSummaryStore();
        Assert.Null(store.Get("g1", "main"));

        store.Put(new TopicSummaryRecord { GroupId = "g1", TopicId = "main", Summary = "A", WatermarkMessageId = "m10", UpdatedAtMs = 1, MessageCount = 10 });
        var rec = store.Get("g1", "main");
        Assert.NotNull(rec);
        Assert.Equal("A", rec!.Summary);
        Assert.Equal("m10", rec.WatermarkMessageId);

        Assert.True(store.TryBeginGenerate("g1", "main")); // 同话题占用
        Assert.False(store.TryBeginGenerate("g1", "main")); // 已在生成 → 拒绝重入
        Assert.True(store.TryBeginGenerate("g2", "main")); // 不同话题不受影响
        store.EndGenerate("g1", "main");
        Assert.True(store.TryBeginGenerate("g1", "main"));
        store.EndGenerate("g1", "main");
        store.EndGenerate("g2", "main");

        // 快照 / 恢复
        var snap = store.Snapshot();
        Assert.Single(snap);
        var store2 = new TopicSummaryStore();
        store2.Restore(snap);
        Assert.Equal("A", store2.Get("g1", "main")!.Summary);
    }

    // ---- S2 生成器（mock 确定性）----
    [Fact]
    public async Task Generator_Mock_ProducesDeterministicTemplate()
    {
        var options = new AgentOptions { Provider = "mock" };
        var messages = new List<(string, string)>
        {
            ("张三", "第一件事：确认 V2 方案"),
            ("李四", "第二件事：周五前给出排期"),
        };
        var text = await TopicSummaryGenerator.GenerateAsync(options, null, messages, NullLogger.Instance, CancellationToken.None);
        Assert.NotNull(text);
        Assert.Contains("本次对话共 2 条", text);
        Assert.Contains("第一件事", text);

        // 带此前小结：承接字样出现
        var again = await TopicSummaryGenerator.GenerateAsync(options, text, [("王五", "收尾事项")], NullLogger.Instance, CancellationToken.None);
        Assert.Contains("承接此前小结", again);
        Assert.Contains("共 1 条", again);
    }

    [Fact]
    public void Generator_Transcript_CapsCharacters()
    {
        var longText = new string('长', 300);
        var messages = new List<(string, string)> { ("张三", longText) };
        var transcript = TopicSummaryGenerator.BuildTranscript(messages);
        Assert.Contains("…", transcript); // 单条截断到 MaxLineChars
        Assert.True(transcript.Length < 300);
    }

    // ---- S3 网关触发链路：同一话题新增消息 ≥ 阈值后，下一次本地模型回复自动生成话题小结 ----
    [Fact]
    public async Task Gateway_AutoGeneratesTopicSummary_WhenThresholdCrossed()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g", OwnerId = "user_1", MemberIds = ["agent_a"],
            Members =
            [
                new MemberSeed { MemberId = "user_1", Nickname = "张三" },
                new MemberSeed { MemberId = "agent_a", MemberType = MemberType.Agent, Nickname = "测试助手" },
            ],
        });

        var options = new AgentOptions
        {
            Provider = "mock",
            TopicSummaryTriggerCount = 5, // 压小阈值便于测试
            Agents =
            [
                new AgentDefinition { AgentId = "agent_a", Nickname = "测试助手", Description = "测试", Instructions = "你是测试助手", TriggerMode = AgentTriggerMode.Mentioned },
            ],
        };
        var store = new TopicSummaryStore();
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).AddSingleton(store).BuildServiceProvider();
        var gateway = new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);

        for (var i = 1; i <= 6; i++)
        {
            await f.Hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = $"第 {i} 条讨论内容" });
        }
        Assert.Null(store.Get(group.GroupId, "main")); // 尚未触发数字员工前无小结

        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_a", "测试助手", "msg_trig",
            "user_1", "继续刚才的讨论，请总结", [], false, TriggerMode: AgentTriggerMode.Mentioned), CancellationToken.None);
        Assert.True(result.Accepted, "网关运行失败: " + result.ErrorCode);

        var summary = store.Get(group.GroupId, "main");
        Assert.NotNull(summary);
        Assert.Contains("本次对话共 6 条", summary!.Summary);
        Assert.NotNull(summary.WatermarkMessageId);
        Assert.Equal(6, summary.MessageCount);

        // 再次触发但新增未达阈值：不重复生成，仍能回读既有小结
        await f.Hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "追问一句" });
        var result2 = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_a", "测试助手", "msg_trig2",
            "user_1", "再追问", [], false, TriggerMode: AgentTriggerMode.Mentioned), CancellationToken.None);
        Assert.True(result2.Accepted, "网关第二次运行失败: " + result2.ErrorCode);
        Assert.Equal(6, store.Get(group.GroupId, "main")!.MessageCount); // 阈值未到，不推进
    }
}
