using System.Text.Json;
using System.Text.Json.Serialization;

namespace AguiGroupChat.Hub.Infra;

/// <summary>
/// 全局 JSON 约定：camelCase 字段、枚举字符串化、忽略 null —— 与协议示例保持一致。
/// </summary>
public static class AguiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>把任意值包装为 JsonElement，便于写入扩展字段 / 更新字段。</summary>
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
