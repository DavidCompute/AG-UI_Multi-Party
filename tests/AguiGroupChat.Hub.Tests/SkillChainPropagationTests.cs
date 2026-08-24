using AguiGroupChat.Agents;
using AguiGroupChat.Agents.Tools;
using AguiGroupChat.Hub.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 多跳技能结果回传测试：技能调用链 A→B→C 时，各级智能体的最终回复应向下游回传，
/// 顶层（万事通）最终回复应包含最底层（员工手册解读专家）检索到的结论。
/// </summary>
public sealed class SkillChainPropagationTests
{
    private static IServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    /// <summary>模型：收到含 FunctionResult 的消息 → 产出引用该结果的最终答复文本；否则调用指定技能。</summary>
    private sealed class SkillCallingClient : IChatClient
    {
        private readonly string _skillName;
        public SkillCallingClient(string skillName) => _skillName = skillName;
        public object? GetService(Type t, object? k = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> msgs, ChatOptions? o = null, CancellationToken ct = default)
        {
            var results = msgs.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
            if (results.Count > 0)
            {
                var last = results[^1].Result?.ToString() ?? "";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    $"汇总：上游专家结论为——“{last}”。我已在答复中采纳。")));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call_" + Guid.NewGuid().ToString("N")[..6], _skillName,
                    new Dictionary<string, object?> { ["query"] = "采购请假（技能内已携带）" }),
            })));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> msgs, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var results = msgs.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
            if (results.Count > 0)
            {
                var last = results[^1].Result?.ToString() ?? "";
                yield return new ChatResponseUpdate(ChatRole.Assistant, $"汇总：上游专家结论为——“{last}”。我已在答复中采纳。");
                yield break;
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call_" + Guid.NewGuid().ToString("N")[..6], _skillName,
                    new Dictionary<string, object?> { ["query"] = "采购请假（技能内已携带）" }),
            });
        }

        public void Dispose() { }
    }

    private static ChatClientAgent PlainAgent(string text)
        => new(new StaticTextClient(text), "指令", name: "n", description: "d", tools: null,
            NullLoggerFactory.Instance, Services());

    private static ChatClientAgent SkillAgent(string skillName, AITool skillTool)
        => new(new SkillCallingClient(skillName), "指令", name: "n", description: "d",
            tools: new AITool[] { skillTool }, NullLoggerFactory.Instance, Services());

    [Fact]
    public async Task NestedSkill_PropagatesLeafResultToTop()
    {
        // 员工手册解读专家（叶子）：直接答复
        var leaf = PlainAgent("「员工手册」规定：男员工陪产假 15 天（含周末）。");
        // hr专员（中层）：技能 → 叶子；模型调用技能后引用其结果
        var leafSkill = AIFunctionFactory.Create(new AgentSkillCall(leaf, "handbook", "员工手册解读专家", "skill_handbook", NullLoggerFactory.Instance).InvokeAsync, "skill_handbook", "解读员工手册");
        var mid = SkillAgent("skill_handbook", leafSkill);
        // 万事通（顶层）：技能 → 中层
        var midSkill = AIFunctionFactory.Create(new AgentSkillCall(mid, "hr", "hr专员", "skill_hr", NullLoggerFactory.Instance).InvokeAsync, "skill_hr", "咨询 HR");
        var top = SkillAgent("skill_hr", midSkill);

        // 以技能方式运行顶层
        var hostCtx = new AgentInvocationContext("g1", "t1", "wst", "万事通", "msg1", "user_1", "陪产假公司怎么规定", [], false);
        var prev = AgentGateway.AmbientContext.Value;
        var prevChain = SkillChainBuilder.Ambient.Value;
        AgentGateway.AmbientContext.Value = hostCtx;
        var chain = new SkillChainBuilder();
        chain.EnsureRoot("wst", "万事通");
        SkillChainBuilder.Ambient.Value = chain; // 链路可视化：技能调用嵌套记录
        try
        {
            var result = await new AgentSkillCall(top, "wst", "万事通", "skill_hr", NullLoggerFactory.Instance).InvokeAsync("陪产假公司怎么规定", CancellationToken.None);
            // 顶层最终答复应引用到最底层叶子的结论（15 天），证明结果逐层回传
            Assert.Contains("15 天", result);
            Assert.Contains("陪产假", result);

            // 链路可视化：多跳应记录三层结构 万事通(skill_hr→hr) →(skill_handbook→手册)
            var json = chain.ToJson();
            Assert.NotNull(json);
            Assert.Contains("skill_hr", json);
            Assert.Contains("skill_handbook", json);
            Assert.Contains("员工手册解读专家", json);
        }
        finally
        {
            AgentGateway.AmbientContext.Value = prev;
            SkillChainBuilder.Ambient.Value = prevChain;
        }
    }

    private sealed class StaticTextClient : IChatClient
    {
        private readonly string _text;
        public StaticTextClient(string text) => _text = text;
        public object? GetService(Type t, object? k = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> msgs, ChatOptions? o = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text)));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> msgs, ChatOptions? o = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, _text);
        }
        public void Dispose() { }
    }
}
