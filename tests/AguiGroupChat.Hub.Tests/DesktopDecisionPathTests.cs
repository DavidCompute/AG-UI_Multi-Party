using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 桌面宿主上的「小决策」路径：语境触发的数字员工必须真的按判定<b>发言 / 沉默</b>。
///
/// <para>
/// 为什么在桌面宿主上再验一遍：桌面版是**另一套组合根**（<c>DesktopApp.cs</c>），它自己
/// <c>AddAgentFramework(builder.Configuration)</c> 装配网关与 <c>AgentOptions</c>。两类事只有在这一层才看得出来：
/// </para>
/// <list type="number">
///   <item>后注册者生效 —— 若装配顺序被改动，<c>NoopAgentGateway</c> 会盖住真实网关，数字员工<b>永不回复</b>；</item>
///   <item>配置绑定 —— 判定模型名 / 判定阈值这些新增项必须真的从桌面配置绑到宿主用的那个 <c>AgentOptions</c> 单例上，
///         否则「思考模式一开、判定又走推理模型」这个已修的缺陷会在桌面端悄悄复活。</item>
/// </list>
///
/// <para>
/// 本用例集只跑 <c>Provider=mock</c>（桌面默认值），因此不会打到任何真实模型端点；若将来给桌面加了
/// appsettings.json 把 provider 改成真实端点，<see cref="DesktopHost_BindsTheDecisionOptions"/> 会直接红，
/// 避免 CI 悄悄产生计费调用。
/// </para>
/// </summary>
[Collection(DesktopHostCollection.Name)]
public sealed class DesktopDecisionPathTests
{
    private readonly DesktopCompositionServerFixture _fixture;

    public DesktopDecisionPathTests(DesktopCompositionServerFixture fixture) => _fixture = fixture;

    private T Get<T>() where T : notnull => _fixture.App.Services.GetRequiredService<T>();

    /// <summary>本用例集专用 agentId：起局部临时群，不碰宿主里已有的数据。</summary>
    private const string DecisionAgentId = "agent_desktop_decide";

    /// <summary>只在 Contextual 触发下才会走到的判定提示：内容里不能出现 <c>@</c>（被 @ 会按“提及”语义跳过判定）。</summary>
    private const string Irrelevant = "周末约一下打球吧，随便聊聊。";

    private const string Addressed = "帮我整理一份产品介绍，谢谢。";

    [Fact]
    public void DesktopHost_UsesTheRealGateway_NotTheNoop()
    {
        // AddAgentFramework 必须在 HubApp.ConfigureServices 之后注册（后注册者生效），
        // 否则落到 NoopAgentGateway —— 桌面端所有数字员工都不回复，且没有任何报错。
        Assert.IsType<AgentGateway>(Get<IAgentGateway>());
    }

    [Fact]
    public void DesktopHost_BindsTheDecisionOptions()
    {
        var options = Get<AgentOptions>();

        Assert.True(string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase),
            $"桌面宿主的 Agents:Provider 应为 mock（本用例集不得调用真实模型端点），实际为 {options.Provider}");
        // 阈值默认 0.3，且必须是可用的概率：0/1 会让判定退化成“恒沉默 / 恒发言”。
        Assert.InRange(options.DecisionMinProbability, 0.05, 0.95);
    }

    /// <summary>
    /// 按桌面 appsettings.json 的形状（<c>deepseek</c> + 思考模式默认开）绑定一次配置，验证：
    /// ① 新增的判定项真的能从 <c>Agents</c> 节绑上（键名就是对外承诺的契约）；
    /// ② 思考模式开启时，正式回复走推理模型而<b>小决策仍走非推理模型</b>。
    /// 后者是已修缺陷（判定正文为空 → 静默失效）的根因，必须在桌面这套配置形状下也不复发。
    /// </summary>
    [Fact]
    public void DesktopShapedConfig_KeepsDecisionsOffTheReasoningModel()
    {
        var bound = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agents:Provider"] = "deepseek",
            ["Agents:Model"] = "deepseek-chat",
            ["Agents:ThinkingMode"] = "true",
        }).Build().GetSection("Agents").Get<AgentOptions>()!;
        var def = new AgentDefinition { AgentId = DecisionAgentId, Nickname = "桌面判定测试" };

        Assert.True(bound.ThinkingMode);
        // 正式回复：思考模式开启 → 推理模型（这两个名字是 AgentCatalog 里文档化的 DeepSeek 组合）
        Assert.Equal("deepseek-flash", AgentCatalog.ResolveModelName(bound, def, isDeepSeek: true));
        // 小决策：必须仍是非推理的常规模型
        Assert.Equal("deepseek-chat", AgentCatalog.ResolveDecisionModelName(bound, def, isDeepSeek: true));

        // 两个新旋钮可被配置覆盖（键名 = 对外契约：AGENTS_DECISION_MODEL / AGENTS_DECISION_MIN_PROBABILITY 同源）
        var tuned = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agents:DecisionModel"] = "my-router",
            ["Agents:DecisionMinProbability"] = "0.42",
        }).Build().GetSection("Agents").Get<AgentOptions>()!;
        Assert.Equal("my-router", AgentCatalog.ResolveDecisionModelName(tuned, def, isDeepSeek: true));
        Assert.Equal(0.42, tuned.DecisionMinProbability, 3);
    }

    [Fact]
    public async Task DesktopHost_ContextualDecision_SpeaksOnlyWhenAddressed()
    {
        var options = Get<AgentOptions>();
        Assert.True(string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase),
            "该用例需要 mock 提供方（判定输出确定），桌面宿主当前配置不是 mock");

        var hub = Get<GroupHub>();
        var catalog = Get<AgentCatalog>();
        var gateway = Get<AgentGateway>();

        // 语境触发（不 @ 也会被唤起），判定由 ShouldSpeakAsync 决定
        catalog.Upsert(new AgentDefinition
        {
            AgentId = DecisionAgentId,
            Nickname = "桌面判定测试",
            Description = "桌面判定路径回归用",
            Instructions = "输出：桌面判定测试答复",
            TriggerMode = AgentTriggerMode.Contextual,
        });
        var group = await hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "桌面判定-临时",
            OwnerId = "user_desktop_decide",
            MemberIds = [DecisionAgentId],
            Members = [new MemberSeed { MemberId = DecisionAgentId, MemberType = MemberType.Agent, Nickname = "桌面判定测试" }],
        });

        async Task<AgentInvocationResult> InvokeAsync(string content) => await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId, ThreadId: "thread_" + group.GroupId,
            AgentId: DecisionAgentId, AgentNickname: "桌面判定测试",
            TriggerMessageId: "msg_" + Guid.NewGuid().ToString("N")[..8],
            TriggerUserId: "user_desktop_decide", Content: content, Mentions: [], MentionAll: false,
            TriggerMode: AgentTriggerMode.Contextual), CancellationToken.None);

        // 与职责无关 → 判 NO → 静默（不发任何事件，也不建消息）
        var silent = await InvokeAsync(Irrelevant);
        Assert.False(silent.Accepted);
        Assert.Equal("AGENT_DECIDED_SILENT", silent.ErrorCode);

        // 明确求助 → 判 YES → 真正走完一轮并产出回复
        var spoke = await InvokeAsync(Addressed);
        Assert.True(spoke.Accepted, "判定应发言却未发言：" + spoke.ErrorCode);
        Assert.NotEqual("AGENT_DECIDED_SILENT", spoke.ErrorCode);
    }
}
