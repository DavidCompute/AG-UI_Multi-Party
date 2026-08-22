using System.Text.Json;
using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// AG-UI 桥接协议事件覆盖：附件（ATTACHMENT_STARTED / START 附件数组）、独立中断（INTERRUPT_STARTED）、
/// 动作（ACTION_STARTED）、hub 方言工具调用与附件。确保外部 AG-UI 服务的各类事件都能被接收并转成统一事件流。
/// </summary>
public sealed class AguiBridgeProtocolCoverageTests
{
    private static AguiBridgeEvent? Parse(string json, bool hub = false, HashSet<string>? accepted = null)
    {
        using var doc = JsonDocument.Parse(json);
        string? selfMessageId = null;
        return AguiBridgeProtocol.Parse(doc, hub, "agent_x", accepted ?? new HashSet<string>(), ref selfMessageId);
    }

    /// <summary>ATTACHMENT_STARTED（source.url 型）：产出附件事件（kind 按 contentType 识别）。</summary>
    [Fact]
    public void Parse_AttachmentStarted_WithSourceUrl_ProducesAttachment()
    {
        var json = """
        {"type":"ATTACHMENT_STARTED","id":"att_1","name":"图表.png","contentType":"image/png",
         "source":{"type":"url","url":"https://ext.example.com/files/chart.png"}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("attachment", evt!.Type);
        var att = Assert.Single(evt.Attachments!);
        Assert.Equal("图表.png", att.Name);
        Assert.Equal("https://ext.example.com/files/chart.png", att.Url);
        Assert.Equal("image", att.Kind);
    }

    /// <summary>ATTACHMENT_STARTED 无 url（base64 内容流）→ att_start 占位事件（客户端累积，见累积器测试），不再静默忽略。</summary>
    [Fact]
    public void Parse_AttachmentStarted_WithoutUrl_ProducesAttStart()
    {
        var json = """{"type":"ATTACHMENT_STARTED","id":"att_1","name":"a.png","contentType":"image/png","source":{"type":"base64"}}""";
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("att_start", evt!.Type);
        Assert.Equal("att_1", evt.ToolCallId);
    }

    /// <summary>standard 方言 TEXT_MESSAGE_START 携带附件数组（AGUI 2.x）：批量产出附件事件。</summary>
    [Fact]
    public void Parse_StandardStart_WithAttachments_ProducesBatch()
    {
        var json = """
        {"type":"TEXT_MESSAGE_START","messageId":"msg_1","role":"assistant",
         "attachments":[
            {"id":"a1","name":"报告.pdf","contentType":"application/pdf","source":{"type":"url","url":"https://ext.example.com/r.pdf"}},
            {"id":"a2","name":"封面.png","contentType":"image/png","url":"https://ext.example.com/c.png"}
         ]}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("attachment", evt!.Type);
        Assert.Equal(2, evt.Attachments!.Count);
        Assert.Contains(evt.Attachments, a => a.Kind == "file" && a.Url.EndsWith("r.pdf"));
        Assert.Contains(evt.Attachments, a => a.Kind == "image" && a.Url.EndsWith("c.png"));
    }

