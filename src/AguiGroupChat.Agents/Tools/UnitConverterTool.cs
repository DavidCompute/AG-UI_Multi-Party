namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// 单位换算工具：长度 / 质量 / 温度 / 时间 / 数据量 / 速度。
/// 统一换算到基准单位（温度含偏移：C/F/K），支持常见全名别名（kilometer→km、pound→lb 等）。
/// </summary>
public static class UnitConverterTool
{
    private sealed record UnitInfo(string Category, double Factor, double Offset);

    private static readonly Dictionary<string, UnitInfo> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        // 长度（基准 m）
        ["m"] = new("length", 1, 0), ["meter"] = new("length", 1, 0), ["meters"] = new("length", 1, 0), ["米"] = new("length", 1, 0),
        ["km"] = new("length", 1000, 0), ["kilometer"] = new("length", 1000, 0), ["kilometers"] = new("length", 1000, 0), ["千米"] = new("length", 1000, 0), ["公里"] = new("length", 1000, 0),
        ["cm"] = new("length", 0.01, 0), ["centimeter"] = new("length", 0.01, 0), ["厘米"] = new("length", 0.01, 0),
        ["mm"] = new("length", 0.001, 0), ["millimeter"] = new("length", 0.001, 0), ["毫米"] = new("length", 0.001, 0),
        ["mile"] = new("length", 1609.344, 0), ["miles"] = new("length", 1609.344, 0), ["英里"] = new("length", 1609.344, 0),
        ["yard"] = new("length", 0.9144, 0), ["yards"] = new("length", 0.9144, 0), ["码"] = new("length", 0.9144, 0),
        ["foot"] = new("length", 0.3048, 0), ["feet"] = new("length", 0.3048, 0), ["ft"] = new("length", 0.3048, 0), ["英尺"] = new("length", 0.3048, 0),
        ["inch"] = new("length", 0.0254, 0), ["inches"] = new("length", 0.0254, 0), ["in"] = new("length", 0.0254, 0), ["英寸"] = new("length", 0.0254, 0),
        // 质量（基准 kg）
        ["kg"] = new("mass", 1, 0), ["kilogram"] = new("mass", 1, 0), ["kilograms"] = new("mass", 1, 0), ["千克"] = new("mass", 1, 0), ["公斤"] = new("mass", 1, 0),
        ["g"] = new("mass", 0.001, 0), ["gram"] = new("mass", 0.001, 0), ["grams"] = new("mass", 0.001, 0), ["克"] = new("mass", 0.001, 0),
        ["mg"] = new("mass", 1e-6, 0), ["milligram"] = new("mass", 1e-6, 0), ["毫克"] = new("mass", 1e-6, 0),
        ["t"] = new("mass", 1000, 0), ["ton"] = new("mass", 1000, 0), ["tonne"] = new("mass", 1000, 0), ["吨"] = new("mass", 1000, 0),
        ["lb"] = new("mass", 0.45359237, 0), ["lbs"] = new("mass", 0.45359237, 0), ["pound"] = new("mass", 0.45359237, 0), ["pounds"] = new("mass", 0.45359237, 0), ["磅"] = new("mass", 0.45359237, 0),
        ["oz"] = new("mass", 0.028349523125, 0), ["ounce"] = new("mass", 0.028349523125, 0), ["ounces"] = new("mass", 0.028349523125, 0), ["盎司"] = new("mass", 0.028349523125, 0),
        // 温度（基准 K，偏移量保证 C/F/K 互转正确）
        ["c"] = new("temperature", 1, 0), ["celsius"] = new("temperature", 1, 0), ["摄氏度"] = new("temperature", 1, 0), ["℃"] = new("temperature", 1, 0),
        ["f"] = new("temperature", 5.0 / 9, -160.0 / 9), ["fahrenheit"] = new("temperature", 5.0 / 9, -160.0 / 9), ["华氏度"] = new("temperature", 5.0 / 9, -160.0 / 9), ["℉"] = new("temperature", 5.0 / 9, -160.0 / 9),
        ["k"] = new("temperature", 1, -273.15), ["kelvin"] = new("temperature", 1, -273.15), ["开尔文"] = new("temperature", 1, -273.15),
        // 时间（基准 s）
        ["s"] = new("time", 1, 0), ["sec"] = new("time", 1, 0), ["second"] = new("time", 1, 0), ["seconds"] = new("time", 1, 0), ["秒"] = new("time", 1, 0),
        ["ms"] = new("time", 0.001, 0), ["millisecond"] = new("time", 0.001, 0), ["毫秒"] = new("time", 0.001, 0),
        ["min"] = new("time", 60, 0), ["minute"] = new("time", 60, 0), ["minutes"] = new("time", 60, 0), ["分"] = new("time", 60, 0), ["分钟"] = new("time", 60, 0),
        ["h"] = new("time", 3600, 0), ["hr"] = new("time", 3600, 0), ["hour"] = new("time", 3600, 0), ["hours"] = new("time", 3600, 0), ["时"] = new("time", 3600, 0), ["小时"] = new("time", 3600, 0),
        ["day"] = new("time", 86400, 0), ["days"] = new("time", 86400, 0), ["天"] = new("time", 86400, 0),
        // 数据量（基准 B，二进制 1024）
        ["b"] = new("data", 1, 0), ["byte"] = new("data", 1, 0), ["bytes"] = new("data", 1, 0), ["字节"] = new("data", 1, 0),
        ["kb"] = new("data", 1024, 0), ["kilobyte"] = new("data", 1024, 0), ["千字节"] = new("data", 1024, 0),
        ["mb"] = new("data", 1024 * 1024, 0), ["megabyte"] = new("data", 1024 * 1024, 0), ["兆字节"] = new("data", 1024 * 1024, 0),
        ["gb"] = new("data", 1024.0 * 1024 * 1024, 0), ["gigabyte"] = new("data", 1024.0 * 1024 * 1024, 0), ["吉字节"] = new("data", 1024.0 * 1024 * 1024, 0),
        ["tb"] = new("data", 1024.0 * 1024 * 1024 * 1024, 0), ["terabyte"] = new("data", 1024.0 * 1024 * 1024 * 1024, 0),
        ["bit"] = new("data", 0.125, 0), ["bits"] = new("data", 0.125, 0), ["比特"] = new("data", 0.125, 0),
        // 速度（基准 m/s）
        ["mps"] = new("speed", 1, 0), ["m/s"] = new("speed", 1, 0), ["米每秒"] = new("speed", 1, 0),
        ["kmh"] = new("speed", 1.0 / 3.6, 0), ["kph"] = new("speed", 1.0 / 3.6, 0), ["km/h"] = new("speed", 1.0 / 3.6, 0), ["公里每小时"] = new("speed", 1.0 / 3.6, 0),
        ["mph"] = new("speed", 0.44704, 0), ["英里每小时"] = new("speed", 0.44704, 0),
    };

    /// <summary>单位换算：value 数值、from 原单位、to 目标单位；类别不匹配 / 未知单位返回错误说明。</summary>
    public static string Convert(double value, string from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return "请提供 from（原单位）与 to（目标单位）。";
        if (!Units.TryGetValue(from.Trim(), out var source)) return $"未知单位：{from}";
        if (!Units.TryGetValue(to.Trim(), out var target)) return $"未知单位：{to}";
        if (!string.Equals(source.Category, target.Category, StringComparison.OrdinalIgnoreCase))
            return $"{from}（{source.Category}）与 {to}（{target.Category}）类别不同，不可换算";
        var baseValue = value * source.Factor + source.Offset;
        var result = (baseValue - target.Offset) / target.Factor;
        return $"{ToolFormat.Number(value)} {from} = {ToolFormat.Number(result)} {to}";
    }
}
