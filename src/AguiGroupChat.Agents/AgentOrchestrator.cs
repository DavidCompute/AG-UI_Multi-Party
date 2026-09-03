using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 一键组织编排：根据一句话需求，用模型生成<b>数字员工组织架构方案</b>——岗位清单、
/// 每个岗位要挂载的技能、以及岗位之间连接（向下指派 / 向上提升 / 汇报中继）。
/// 生成结果<b>不落库</b>，由前端预览确认后再交实际创建端点落库（见 AgentApi.OrchestrateApply）。
/// Provider=mock 时输出确定性模板（无模型调用），便于本地演示与测试。
/// </summary>
public static class AgentOrchestrator
{
    /// <summary>生成组织方案。真实模型走 OpenAI 兼容接口；mock 走确定性模板。</summary>
    public static async Task<OrchestrationPlan> GenerateAsync(AgentOptions options, string requirement, ILogger logger, CancellationToken ct)
    {
        var req = (requirement ?? "").Trim();
        if (req.Length < 2) throw new InvalidOperationException("需求描述至少 2 个字符");
        if (req.Length > 5000) throw new InvalidOperationException("需求描述最长 5000 字符");

        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            return BuildTemplate(req);

        IChatClient client;
        try
        {
            var isDeepSeek = string.Equals(options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            var ov = AgentCatalog.StructuredFastModel(isDeepSeek); // 组织方案按严格 JSON 解析：格式任务，不进 reasoner
            client = AgentCatalog.BuildOpenAIChatClient(
                options, new AgentDefinition { AgentId = "orchestrator", Nickname = "组织编排器" }, isDeepSeek, ov).AsIChatClient();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("组织编排需要可用的模型配置（" + ex.Message + "）", ex);
        }

        try
        {
            var prompt = BuildPrompt(req);
            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var text = resp.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("组织编排返回为空，请重试");
            var plan = Parse(text);
            logger.LogInformation("已根据需求生成组织方案（{Agents} 名 / {Skills} 技能）：{Req}",
                plan.Agents.Count, plan.Skills.Count, Truncate(req));
            return plan;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>流式生成组织方案文本：逐个 token 产出已到达的文本增量，供调用方实时转发（SSE）/ 展示生成过程。
    /// 结束后把完整文本交给调用方 <see cref="Parse"/> 得到结构化方案。mock 模式无真实模型，整段模板拆几段产出。</summary>
    public static async IAsyncEnumerable<string> StreamTextAsync(
        AgentOptions options, string requirement, ILogger logger,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var req = (requirement ?? "").Trim();
        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            var text = JsonSerializer.Serialize(BuildTemplate(req));
            const int chunk = 80;
            for (var i = 0; i < text.Length; i += chunk)
            {
                ct.ThrowIfCancellationRequested();
                yield return text.Substring(i, Math.Min(chunk, text.Length - i));
                await Task.Yield();
            }
            yield break;
        }

        IChatClient client;
        try
        {
            var isDeepSeek = string.Equals(options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            var ov2 = AgentCatalog.StructuredFastModel(isDeepSeek); // 同：流式组织的 JSON 也按常规模型，保证速度与有效
            client = AgentCatalog.BuildOpenAIChatClient(
                options, new AgentDefinition { AgentId = "orchestrator", Nickname = "组织编排器" }, isDeepSeek, ov2).AsIChatClient();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("组织编排需要可用的模型配置（" + ex.Message + "）", ex);
        }

        try
        {
            var prompt = BuildPrompt(req);
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct))
            {
                if (string.IsNullOrEmpty(update.Text)) continue;
                yield return update.Text;
            }
            logger.LogInformation("组织编排流式文本已接收完毕：{Req}", Truncate(req));
        }
        finally
        {
            client.Dispose();
        }
    }

    private static string BuildPrompt(string requirement)
    {
        return
            "你是企业数字化组织架构设计师。根据用户的一句需求，设计一套「数字员工组织架构 + 各岗位技能 + 岗位连接」方案，只输出一个 JSON，不要任何其他文字。\n\n" +
            "数字员工（agents）字段：\n" +
            "- agentId：ASCII 字母/数字/下划线/连字符（≤40）。\n" +
            "- nickname：中文角色名。\n" +
            "- description：一句话职责（供模型指派时判断语境）。\n" +
            "- instructions：该角色的系统提示（身份/职责/风格，150~300 字）。\n" +
            "- triggerMode：mentioned（默认，@ 触发）。\n" +
            "- skillIds：本岗位要挂载的技能 ID 列表（引用下方 skills 里的 skillId）。\n" +
            "- assignmentIds：向下指派白名单（可把不归属自己的任务指派给哪个下级，填下级 agentId）。\n" +
            "- escalationAgentId：向上提升目标（通常是其上级 agentId）。\n" +
            "- relayToAgentId：整轮交接目标（可选，较少用）。\n\n" +
            "技能（skills）字段：\n" +
            "- skillId：ASCII 且 ≤40。\n" +
            "- name：中文名。\n" +
            "- description：给模型的调用说明（何时调用/参数/返回，50~150 字）。\n" +
            "- kind：按岗位职责<b>智能选择</b>——需要本机/系统操作（查电脑信息、执行命令、操作文件/磁盘等）用 <b>shell</b>；需要调用外部 HTTP 接口用 <b>http</b>；纯文本/知识/写作/流程模板用 <b>prompt</b>。不要一律 prompt。\n" +
            "- body：prompt 填模板文本；shell 填命令/脚本（可跨平台，Windows 用 PowerShell）；http 填 {\"method\":\"GET\",\"url\":\"${query}\",\"headers\":{}}。\n" +
            "- executionLocation：shell 用 <b>client</b>（在本机执行，需批准）；http/prompt 用 server（服务端）。\n" +
            "- requiresApproval：shell 一律 true；http 一律 true；executionLocation=client 一律 true；纯 prompt 服务端可 false。\n\n" +
            "连接原则：给出 2~6 个数字员工；尽量形成「主管 → 若干执行岗」的层次；主管的 escalationAgentId 指向更上层或留空；\n" +
            "执行岗 assignmentIds 留空、escalationAgentId 指向主管。技能要贴合岗位职责，数量 1~6 个。\n\n" +
            "只输出如下 JSON：\n" +
            "{\"title\":\"<组织名>\",\"agents\":[{\"agentId\":\"\",\"nickname\":\"\",\"description\":\"\",\"instructions\":\"\",\"triggerMode\":\"mentioned\",\"skillIds\":[],\"assignmentIds\":[],\"escalationAgentId\":null,\"relayToAgentId\":null}],\"skills\":[{\"skillId\":\"\",\"name\":\"\",\"description\":\"\",\"kind\":\"prompt\",\"body\":\"\",\"executionLocation\":\"server\",\"requiresApproval\":false}]}\n\n" +
            "用户需求：" + requirement;
    }

    /// <summary>解析模型生成的 JSON 文本为结构化方案（供流式端点收尾使用）。失败抛明确异常。</summary>
    public static OrchestrationPlan Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("组织编排结果不是有效 JSON");
        OrchestrationPlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<OrchestrationPlan>(text.Substring(start, end - start + 1),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("组织编排结果为空");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("组织编排结果 JSON 解析失败：" + ex.Message, ex);
        }
        plan.Agents ??= [];
        plan.Skills ??= [];
        if (plan.Agents.Count == 0 || plan.Agents.All(a => string.IsNullOrWhiteSpace(a.AgentId)))
            throw new InvalidOperationException("组织编排结果缺少数字员工");
        return plan;
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";

    /// <summary>mock 模式确定性模板：围绕需求生成一个示例组织（主管 + 两个执行岗 + 各自技能 + 连接）。</summary>
    private static OrchestrationPlan BuildTemplate(string requirement)
    {
        // mock 用带随机序号的纯 ASCII id（避免中文/符号转成重复下划线，也避免多次生成/重复 apply 在同一实例撞 ID）
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var manager = "mgr_" + suffix;
        var exec1 = "exec_" + suffix + "_a";
        var exec2 = "exec_" + suffix + "_b";
        var skill1 = "skill_" + suffix + "_a";
        var skill2 = "skill_" + suffix + "_b";
        var brief = requirement.Length > 30 ? requirement[..30] + "…" : requirement;

        return new OrchestrationPlan
        {
            Title = $"「{brief}」组织",
            Agents =
            [
                new OrchestratedAgent { AgentId = manager, Nickname = "主管", Description = $"统筹「{brief}」", Instructions = $"你是一位数字员工主管，负责统筹「{brief}」相关工作，评判断语境并指派给合适的下一层执行岗。", TriggerMode = "mentioned", SkillIds = [skill1, skill2], AssignmentIds = [exec1, exec2], EscalationAgentId = null, RelayToAgentId = null },
                new OrchestratedAgent { AgentId = exec1, Nickname = "执行岗A", Description = $"负责「{brief}」的部分执行", Instructions = $"你是执行岗A，负责完成「{brief}」相关工作，遇到不属于自己的任务应说明并上抛。", TriggerMode = "mentioned", SkillIds = [skill1], AssignmentIds = [], EscalationAgentId = manager, RelayToAgentId = null },
                new OrchestratedAgent { AgentId = exec2, Nickname = "执行岗B", Description = $"负责「{brief}」的部分执行", Instructions = $"你是执行岗B，负责完成「{brief}」相关工作，遇到不属于自己的任务应说明并上抛。", TriggerMode = "mentioned", SkillIds = [skill2], AssignmentIds = [], EscalationAgentId = manager, RelayToAgentId = null },
            ],
            Skills =
            [
                new OrchestratedSkill { SkillId = skill1, Name = "A 类事务处理", Kind = "prompt", Description = $"处理与「{brief}」相关的 A 类事务。", Body = $"请针对与「{brief}」相关的 A 类事务，给出专业、可落地的处理建议。", ExecutionLocation = "server", RequiresApproval = false },
                new OrchestratedSkill { SkillId = skill2, Name = "本机信息速查", Kind = "shell", Description = $"获取本机基本信息与资源占用的运维速查（演示 skill 示例：读系统信息）。", Body = "$d=[Environment]::GetFolderPath('Desktop'); if(Test-Path $d){ Write-Output $d } else { Write-Output 'no-desk' }", ExecutionLocation = "client", RequiresApproval = true },
            ],
        };
    }
}

/// <summary>一份完整的组织编排方案（岗位 + 技能 + 连接）。由 <see cref="AgentOrchestrator"/> 生成，前端预览确认后落库。</summary>
public sealed class OrchestrationPlan
{
    public string? Title { get; set; }
    public List<OrchestratedAgent> Agents { get; set; } = [];
    public List<OrchestratedSkill> Skills { get; set; } = [];
}

/// <summary>一个数字员工岗位及其连接关系。</summary>
public sealed class OrchestratedAgent
{
    public string? AgentId { get; set; }
    public string? Nickname { get; set; }
    public string? Description { get; set; }
    public string? Instructions { get; set; }
    public string? TriggerMode { get; set; }
    public List<string> SkillIds { get; set; } = [];
    public List<string>? AssignmentIds { get; set; }
    public string? EscalationAgentId { get; set; }
    public string? RelayToAgentId { get; set; }
}

/// <summary>一个可复用技能定义（由编排方案批量生成，供落地时写入技能库）。</summary>
public sealed class OrchestratedSkill
{
    public string? SkillId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Kind { get; set; }

