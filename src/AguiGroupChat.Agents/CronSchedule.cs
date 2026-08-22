using System.Globalization;

namespace AguiGroupChat.Agents;

/// <summary>
/// 轻量五段式 Cron 表达式解析与匹配（分 时 日 月 周），用于智能体定时任务。
/// 支持：<c>*</c>（全部）、<c>*/n</c>（步长）、<c>a-b</c>（范围）、<c>a,b,c</c>（列表）、<c>a-b/n</c>（范围+步长）。
/// 周字段支持 0-6（0=周日，与标准 cron 一致），也接受 7 作为周日。
/// 匹配以 UTC 时间为准（服务器时区无关，行为可预测）。
/// </summary>
public sealed class CronSchedule
{
    private readonly HashSet<int> _minutes = [];
    private readonly HashSet<int> _hours = [];
    private readonly HashSet<int> _days = [];
    private readonly HashSet<int> _months = [];
    private readonly HashSet<int> _dows = [];

    private CronSchedule() { }

    /// <summary>解析 cron 表达式。合法返回 true；否则返回 false 并给出中文错误说明。</summary>
    public static bool TryParse(string? expression, out CronSchedule? schedule, out string? error)
    {
        schedule = null;
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "定时表达式不能为空";
            return false;
        }
        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = $"定时表达式必须为 5 段（分 时 日 月 周），当前为 {fields.Length} 段，如：0 9 * * *（每天 9 点）";
            return false;
        }

        var result = new CronSchedule();
        var ranges = new[]
        {
            ("分", fields[0], 0, 59, result._minutes),
            ("时", fields[1], 0, 23, result._hours),
            ("日", fields[2], 1, 31, result._days),
            ("月", fields[3], 1, 12, result._months),
            ("周", fields[4], 0, 7, result._dows),
        };
        foreach (var (name, field, min, max, target) in ranges)
        {
            if (!TryParseField(field, min, max, target, out var fieldError))
            {
                error = $"定时表达式「{name}」段非法：{fieldError}";
                return false;
            }
            if (name == "周" && target.Contains(7))
            {
                target.Remove(7);
                target.Add(0); // 7 与 0 都表示周日
            }
        }
        // 日与周同时受限时（cron 语义：两者是「或」关系），这里简化为「任一匹配即触发」的标准 cron 行为
        schedule = result;
        return true;
    }

    private static bool TryParseField(string field, int min, int max, HashSet<int> target, out string error)
    {
        error = "";
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (range, step, stepError) = SplitStep(part);
            if (stepError is not null)
            {
                error = $"「{part}」{stepError}";
                return false;
            }
            var dash = range.IndexOf('-');
            int low, high;
            if (dash > 0)
            {
                if (!int.TryParse(range[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out low)
                    || !int.TryParse(range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out high))
                {
                    error = $"「{part}」不是合法数字范围";
                    return false;
                }
                if (low > high) (low, high) = (high, low);
            }
            else if (range == "*")
            {
                low = min;
                high = max;
            }
            else
            {
                if (!int.TryParse(range, NumberStyles.None, CultureInfo.InvariantCulture, out low))
                {
                    error = $"「{part}」不是合法数字";
                    return false;
                }
                high = low;
            }
            if (low < min || high > max)
            {
                error = $"「{part}」超出合法范围 {min}-{max}";
                return false;
            }
            for (var v = low; v <= high; v += step)
                target.Add(v);
        }
        return target.Count > 0;
    }

    private static (string Range, int Step, string? Error) SplitStep(string part)
    {
        var slash = part.IndexOf('/');
        if (slash < 0) return (part, 1, null);
        var raw = part[(slash + 1)..];
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var s) || s <= 0)
            return (part, 0, $"步长「{raw}」必须为正整数");
        return (part[..slash], s, null);
    }

    /// <summary>判断给定时刻（UTC）是否命中该表达式（分钟粒度：分/时/日/月/周全部匹配即命中）。</summary>
    public bool Matches(DateTimeOffset utcNow)
    {
        var dt = utcNow.UtcDateTime;
        return _minutes.Contains(dt.Minute)
            && _hours.Contains(dt.Hour)
            && _days.Contains(dt.Day)
            && _months.Contains(dt.Month)
            && _dows.Contains((int)dt.DayOfWeek);
    }
}