    /// <summary>独立中断事件（AGUI 2.x INTERRUPT_STARTED，interrupt 在 payload 内）：产出 interrupt。</summary>
    [Fact]
    public void Parse_InterruptStarted_PayloadInterrupt_ProducesInterrupt()
    {
        var json = """
        {"type":"INTERRUPT_STARTED","threadId":"t","runId":"r",
         "payload":{"interrupt":{"id":"int_9","reason":"tool_call","message":"Approval required for tool call: send_email","toolCallId":"call_9"}}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("int_9", evt.InterruptId);
        Assert.Equal("call_9", evt.ToolCallId);
        Assert.Equal("send_email", evt.ToolName);
    }

    /// <summary>独立中断事件（interrupt 在顶层）：同样产出 interrupt。</summary>
    [Fact]
    public void Parse_InterruptStarted_TopLevel_ProducesInterrupt()
    {
        var json = """
        {"type":"INTERRUPT_STARTED","threadId":"t","runId":"r",
         "interrupt":{"id":"int_1","reason":"tool_call","message":"Approval required for tool call: publish","toolCallId":"call_1"}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("int_1", evt.InterruptId);
        Assert.Equal("publish", evt.ToolName);
    }

    /// <summary>ACTION_STARTED：产出 action 事件（网关按「🔧」过程行渲染）。</summary>
    [Fact]
    public void Parse_ActionStarted_ProducesAction()
    {
        var json = """{"type":"ACTION_STARTED","threadId":"t","runId":"r","payload":{"action":{"name":"compile_project"}}}""";
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("action", evt!.Type);
        Assert.Equal("compile_project", evt.ToolName);
    }

    /// <summary>hub 方言：外部回复消息的工具调用（TOOL_CALL_START 匹配已登记回复）→ tool 事件。</summary>
    [Fact]
    public void Parse_HubToolCallStart_MatchedReply_ProducesTool()
    {
        var json = """{"type":"TOOL_CALL_START","messageId":"msg_reply","toolCallId":"call_1","toolCallName":"search_docs"}""";
        var evt = Parse(json, hub: true, accepted: new HashSet<string> { "msg_reply" });
        Assert.NotNull(evt);
        Assert.Equal("tool", evt!.Type);
        Assert.Equal("search_docs", evt.ToolName);
    }

    /// <summary>hub 方言：未登记的 TOOL_CALL_START（其他成员 / 未知回复）忽略。</summary>
    [Fact]
    public void Parse_HubToolCallStart_Unmatched_Ignored()
    {
        var json = """{"type":"TOOL_CALL_START","messageId":"msg_other","toolCallName":"x"}""";
        Assert.Null(Parse(json, hub: true, accepted: new HashSet<string> { "msg_reply" }));
    }

    /// <summary>ATTACHMENT_STARTED 无 url（base64 内容流）→ att_start 占位（客户端累积），ATTACHMENT_CONTENT → att_content，FINISHED → att_finish。</summary>
    [Fact]
    public void Parse_Base64AttachmentSequence_ProducesAccumulatorEvents()
    {
        var start = Parse("""{"type":"ATTACHMENT_STARTED","id":"att_b1","name":"图表.png","contentType":"image/png","source":{"type":"base64"}}""");
        Assert.NotNull(start);
        Assert.Equal("att_start", start!.Type);
        Assert.Equal("att_b1", start.ToolCallId);
        Assert.Equal("图表.png", start.AttachmentName);
        Assert.Equal("image/png", start.AttachmentContentType);

        var content = Parse("""{"type":"ATTACHMENT_CONTENT","id":"att_b1","content":{"type":"base64","value":"iVBORw0KGgo="}}""");
        Assert.NotNull(content);
        Assert.Equal("att_content", content!.Type);
        Assert.Equal("att_b1", content.ToolCallId);
        Assert.Equal("iVBORw0KGgo=", content.Delta);

        var finish = Parse("""{"type":"ATTACHMENT_FINISHED","id":"att_b1"}""");
        Assert.NotNull(finish);
        Assert.Equal("att_finish", finish!.Type);
        Assert.Equal("att_b1", finish.ToolCallId);
    }

    /// <summary>累积器：att_start → att_content（分帧 ×2）→ att_finish 组装 data URL 附件；其余事件原样透传。</summary>
    [Fact]
    public void Accumulator_StartContentFinish_ProducesDataUrlAttachment()
    {
        var acc = new BridgeAttachmentAccumulator();

        var s1 = acc.Track(new AguiBridgeEvent("att_start", ToolCallId: "att_b2", AttachmentName: "图片.png", AttachmentContentType: "image/png"));
        Assert.Null(s1); // 累积中不产出
        Assert.Null(acc.Track(new AguiBridgeEvent("att_content", ToolCallId: "att_b2", Delta: "iVBORw0KGgoAAAANSUhEUg")));
        Assert.Null(acc.Track(new AguiBridgeEvent("att_content", ToolCallId: "att_b2", Delta: "AAAADr")));

        var done = acc.Track(new AguiBridgeEvent("att_finish", ToolCallId: "att_b2"));
        Assert.NotNull(done);
        Assert.Equal("attachment", done!.Type);
        var att = Assert.Single(done.Attachments!);
        Assert.Equal("图片.png", att.Name);
        Assert.StartsWith("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAADr", att.Url);
        Assert.Equal("image", att.Kind);

        // 无关事件透传
        var other = acc.Track(new AguiBridgeEvent("content", Delta: "hi"));
        Assert.Same(other, other);
        Assert.Equal("content", other!.Type);
    }

    /// <summary>累积器：超限（>20MB base64）丢弃，不产出损坏附件。</summary>
    [Fact]
    public void Accumulator_Overflow_Discards()
    {
        var acc = new BridgeAttachmentAccumulator();
        acc.Track(new AguiBridgeEvent("att_start", ToolCallId: "att_big", AttachmentName: "big.bin", AttachmentContentType: "application/octet-stream"));
        // 分两帧塞入 21MB
        var chunk = new string('A', 11 * 1024 * 1024);
        acc.Track(new AguiBridgeEvent("att_content", ToolCallId: "att_big", Delta: chunk));
        acc.Track(new AguiBridgeEvent("att_content", ToolCallId: "att_big", Delta: chunk));
        Assert.Null(acc.Track(new AguiBridgeEvent("att_finish", ToolCallId: "att_big")));
    }

    /// <summary>审批中断的 responseSchema 仅含 approved(boolean) → 工具审批（kind=approval）。</summary>
    [Fact]
    public void Parse_Interrupt_ApprovalSchema_KindIsApproval()
    {
        var json = """
        {"type":"RUN_FINISHED","threadId":"t","runId":"r",
         "outcome":{"type":"interrupt","interrupts":[{
            "id":"int_ap","reason":"tool_call","message":"Approval required for tool call: send_email","toolCallId":"call_ap",
            "responseSchema":{"type":"object","properties":{"approved":{"type":"boolean"}},"required":["approved"]}
         }]}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("approval", evt.InterruptKind);
        Assert.Null(evt.InputField);
    }

    /// <summary>请求输入型中断：responseSchema 含非布尔字段（如 answer）→ kind=input + inputField=answer。</summary>
    [Fact]
    public void Parse_Interrupt_InputSchema_KindIsInput()
    {
        var json = """
        {"type":"RUN_FINISHED","threadId":"t","runId":"r",
         "outcome":{"type":"interrupt","interrupts":[{
            "id":"int_in","reason":"request_input","message":"请提供请假条的内容要求（如请假原因、日期）：","toolCallId":"call_in",
            "responseSchema":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}
         }]}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("input", evt.InterruptKind);
        Assert.Equal("answer", evt.InputField);
        Assert.Equal("int_in", evt.InterruptId);
    }

    /// <summary>请求输入型：schema.type=string → kind=input，约定输入字段 answer。</summary>
    [Fact]
    public void Parse_Interrupt_StringSchema_KindIsInput()
    {
        var json = """
        {"type":"INTERRUPT_STARTED","threadId":"t","runId":"r",
         "payload":{"interrupt":{"id":"int_s","reason":"request_input","message":"请输入内容：",
            "responseSchema":{"type":"string"}}}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("input", evt.InterruptKind);
        Assert.Equal("answer", evt.InputField);
    }

    /// <summary>hub 方言：AGENT_INTERACTION_REQUEST 携带 responseSchema（非布尔字段）→ input 类型。</summary>
    [Fact]
    public void Parse_HubInterrupt_InputSchema_KindIsInput()
    {
        var json = """
        {"type":"AGENT_INTERACTION_REQUEST","interruptId":"int_hub","message":"请输入补充信息：",
         "responseSchema":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}}
        """;
        var evt = Parse(json, hub: true);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("input", evt.InterruptKind);
        Assert.Equal("answer", evt.InputField);
    }

    /// <summary>输入 payload 规范化：多选（array）逗号字符串拆数组；integer/number 转数值；其余保持。</summary>
    [Fact]
    public void NormalizeInputPayload_ConvertsBySchema()
    {
        var schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "reason":{"type":"string"},
          "days":{"type":"integer"},
          "notify":{"type":"boolean"},
          "tags":{"type":"array","items":{"type":"string","enum":["a","b","c"]}}
        }}
        """).RootElement.Clone();
        var payload = JsonDocument.Parse("""{"reason":"个人原因","days":"1","notify":"true","tags":"a,c"}""").RootElement.Clone();

