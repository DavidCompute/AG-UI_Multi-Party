namespace AguiGroupChat.Sdk.Models;

/// <summary>
/// 新增 / 更新智能体的请求体（POST/PUT /ag-ui/agents）。字段与 Hub 的 AgentUpsertHttpRequest 对齐。
/// </summary>
public sealed class AgentUpsertRequest
{
    /// <summary>可空：POST 时留空由服务端生成 agent_xxx；PUT 路径段决定 agentId。</summary>
    public string? AgentId { get; set; }
    public required string Nickname { get; set; }
    public string? Description { get; set; }
    public string? Instructions { get; set; }
    public string? Avatar { get; set; }

    /// <summary>触发模式：mentioned / allMessages / keyword / contextual（字符串，与 Hub 目录一致）。</summary>
    public string? TriggerMode { get; set; }
    public IReadOnlyList<string>? Keywords { get; set; }
    public string? Schedule { get; set; }
    public string? Model { get; set; }

    /// <summary>外部 AG-UI 桥接端点（仅系统管理员可配置）。</summary>
    public string? BridgeEndpoint { get; set; }
    public string? BridgeMode { get; set; }
    public string? BridgeToken { get; set; }

    public bool? PersonalMemoryEnabled { get; set; }
    public bool? IsPrivate { get; set; }

    public IReadOnlyList<string>? Skills { get; set; }
    public IReadOnlyList<string>? KnowledgeBaseIds { get; set; }
    public IReadOnlyList<string>? RequireApprovalToolNames { get; set; }
    public object? Pipeline { get; set; }
    public string? RelayToAgentId { get; set; }
}
