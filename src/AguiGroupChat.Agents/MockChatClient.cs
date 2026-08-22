using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AguiGroupChat.Agents;

/// <summary>
/// 本地模拟 IChatClient（Provider=mock 时使用）：无需 API 密钥即可演示流式群聊。
/// 按智能体身份生成结构化回复，分 3 段增量输出以模拟真实模型流式效果。
/// </summary>
public sealed class MockChatClient : IChatClient
{
    private readonly AgentDefinition _agent;
    private readonly TimeProvider _time;
    private readonly bool _enableTools;
    private readonly IReadOnlyList<AgentSkillConfig>? _skills;
    private int _toolCallSeq; // 工具调用序号：并发流式下用 Interlocked 自增保证唯一

    public MockChatClient(AgentDefinition agent, TimeProvider? time = null, bool enableTools = false, IReadOnlyList<AgentSkillConfig>? skills = null)
    {
        _agent = agent;
        _time = time ?? TimeProvider.System;
        _enableTools = enableTools;
        _skills = skills;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = string.Concat(BuildChunks(messages));
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 工具调用 / 人机交互决策恢复：优先产出工具更新（含审批中断），否则按普通文本流式输出
        var toolUpdate = BuildToolUpdate(messages);
        if (toolUpdate is not null)
        {
            yield return toolUpdate;
            yield break;
        }
        // 与真实 OpenAI 兼容客户端一致：每帧只产出新增片段（增量），而非累计全文。
        foreach (var chunk in BuildChunks(messages))
        {
            await Task.Delay(200, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
    }

    public void Dispose() { }

    private string[] BuildChunks(IEnumerable<ChatMessage> messages)
    {
        var lastUserText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        // 语境发言决策（Contextual 模式）：按简单规则返回 YES/NO，便于本地演示与测试
        if (lastUserText.Contains("__AGUI_DECIDE__", StringComparison.Ordinal))
        {
            var idx = lastUserText.IndexOf("最新消息：", StringComparison.Ordinal);
            var latest = idx >= 0 ? lastUserText[(idx + "最新消息：".Length)..] : "";
            var shouldSpeak = latest.Contains('?') || latest.Contains('？')
                || latest.Contains("帮我", StringComparison.Ordinal)
                || latest.Contains("建议", StringComparison.Ordinal)
                || latest.Contains("@" + _agent.Nickname, StringComparison.Ordinal);
            return [shouldSpeak ? "YES" : "NO"];
        }

        var text = $"收到！关于「{lastUserText}」，作为「{_agent.Nickname}」我的建议如下：\n\n" +
                   "1. 明确需求边界与验收标准，避免范围蔓延；\n" +
                   "2. 拆分里程碑，先交付可验证的最小闭环；\n" +
                   "3. 同步补齐异常路径与回滚方案。\n\n" +
                   "（当前为 Mock 模式模拟回复，配置 Agents:ApiKey 后可接入真实模型。）";
        var split = Math.Max(1, text.Length / 3);
        return [text[..split], text[split..(split * 2)], text[(split * 2)..]];
    }

    /// <summary>
    /// 模拟工具调用与交互决策（EnableTools 时）：请求「时间」→ get_current_time；「公告」→ publish_announcement（需审批）；
    /// 「计算」→ calculator；「换算」→ unit_converter。消息含 ToolApprovalResponseContent（触发者决策已回灌）→ 输出工具执行结果文本。
    /// </summary>
    private ChatResponseUpdate? BuildToolUpdate(IEnumerable<ChatMessage> messages)
    {
        if (!_enableTools) return null;
        var lastUserText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        // 交互决策恢复：批准 → 工具已在恢复阶段执行（历史含 FunctionResultContent）；拒绝 → 决策仍保留在消息中
        var results = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        var decisions = messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>().ToList();
        if (results.Count > 0)
        {
            var last = results[^1];
            // publish_announcement 走审批路径（CallId 前缀 call_mock_ann_）；其余工具直接回显执行结果
            if (last.CallId?.StartsWith("call_mock_ann_", StringComparison.Ordinal) == true)
                return new ChatResponseUpdate(ChatRole.Assistant, "✅ 已批准：操作已执行，公告发布成功，所有成员可见。");
            return new ChatResponseUpdate(ChatRole.Assistant, $"工具执行结果：{last.Result}");
        }
        if (decisions.Count > 0 && !decisions[^1].Approved)
            return new ChatResponseUpdate(ChatRole.Assistant, "已拒绝：操作已取消，未执行任何操作。");

        // 请求「创建技能」→ create_skill（需审批）：提取技能名，人设取消息剩余部分
        var skillCreate = Regex.Match(lastUserText, @"创建技能\s*[:：]?\s*([A-Za-z0-9_-]{1,40})");
        if (skillCreate.Success)
        {
            var name = skillCreate.Groups[1].Value;
            var idx = lastUserText.IndexOf("创建技能", StringComparison.Ordinal);
            var rest = lastUserText[(idx + 4)..].Trim().TrimStart(':', '：', ' ');
            var instructions = rest.Length > name.Length && rest.StartsWith(name, StringComparison.Ordinal)
                ? rest[name.Length..].Trim().TrimStart(':', '：', ' ')
                : rest;
            if (string.IsNullOrEmpty(instructions))
                instructions = $"你是「{name}」技能专家，按宿主智能体需求提供专业答复。";
            return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call_mock_skillcreate_" + Interlocked.Increment(ref _toolCallSeq), "create_skill",
                    new Dictionary<string, object?>
                    {
                        ["skillName"] = name,
                        ["instructions"] = instructions,
                        ["description"] = $"按需调用技能 {name}，获取该领域专业答复。",
                    })]);
        }

