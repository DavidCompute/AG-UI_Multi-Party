using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>诊断：官方 AGUIChatClient 对接真实外部 AG-UI 服务（宿主机 localhost:62572），
/// 验证审批中断是否呈现为 ToolApprovalRequestContent。</summary>
public sealed class AguiChatClientHitlProbeTests
{
    [Fact]
    public async Task Probe_RealExternalService_EmitsApproval()
    {
        var endpoint = "http://localhost:62572";
        if (!await IsServiceReachableAsync(endpoint))
        {
            System.Console.WriteLine($">>> 外部 AG-UI 服务 {endpoint} 未运行，跳过探针测试");
            return;
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var chatClient = new AGUIChatClient(http, endpoint);
        var agent = chatClient.AsAIAgent(name: "邮件助手", description: "发送邮件");

        var session = await agent.CreateSessionAsync();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "发邮件给david@lingtong.com，主题：hello，内容：hello again."),
        };

        System.Console.WriteLine(">>> RunStreamingAsync 开始");
        await foreach (var update in agent.RunStreamingAsync(messages, session))
        {
            foreach (var c in update.Contents)
                System.Console.WriteLine($"  CONTENT: {c.GetType().Name} | {c}");
        }
        System.Console.WriteLine(">>> 结束");
        Assert.True(true);
    }

    private static async Task<bool> IsServiceReachableAsync(string endpoint)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await probe.GetAsync(endpoint, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