    /// <summary>技能体：模型有时把 prompt 写为字符串、把 http/shell 写为对象/数组。
    /// 这里用 <see cref="FlexibleBodyConverter"/> 兼容成字符串（对象/数组/数值则序列化为紧凑 JSON 文本）。</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleBodyConverter))]
    public string? Body { get; set; }
    public string? ExecutionLocation { get; set; }
    public bool RequiresApproval { get; set; }
}

/// <summary>把技能 body 的 JSON 值宽松地读成字符串：字符串原样；对象 / 数组 / 数值 / 布尔序列化为紧凑 JSON 文本。
/// 真实模型对 http/shell 技能常把 body 写成 JSON 对象，兼容后不报错（后续按字符串写入技能库）。</summary>
public sealed class FlexibleBodyConverter : System.Text.Json.Serialization.JsonConverter<string?>
{
    public override string? Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case System.Text.Json.JsonTokenType.String:
            case System.Text.Json.JsonTokenType.Null:
                return reader.GetString();
            case System.Text.Json.JsonTokenType.StartObject:
            case System.Text.Json.JsonTokenType.StartArray:
            {
                using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);
                return doc.RootElement.GetRawText();
            }
            default:
                // 数值：转字符串（宽松容错，不追求精度）；布尔原样
                if (reader.TryGetDouble(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (reader.TokenType == System.Text.Json.JsonTokenType.True) return "true";
                if (reader.TokenType == System.Text.Json.JsonTokenType.False) return "false";
                throw new System.Text.Json.JsonException("不支持的 body JSON 类型：" + reader.TokenType);
        }
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, string? value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
