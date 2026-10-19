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

    /// <summary>
    /// 本知聚的<b>模型覆写</b>（留空 = 不覆写）：管理员可给某个知聚钉一个模型，
    /// 例如给高消耗知聚钉 <c>deepseek-v4-pro</c>（要质量），或钉 <c>deepseek-flash</c>（省成本）。
    /// <b>优先级最高</b>——覆盖「按任务复杂度自动选模型」（<c>Agents:ModelRouting</c>）与该知聚内智能体的默认模型。
    /// 存于 <c>Extra["model"]</c>（字符串），既有所有群存储均透传 extra JSON，无需新增列 / 迁移（同 <see cref="Kind"/>）。
    /// </summary>
    public string? ModelOverride
    {
        get => Extra is not null && Extra.TryGetValue("model", out var v) ? StrOf(v) : null;
        set
        {
            var s = (value ?? "").Trim();
            if (s.Length == 0) Extra.Remove("model"); // 空 = 取消覆写
            else Extra["model"] = s;
        }
    }

    /// <summary>Extra 值归一化为字符串：兼容 CLR string / JsonElement（存储反序列化可能给出任一形态）。</summary>
    private static string? StrOf(object? v) => v switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s,
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String =>
            string.IsNullOrWhiteSpace(je.GetString()) ? null : je.GetString(),
        System.Text.Json.JsonElement je when je.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
        _ => v.ToString(),
    };

    /// <summary>
    /// 本知聚的<b>每日 Token 配额覆写</b>（<c>null</c> = 用全局 <c>Agents:DailyTokenQuotaPerGroup</c>）。
    /// 为什么要按知聚覆写：全局配额对全部知聚一刀切，但知聚之间开销差异极大（客服知聚接待顾客 vs 内部小群）；
    /// 管理员可给某知聚单独放额（例如设为 <c>0</c> = 本知聚不限），而不必抬高全平台上限。
    /// 存于 <c>Extra["quotaPerGroup"]</c>（字符串），既有所有群存储均透传 extra JSON，无需新增列 / 迁移（同 <see cref="ModelOverride"/>）。
    /// </summary>
    public long? QuotaOverride
    {
        get => Extra is not null && Extra.TryGetValue("quotaPerGroup", out var v) ? LongOf(v) : null;
        set
        {
            if (value is not { } n || n < 0)
            {
                Extra.Remove("quotaPerGroup");       // null / 负数 = 取消覆写
                Extra.Remove("quotaPerGroupUntil");  // 取消时一并清到期时间（不留孤儿）
            }
            else Extra["quotaPerGroup"] = n.ToString(); // 纯整数默认格式即无分隔符，与 culture 无关
        }
    }

    /// <summary>
    /// 配额覆写的**到期时间**（UTC 毫秒；<c>null</c> = 长期有效）。
    /// 为什么需要：给知聚「临时放额」（如大促 / 演示），到期**自动回退全局配额**，不需要人工记得改回来。
    /// 失效是**惰性**的（读时判定，无后台任务）：超时后 <see cref="IsQuotaOverrideActive"/> 返回 false，
    /// 存储里的旧值保留（管理员仍能在设置里看到当时设了多少）。
    /// 存于 <c>Extra["quotaPerGroupUntil"]</c>（字符串）。
    /// </summary>
    public long? QuotaOverrideUntil
    {
        get => Extra is not null && Extra.TryGetValue("quotaPerGroupUntil", out var v) ? LongOf(v) : null;
        set
        {
            if (value is not { } n || n <= 0) Extra.Remove("quotaPerGroupUntil");
            else Extra["quotaPerGroupUntil"] = n.ToString();
        }
    }

    /// <summary>在给定时刻（UTC 毫秒）覆写是否仍生效：需有值，且无到期时间或尚未到期。</summary>
    public bool IsQuotaOverrideActive(long nowMs)
        => QuotaOverride is not null && (QuotaOverrideUntil is not { } u || u > nowMs);

    /// <summary>Extra 值归一化为可空 long：兼容 CLR 数值 / JsonElement（number 或纯数字字符串）。非数字 = null（视为未覆写）。</summary>
    private static long? LongOf(object? v) => v switch
    {
        null => null,
        long l => l,
        int i => i,
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number
            => je.TryGetInt64(out var n) ? n : null,
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String
            => long.TryParse(je.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null,
        string s => long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null,
        _ => null,
    };

    /// <summary>群主成员 ID。</summary>
    public required string OwnerId { get; set; }

    /// <summary>当前成员总数。</summary>
    public int MemberCount { get; set; }

    /// <summary>创建时间戳（毫秒级）。</summary>
    public required long CreateTime { get; init; }

    private Dictionary<string, object?>? _extra;

    /// <summary>
    /// 业务自定义扩展字段。**读写均不会为 null**：旧数据库行 / 导入包里 `extra` 缺省时也能安全写入
    /// <see cref="Kind"/> / <see cref="ModelOverride"/> / <see cref="QuotaOverride"/>。
    /// 否则这些属性的 setter 会因「Extra 为 null」而**静默 no-op**——接口返回 200 但实际没改（1.0.271 修）。
    /// </summary>
    public Dictionary<string, object?> Extra
    {
        get => _extra ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        set => _extra = value ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }
}