        var normalized = AguiBridgeProtocol.NormalizeInputPayload(schema, payload);
        Assert.NotNull(normalized);
        Assert.True(normalized!.Value.TryGetProperty("reason", out var reason) && reason.GetString() == "个人原因");
        Assert.True(normalized.Value.TryGetProperty("days", out var days) && days.GetInt64() == 1);
        Assert.True(normalized.Value.TryGetProperty("notify", out var notify) && notify.GetBoolean()); // boolean 字符串转布尔（前端 select 值）
        Assert.True(normalized.Value.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array);
        Assert.Equal(2, tags.EnumerateArray().Count());
        Assert.Equal("a", tags[0].GetString());
        Assert.Equal("c", tags[1].GetString());
    }

    /// <summary>输入 payload 规范化：schema 缺失 / payload 非对象 → 原样返回。</summary>
    [Fact]
    public void NormalizeInputPayload_NoSchema_Passthrough()
    {
        var payload = JsonDocument.Parse("""{"answer":"hello"}""").RootElement.Clone();
        var normalized = AguiBridgeProtocol.NormalizeInputPayload(null, payload);
        Assert.NotNull(normalized);
        Assert.Equal("hello", normalized!.Value.GetProperty("answer").GetString());
    }

    /// <summary>顶层单字段 schema（choice 单选 string+enum）：payload 首字段套用顶层类型规范化（字符串保持）。</summary>
    [Fact]
    public void Normalize_ChoiceTopLevelSchema_KeepsString()
    {
        using var schema = JsonDocument.Parse("""{"type":"string","enum":["事假","病假"]}""");
        using var payload = JsonDocument.Parse("""{"answer":"事假"}""");
        var normalized = AguiBridgeProtocol.NormalizeInputPayload(schema.RootElement, payload.RootElement);
        Assert.Equal("事假", normalized.Value.GetProperty("answer").GetString());
    }

    /// <summary>顶层单字段 schema（multi_choice 多选 array+items.enum）：逗号字符串拆数组。</summary>
    [Fact]
    public void Normalize_MultiChoiceTopLevelSchema_SplitsArray()
    {
        using var schema = JsonDocument.Parse("""{"type":"array","items":{"type":"string","enum":["A","B","C"]}}""");
        using var payload = JsonDocument.Parse("""{"answer":"A,C"}""");
        var normalized = AguiBridgeProtocol.NormalizeInputPayload(schema.RootElement, payload.RootElement);
        var arr = normalized.Value.GetProperty("answer");
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("A", arr[0].GetString());
        Assert.Equal("C", arr[1].GetString());
    }

    /// <summary>对象 schema：boolean 字符串转布尔、integer 字符串转数值、多选拆数组。</summary>
    [Fact]
    public void Normalize_ObjectSchema_ConvertsTypes()
    {
        using var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"confirm\":{\"type\":\"boolean\"},\"days\":{\"type\":\"integer\"},\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"x\",\"y\"]}}},\"required\":[\"confirm\"]}");
        using var payload = JsonDocument.Parse("""{"confirm":"true","days":"3","tags":"x,y"}""");
        var normalized = AguiBridgeProtocol.NormalizeInputPayload(schema.RootElement, payload.RootElement);
        Assert.True(normalized.Value.GetProperty("confirm").GetBoolean());
        Assert.Equal(3L, normalized.Value.GetProperty("days").GetInt64());
        Assert.Equal(2, normalized.Value.GetProperty("tags").GetArrayLength());
    }

    /// <summary>hub 方言：外部回复 START 携带附件（本协议 TEXT_MESSAGE_START.attachments）→ 批量附件事件。
    /// 真实流两步：先收到自己的回显（捕获 selfMessageId），再收到外部回复 START。</summary>
    [Fact]
    public void Parse_HubStart_WithAttachments_ProducesBatch()
    {
        var selfEcho = """
        {"type":"TEXT_MESSAGE_START","messageId":"msg_self","senderId":"agent_x","replyToMessageId":null}
        """;
        string? selfMessageId = null;
        var accepted = new HashSet<string>();
        using (var selfDoc = JsonDocument.Parse(selfEcho))
        {
            AguiBridgeProtocol.Parse(selfDoc, hubMode: true, "agent_x", accepted, ref selfMessageId);
            Assert.Equal("msg_self", selfMessageId);
        }

        var json = """
        {"type":"TEXT_MESSAGE_START","messageId":"msg_reply","senderId":"agent_remote","replyToMessageId":"msg_self",
         "attachments":[{"name":"群聊摘要.pptx","contentType":"application/vnd.openxmlformats-officedocument.presentationml.presentation",
                          "url":"http://ext:5088/api/file/download?path=abc"}]}
        """;
        AguiBridgeEvent? evt;
        using (var doc = JsonDocument.Parse(json))
        {
            evt = AguiBridgeProtocol.Parse(doc, hubMode: true, "agent_x", accepted, ref selfMessageId);
        }
        Assert.NotNull(evt);
        Assert.Equal("attachment", evt!.Type);
        var att = Assert.Single(evt.Attachments!);
        Assert.Equal("群聊摘要.pptx", att.Name);
        Assert.Equal("file", att.Kind);
        Assert.Contains("api/file/download", att.Url);
    }

    // ---- question 工具中断：问题文本兜底提取 ----------------

    /// <summary>外部 question 工具中断：顶层 message 缺失、问题在 toolArguments.question（AG-UI 标准工具参数）→ 提取为中断消息。</summary>
    [Fact]
    public void Parse_QuestionTool_ArgsQuestion_BecomesInterruptMessage()
    {
        var json = """
        {"type":"RUN_FINISHED","threadId":"t","runId":"r",
         "outcome":{"type":"interrupt","interrupts":[{
            "id":"int_q1","reason":"tool_call","toolCallId":"call_q1",
            "toolName":"question","toolArguments":{"question":"你希望用哪种方式发送报告？","choices":null,"allowFreeform":true},
            "responseSchema":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}
         }]}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("question", evt.ToolName);
        Assert.Equal("你希望用哪种方式发送报告？", evt.InterruptMessage);
        Assert.Equal("input", evt.InterruptKind);
        Assert.Equal("call_q1", evt.ToolCallId);
    }

    /// <summary>AGUI.AspNetCore 形态：问题在 metadata.function_call.arguments（JSON 字符串）→ 解析提取；顶层 message 为空字符串同样兜底。</summary>
    [Fact]
    public void Parse_QuestionTool_MetadataStringArgs_BecomesInterruptMessage()
    {
        var json = """
        {"type":"RUN_FINISHED","threadId":"t","runId":"r",
         "outcome":{"type":"interrupt","interrupts":[{
            "id":"int_q2","reason":"tool_call","message":"","toolCallId":"call_q2",
            "metadata":{"function_call":{"name":"question",
               "arguments":"{\"question\":\"请问您的请假原因是？\",\"allowFreeform\":true}"}},
            "responseSchema":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}
         }]}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("question", evt.ToolName);
        Assert.Equal("请问您的请假原因是？", evt.InterruptMessage);
        Assert.Equal("input", evt.InterruptKind);
    }

    /// <summary>顶层 message 非空时优先保留（不覆盖外部服务已有的明确文案）。</summary>
    [Fact]
    public void Parse_QuestionTool_ExplicitMessage_TakesPriority()
    {
        var json = """
        {"type":"RUN_FINISHED","threadId":"t","runId":"r",
         "outcome":{"type":"interrupt","interrupts":[{
            "id":"int_q3","reason":"tool_call","message":"请确认接收方式","toolCallId":"call_q3",
            "toolName":"question","toolArguments":{"question":"你希望用哪种方式？"}
         }]}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("请确认接收方式", evt!.InterruptMessage);
    }

    /// <summary>hub 方言：AGENT_INTERACTION_REQUEST 无 message、参数含 question → 同样兜底提取。</summary>
    [Fact]
    public void Parse_HubInterrupt_ArgsQuestion_BecomesInterruptMessage()
    {
        var json = """
        {"type":"AGENT_INTERACTION_REQUEST","interruptId":"int_hub_q","toolCallId":"call_hub_q",
         "toolName":"question","toolArguments":{"question":"选择哪种方案？","choices":["A","B"]},
         "responseSchema":{"type":"object","properties":{"answer":{"type":"string","enum":["A","B"]}},"required":["answer"]}}
        """;
        var evt = Parse(json, hub: true);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("选择哪种方案？", evt.InterruptMessage);
        Assert.Equal("choice", evt.InterruptKind);
    }

    /// <summary>OpenCode 形态：问题在 metadata.questions（结构化数组，message 仅为占位文案）→
    /// 拼接为「1. 【header】question + 选项列表」供前端展示。</summary>
    [Fact]
    public void Parse_QuestionTool_MetadataQuestions_BecomesInterruptMessage()
    {
        var json = """
        {"type":"RUN_FINISHED","threadId":"t","runId":"r",
         "outcome":{"type":"interrupt","interrupts":[{
            "id":"que_01abc","reason":"user_input","message":"OpenCode requires additional input",
            "responseSchema":{"type":"object","properties":{"answers":{"type":"array"}},"required":["answers"]},
            "metadata":{"source":"opencode","kind":"question","questions":[
               {"header":"使用场景","question":"这份 PPT 的主要使用场景是？",
                "options":[{"label":"行业研究报告 / 内部分享"},{"label":"对外路演"}]},
               {"question":"你希望的篇幅规模是？",
                "options":[{"label":"精简版"},{"label":"标准版"}]}
            ]}
         }]}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        Assert.Equal("multi_choice", evt.InterruptKind); // answers 数组 → 多选
        Assert.Equal("answers", evt.InputField);
        // OpenCode 的 message 是占位文案（"OpenCode requires additional input"）→ 被 metadata.questions 覆盖，展示真实问题
        Assert.Contains("1. [使用场景] 这份 PPT 的主要使用场景是？", evt.InterruptMessage);
        Assert.Contains("2. 你希望的篇幅规模是？", evt.InterruptMessage);
        // 结构化问题同步解析（前端逐题渲染选项用）
        Assert.NotNull(evt.Questions);
        Assert.Equal(2, evt.Questions!.Count);
        Assert.Equal("使用场景", evt.Questions[0].Header);
        Assert.Equal("这份 PPT 的主要使用场景是？", evt.Questions[0].Question);
        Assert.Equal(2, evt.Questions[0].Options!.Count);
        Assert.Equal("行业研究报告 / 内部分享", evt.Questions[0].Options![0].Label);
        Assert.Equal("对外路演", evt.Questions[0].Options![1].Label);
        Assert.Null(evt.Questions[1].Header); // 第二个问题无 header
        Assert.Equal("精简版", evt.Questions[1].Options![0].Label);
    }

    /// <summary>OpenCode 多选标记：metadata.questions 元素带 multiple:true → BridgeQuestion.Multiple，前端渲染勾选。</summary>
    [Fact]
    public void Parse_QuestionTool_MetadataQuestions_MultipleFlag()
    {
        var json = """
        {"type":"RUN_FINISHED","threadId":"t","runId":"r",
         "outcome":{"type":"interrupt","interrupts":[{
            "id":"que_99","reason":"user_input","message":"",
            "responseSchema":{"type":"object","properties":{"answers":{"type":"array"}},"required":["answers"]},
            "metadata":{"questions":[
               {"header":"受众","question":"受众是？","options":[{"label":"投资"},{"label":"内部"}]},
               {"header":"板块","question":"选哪些板块？（可多选）","multiple":true,
                "options":[{"label":"市场规模"},{"label":"竞争格局"},{"label":"技术趋势"}]}
            ]}
         }]}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal(2, evt!.Questions!.Count);
        Assert.False(evt.Questions[0].Multiple); // 单选
        Assert.True(evt.Questions[1].Multiple);  // 多选标记
        Assert.Equal(3, evt.Questions[1].Options!.Count);
    }

    /// <summary>OpenCode 形态 + 顶层 message 为空：从 metadata.questions 拼接问题文本（含 header 与选项）。</summary>
    [Fact]
    public void Parse_QuestionTool_MetadataQuestions_NoMessage_ConcatenatesQuestions()
    {
        var json = """
        {"type":"RUN_FINISHED","threadId":"t","runId":"r",
         "outcome":{"type":"interrupt","interrupts":[{
            "id":"que_02def","reason":"user_input","message":"",
            "responseSchema":{"type":"object","properties":{"answers":{"type":"array"}},"required":["answers"]},
            "metadata":{"questions":[
               {"header":"使用场景","question":"这份 PPT 的主要使用场景是？",
                "options":[{"label":"行业研究报告 / 内部分享"},{"label":"对外路演"}]},
               {"question":"你希望的篇幅规模是？",
                "options":[{"label":"精简版"},{"label":"标准版"}]}
            ]}
         }]}}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("interrupt", evt!.Type);
        var msg = evt!.InterruptMessage!;
        Assert.Contains("1. [使用场景] 这份 PPT 的主要使用场景是？", msg);
        Assert.Contains("· 行业研究报告 / 内部分享", msg);
        Assert.Contains("2. 你希望的篇幅规模是？", msg);
        Assert.Contains("· 标准版", msg);
    }

    /// <summary>OpenCode 工具执行结果：TOOL_CALL_RESULT 解析为 tool_result 事件（含 toolCallId / content），前端与调用行关联。</summary>
    [Fact]
    public void Parse_ToolCallResult_ProducesToolResultEvent()
    {
        var json = """
        {"type":"TOOL_CALL_RESULT","messageId":"tool-result-call_01","toolCallId":"call_01_AbC",
         "content":"总计 584\n-rw-r--r-- AI前景展望.pptx\n","role":"tool"}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("tool_result", evt!.Type);
        Assert.Equal("call_01_AbC", evt.ToolCallId);
        Assert.Contains("AI前景展望.pptx", evt.Delta);
    }

    /// <summary>OpenCode 工具调用结束：TOOL_CALL_END 解析为 tool_end 事件（供网关回填分帧累积参数）。</summary>
    [Fact]
    public void Parse_ToolCallEnd_ProducesToolEndEvent()
    {
        var json = """{"type":"TOOL_CALL_END","toolCallId":"call_02"}""";
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("tool_end", evt!.Type);
        Assert.Equal("call_02", evt.ToolCallId);
    }

    /// <summary>TOOL_CALL_END + 分帧累积参数：EnrichToolEndArgs 回填完整参数（前端展示参数详情）。</summary>
    [Fact]
    public void EnrichToolEndArgs_FillsAccumulatedArguments()
    {
        var argsByCallId = new Dictionary<string, string> { ["call_03"] = "{\"question\":\"测试\",\"choices\":[\"A\",\"B\"]}" };
        var evt = new AguiBridgeEvent("tool_end", ToolCallId: "call_03");
        var enriched = AguiBridgeProtocol.EnrichToolEndArgs(evt, argsByCallId);
        Assert.NotNull(enriched.ToolArguments);
        Assert.True(enriched.ToolArguments!.Value.TryGetProperty("question", out var q) && q.GetString() == "测试");
        // 无累积参数 → 保持 ToolArguments 为空
        var plain = AguiBridgeProtocol.EnrichToolEndArgs(new AguiBridgeEvent("tool_end", ToolCallId: "call_none"), new Dictionary<string, string>());
        Assert.Null(plain.ToolArguments);
    }

    /// <summary>OpenCode 任务进度流：ACTIVITY_SNAPSHOT（todo 状态快照）解析为 todo 事件，前端实时进度块。</summary>
    [Fact]
    public void Parse_ActivitySnapshot_ProducesTodoEvent()
    {
        var json = """
        {"type":"ACTIVITY_SNAPSHOT","messageId":"todo-ses_1","activityType":"opencode.todo",
         "content":[
           {"content":"Create theme.js","status":"completed","priority":"high"},
           {"content":"Write slides 1-4","status":"in_progress","priority":"high"},
           {"content":"Compile PPTX","status":"pending","priority":"high"}
         ],"replace":true}
        """;
        var evt = Parse(json);
        Assert.NotNull(evt);
        Assert.Equal("todo", evt!.Type);
        Assert.Contains("in_progress", evt.Delta);
        // content 非数组（如错误快照）→ 忽略
        Assert.Null(Parse("""{"type":"ACTIVITY_SNAPSHOT","content":{"foo":1}}"""));
    }
}