        // 请求「技能」→ 调用第一个已配置技能（MSAGENT AgentSkill：模型把子代理作为工具调用，
        // 「技能」之后的内容作为 query 传给子智能体）
        if (_skills is { Count: > 0 } && lastUserText.Contains("技能", StringComparison.Ordinal))
        {
            var skill = _skills[0];
            var skillIdx = lastUserText.IndexOf("技能", StringComparison.Ordinal);
            var query = lastUserText[(skillIdx + 2)..].Trim().TrimStart(':', '：', ' ');
            if (string.IsNullOrEmpty(query)) query = lastUserText.Trim();
            return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call_mock_skill_" + Interlocked.Increment(ref _toolCallSeq), skill.SkillId,
                    new Dictionary<string, object?> { ["query"] = query })]);
        }

        // 请求「公告」→ 调用需审批的 publish_announcement（触发人机交互）
        var announceIdx = lastUserText.IndexOf("公告", StringComparison.Ordinal);
        if (announceIdx >= 0)
        {
            var content = lastUserText[(announceIdx + 2)..].Trim();
            if (string.IsNullOrEmpty(content)) content = "（示例公告）";
            return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call_mock_ann_" + Interlocked.Increment(ref _toolCallSeq), "publish_announcement",
                    new Dictionary<string, object?> { ["announcement"] = content })]);
        }

        // 请求「计算」→ calculator（提取表达式）
        var calcMatch = Regex.Match(lastUserText, @"计算\s*[:：]?\s*([\d\s+\-*/%^().eE]+)");
        if (calcMatch.Success && calcMatch.Groups[1].Value.Trim().Length > 0)
        {
            return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call_mock_calc_" + Interlocked.Increment(ref _toolCallSeq), "calculator",
                    new Dictionary<string, object?> { ["expression"] = calcMatch.Groups[1].Value.Trim() })]);
        }

        // 请求「换算」→ unit_converter（提取 数值 + 单位A 到 单位B）
        var convMatch = Regex.Match(lastUserText, @"换算\s*[:：]?\s*(\d+(?:\.\d+)?)\s*([A-Za-z°℃℉]+)\s*(?:到|至|换成|转成|->|→|to)\s*([A-Za-z°℃℉]+)");
        if (convMatch.Success)
        {
            return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call_mock_conv_" + Interlocked.Increment(ref _toolCallSeq), "unit_converter",
                    new Dictionary<string, object?>
                    {
                        ["value"] = double.Parse(convMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                        ["from"] = convMatch.Groups[2].Value,
                        ["to"] = convMatch.Groups[3].Value,
                    })]);
        }

        // 请求「时间」→ 调用普通工具
        if (lastUserText.Contains("时间", StringComparison.Ordinal) || lastUserText.Contains("几点", StringComparison.Ordinal))
        {
            return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call_mock_time_" + Interlocked.Increment(ref _toolCallSeq), "get_current_time", null)]);
        }
        return null;
    }
}
