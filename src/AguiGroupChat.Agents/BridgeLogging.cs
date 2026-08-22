using System.Collections;
using System.Text.Json;

namespace AguiGroupChat.Agents;

/// <summary>桥接 HITL 交互日志脱敏辅助：日志只记录 payload 的字段名与值规模，不输出实际内容
/// （用户输入可能含敏感信息，序列化全文进日志属于信息泄漏）。</summary>
internal static class BridgeLogging
{
    /// <summary>把 resume payload 转成脱敏摘要（字段名 + 值类型 / 长度），不输出序列化内容。</summary>
    public static string DescribePayload(object? payload)
    {
        if (payload is null) return "null";
        if (payload is IDictionary dict)
            return string.Join(", ", dict.Keys.Cast<object?>()
                .Select(k => $"{k ?? "null"}={DescribeValue(k is null ? null : dict[k])}"));
        if (payload is JsonElement je)
            return je.ValueKind == JsonValueKind.Object
                ? string.Join(", ", je.EnumerateObject().Select(p => $"{p.Name}={DescribeValue(p.Value)}"))
                : je.ValueKind.ToString();
        return payload.GetType().Name;
    }

    private static string DescribeValue(object? v) => v switch
    {
        null => "null",
        string s => $"string({s.Length})",
        JsonElement e => DescribeJsonValue(e),
        IEnumerable => "array",
        _ => v.GetType().Name,
    };

    private static string DescribeJsonValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => $"string({v.GetString()?.Length ?? 0})",
        JsonValueKind.Array => $"array({v.GetArrayLength()})",
        JsonValueKind.Object => "object",
        _ => v.ValueKind.ToString(),
    };
}
