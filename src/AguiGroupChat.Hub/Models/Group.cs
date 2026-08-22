namespace AguiGroupChat.Hub.Models;

/// <summary>群组模型（协议 2.1）。</summary>
public sealed class Group
{
    /// <summary>群组唯一标识，命名：group_xxx。</summary>
    public required string GroupId { get; init; }

    /// <summary>群组显示名称。</summary>
    public required string GroupName { get; set; }

    /// <summary>群组头像 URL。</summary>
    public string? GroupAvatar { get; set; }

    /// <summary>
    /// 是否私密群。私密群的语义记忆只允许在群内检索（智能体在其他群触发时
    /// 检索记忆会排除私密群；本群内触发不受影响）。
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>群主成员 ID。</summary>
    public required string OwnerId { get; init; }

    /// <summary>当前成员总数。</summary>
    public int MemberCount { get; set; }

    /// <summary>创建时间戳（毫秒级）。</summary>
    public required long CreateTime { get; init; }

    /// <summary>业务自定义扩展字段。</summary>
    public Dictionary<string, object?>? Extra { get; init; }
}
