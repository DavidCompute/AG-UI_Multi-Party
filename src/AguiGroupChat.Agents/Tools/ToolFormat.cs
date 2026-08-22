using System.Globalization;

namespace AguiGroupChat.Agents.Tools;

/// <summary>工具结果数字格式化：整数不显示小数点，小数最多 10 位有效，极大 / 极小值走科学计数。</summary>
internal static class ToolFormat
{
    public static string Number(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "非有限数值";
        if (value == 0) return "0";
        if (Math.Abs(value) >= 1e15 || Math.Abs(value) < 1e-9)
            return value.ToString("G10", CultureInfo.InvariantCulture);
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }
}
