using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 回归：**带图的消息也必须保留工具**（否则技能永远不会被调用）。
///
/// <para>
/// 实测缺陷：「本轮带图 → 换视觉模型」这一步曾经把 agent 换成 <c>CreateBareVision</c>
/// （<c>Tools = null</c>、无记忆注入）。于是发往模型的消息里根本没有 <c>tools</c>，
/// 而 DeepSeek 这类模型**不会报错**——它会把工具调用<b>当正文写出来</b>（DSML 标记：
/// tool_calls / invoke / parameter 一整套）。后果：
/// </para>
/// <list type="bullet">
///   <item>技能从未被调用，交付兑底两轮都产不出文件，用户既拿不到文件，还收到一大段标记文本；</item>
///   <item>日志里只有一句「交付兑底均未产出文件」，看不出真正原因（实测就是「与 ppt生成助手 的单聊」里
///         “PPT 首页背景换成附件图”那条请求）。</item>
/// </list>
///
/// <para>
/// 真实端点实测（同一提示）：带 <c>tools</c> → 返回结构化 <c>tool_calls</c>；
/// 不带 <c>tools</c> → 正文里出现 DSML 标记。视觉模型本身是支持 tool calling 的（实测返回结构化调用），
/// 所以问题不在模型，而在“换模型时顺手把工具摘了”。
/// </para>
///
/// <para>
/// 这里用「公告」这一<b>需审批</b>的内置工具当探针：mock 客户端只有在挂了工具时才会发出该调用。
/// 因此「带图 → 仍能触发审批中断」正好等价于「带图 → 工具还在」。
/// </para>
/// </summary>
public sealed class VisionTurnToolRetentionTests
{
    [Fact]
    public async Task ImageTurn_KeepsTools_SoTheSkillCanStillBeCalled()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "agui-vis-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var attachmentStore = new AttachmentStore(tmp);
            // 假 PNG 足够：TryReadImageBytes 按扩展名读字节，不校验图像内容
            var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };
            AttachmentInfo shot;
            using (var ms = new MemoryStream(pngBytes))
                shot = attachmentStore.Save("shot.png", "image/png", ms, pngBytes.Length);

            var f = new HubFixture();
            var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
            {
                GroupName = "g",
                OwnerId = "user_1",
                MemberIds = ["agent_vis"],
                Members = [new MemberSeed { MemberId = "agent_vis", MemberType = MemberType.Agent, Nickname = "带图助手" }],
            });
            var (conn, inbox) = f.NewConnection("user_1");
            await f.Hub.SubscribeAsync(conn, [group.GroupId]);
            f.Drain(inbox);

            var options = new AgentOptions
            {
                Provider = "mock",
                EnableTools = true,                 // mock 按关键词模拟工具调用
                VisionModel = "test-vision",        // 显式配视觉 → 才会走「带图换视觉模型」这条分支
                Agents =
                [
                    new AgentDefinition
                    {
                        AgentId = "agent_vis", Nickname = "带图助手", Description = "测试",
                        Instructions = "你是带图助手", TriggerMode = AgentTriggerMode.Mentioned,
                    },
                ],
            };
            var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
            var services = new ServiceCollection().AddSingleton(f.Hub).BuildServiceProvider();
            var gateway = new AgentGateway(catalog, services, options, attachmentStore: attachmentStore, NullLogger<AgentGateway>.Instance);

            var result = await gateway.InvokeAsync(new AgentInvocationContext(
                GroupId: group.GroupId,
                ThreadId: "thread_" + group.GroupId,
                AgentId: "agent_vis",
                AgentNickname: "带图助手",
                TriggerMessageId: "msg_trigger",
                TriggerUserId: "user_1",
                Content: "帮我发布公告：放假通知",
                Mentions: [],
                MentionAll: false,
                Attachments: [shot]), CancellationToken.None);

            // 工具被调用（需审批的 publish_announcement）→ 运行中断并广播交互卡
            Assert.Equal("AGENT_AWAITING_INTERACTION", result.ErrorCode);
            var raw = f.Drain(inbox).FirstOrDefault(e => HubFixture.TypeOf(e) == EventTypes.AgentInteractionRequest);
            Assert.NotNull(raw);
            Assert.Equal("publish_announcement", HubFixture.Parse(raw!).GetProperty("toolName").GetString());
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* 临时目录清理失败不影响结论 */ }
        }
    }
}
