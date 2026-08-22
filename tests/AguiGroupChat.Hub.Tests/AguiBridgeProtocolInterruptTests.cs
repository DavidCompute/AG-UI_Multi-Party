using System.Text.Json;
using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

public sealed class AguiBridgeProtocolInterruptTests
{
    private static AguiBridgeEvent? Parse(string json, bool hub = false, HashSet<string>? accepted = null)
    {
        using var doc = JsonDocument.Parse(json);
        string? selfMessageId = null;
        return AguiBridgeProtocol.Parse(doc, hub, "agent_x", accepted ?? new HashSet<string>(), ref selfMessageId);
    }

    /// <summary>外部真实服务（AGUI.AspNetCore）返回的审批中断结构：无 metadata、responseSchema 用 approved。</summary>
    [Fact]
    public void Parse_RealExternalRunFinishedInterrupt_ProducesInterruptEvent()
    {
        var json = """
        {"type":"RUN_FINISHED","threadId":"t","runId":"r",
         "outcome":{"type":"interrupt","interrupts":[{
            "id":"ficc_call_00_abc","reason":"tool_call",
            "message":"Approval required for tool call: send_email",
            "toolCallId":"call_00_abc",
            "responseSchema":{"type":"object","properties":{"approved":{"type":"boolean"}},"required":["approved"]}
         }]}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("ficc_call_00_abc", evt.InterruptId);
        Assert.Equal("call_00_abc", evt.ToolCallId);
        Assert.Equal("send_email", evt.ToolName); // 从 message 提取（无 metadata 时兜底）
    }

    /// <summary>无中断的 RUN_FINISHED → end。</summary>
    [Fact]
    public void Parse_RunFinishedWithoutInterrupt_ProducesEnd()
    {
        var json = """{"type":"RUN_FINISHED","threadId":"t","runId":"r","outcome":{"type":"success"}}""";
        var evt = Parse(json);
        Assert.Equal("end", evt!.Type);
    }

    /// <summary>standard 方言：TEXT_MESSAGE_END 只是消息结束（后续可能跟 TOOL_CALL_* / RUN_FINISHED 中断），
    /// 不应作为运行终止事件；hub 方言保持 TEXT_MESSAGE_END → end（Hub 无 RUN_FINISHED）。</summary>
    [Fact]
    public void Parse_TextMessageEnd_IgnoredInStandard_EndsInHub()
    {
        var json = """{"type":"TEXT_MESSAGE_END","messageId":"msg_x","threadId":"t"}""";
        Assert.Null(Parse(json, hub: false)); // standard：忽略，等 RUN_FINISHED
        // hub：终止事件（前提消息已登记为外部回复，同真实流 TEXT_MESSAGE_START 先登记）
        Assert.Equal("end", Parse(json, hub: true, accepted: new HashSet<string> { "msg_x" })!.Type);
    }

    /// <summary>回归：AGUI.AspNetCore 真实事件序列 TEXT_MESSAGE_END 后跟 RUN_FINISHED 中断——
    /// standard 方言必须忽略 TEXT_MESSAGE_END，让 RUN_FINISHED 的中断事件成为最终结果。</summary>
    [Fact]
    public void Parse_EndThenRunFinishedInterrupt_StillProducesInterrupt()
    {
        var end = """{"type":"TEXT_MESSAGE_END","messageId":"msg_x","threadId":"t"}""";
        var finished = """
        {"type":"RUN_FINISHED","threadId":"t","runId":"r",
         "outcome":{"type":"interrupt","interrupts":[{
            "id":"ficc_call_1","reason":"tool_call",
            "message":"Approval required for tool call: send_email",
            "toolCallId":"call_1"}]}}
        """;
        Assert.Null(Parse(end, hub: false));
        var evt = Parse(finished, hub: false);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("ficc_call_1", evt.InterruptId);
    }

    /// <summary>standard 方言：REASONING_MESSAGE_CONTENT（外部服务思考过程）→ reasoning 事件，供 💭 过程渲染。</summary>
    [Fact]
    public void Parse_ReasoningMessageContent_ProducesReasoningEvent()
    {
        var json = """{"type":"REASONING_MESSAGE_CONTENT","messageId":"prt_1","role":"reasoning","delta":"我需要先创建文件，再读取验证。"}""";
        var evt = Parse(json, hub: false);
        Assert.NotNull(evt);
        Assert.Equal("reasoning", evt!.Type);
        Assert.Equal("我需要先创建文件，再读取验证。", evt.Delta);
    }

    /// <summary>standard 方言：TOOL_CALL_START（外部工具调用开始）→ tool 事件（toolCallId + toolCallName），供 TOOL_CALL_START 群事件渲染。</summary>
    [Fact]
    public void Parse_ToolCallStart_ProducesToolEvent()
    {
        var json = """{"type":"TOOL_CALL_START","toolCallId":"call_00_abc","toolCallName":"write","parentMessageId":"msg_x"}""";
        var evt = Parse(json, hub: false);
        Assert.NotNull(evt);
        Assert.Equal("tool", evt!.Type);
        Assert.Equal("call_00_abc", evt.ToolCallId);
        Assert.Equal("write", evt.ToolName);
    }

    /// <summary>standard 方言：REASONING_START / RUN_STARTED 等边界事件忽略（无渲染价值）；
    /// TOOL_CALL_END 已被解析为 tool_end（供网关回填分帧累积的参数并广播展示）。</summary>
    [Fact]
    public void Parse_ReasoningStartAndRunStarted_Ignored()
    {
        Assert.Null(Parse("""{"type":"REASONING_START","messageId":"prt_1"}""", hub: false));
        Assert.Null(Parse("""{"type":"RUN_STARTED","threadId":"t","runId":"r"}""", hub: false));
    }

    /// <summary>standard 方言：TOOL_CALL_END → tool_end 事件（TOOL_CALL_ARGS 分帧累积后由网关回填参数广播）。</summary>
    [Fact]
    public void Parse_ToolCallEnd_ProducesToolEndEvent()
    {
        var evt = Parse("""{"type":"TOOL_CALL_END","toolCallId":"call_1"}""", hub: false);
        Assert.NotNull(evt);
        Assert.Equal("tool_end", evt!.Type);
        Assert.Equal("call_1", evt.ToolCallId);
    }
}
