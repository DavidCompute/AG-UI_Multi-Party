using System.Text.Json;
using System.Text.Json.Serialization;

namespace AguiGroupChat.Sdk;

/// <summary>
/// 与 Hub 保持一致的 JSON 序列化约定：
/// camelCase 属性、忽略 null、枚举序列化为 camelCase 字符串（与协议 §2 一致）。
/// 保证 SDK 发出的请求体与 Hub 的下行事件能相互匹配。
/// </summary>
public static class AguiJson
{
    /// <summary>SDK 全局序列化选项。</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, Options);

    internal static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options);

    internal static async Task<T?> DeserializeAsync<T>(Stream stream, CancellationToken ct)
        => await JsonSerializer.DeserializeAsync<T>(stream, Options, ct).ConfigureAwait(false);
}
