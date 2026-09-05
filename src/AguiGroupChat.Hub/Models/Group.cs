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
    /// 知聚类型（<see cref="GroupKind"/>）。存储于 <c>Extra["kind"]</c>（字符串化），
    /// 既有的所有群存储均可透传 extra JSON，无需新增列 / 迁移。
    /// </summary>
    public GroupKind Kind
    {
        get
        {
            // Extra 反序列化自数据库 JSON 时，值可能是 CLR string 或 JsonElement（依存储反序列化实现而定），
            // 统一归一化为字符串再比较，避免持久化后 reload 丢失 kind。
            if (Extra is null || !Extra.TryGetValue("kind", out var k) || k is null) return GroupKind.Normal;
            var s = k switch
            {
                string str => str,
                System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String => je.GetString(),
                _ => k.ToString(),
            };
            return s switch
            {
                "support" => GroupKind.Support,
                "direct" => GroupKind.Direct,
                _ => GroupKind.Normal,
            };
        }
        set
        {
            if (Extra is null) return; // Extra 在初始值为 null 时不支持原地写入（由创建路径预置）
            Extra["kind"] = value switch
            {
                GroupKind.Support => "support",
                GroupKind.Direct => "direct",
                _ => "normal",
            };
        }
    }

    /// <summary>是否客服知聚（客服可见全部消息，非客服成员只见自己的会话）。</summary>
    public bool IsSupportCircle => Kind == GroupKind.Support;

    /// <summary>是否单聊（用户 ↔ 数字员工的私有双人群）。单聊默认私密、不对外展示。</summary>
    public bool IsDirectChat => Kind == GroupKind.Direct;

    /// <summary>
    /// 是否私密群。私密群的语义记忆只允许在群内检索（智能体在其他群触发时
    /// 检索记忆会排除私密群；本群内触发不受影响）。
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>群主成员 ID。</summary>
    public required string OwnerId { get; set; }

    /// <summary>当前成员总数。</summary>
    public int MemberCount { get; set; }

    /// <summary>创建时间戳（毫秒级）。</summary>
    public required long CreateTime { get; init; }

    /// <summary>业务自定义扩展字段。</summary>
    public Dictionary<string, object?>? Extra { get; init; }
}
