using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>五段式 cron 解析与匹配（智能体定时任务调度器使用，UTC 分钟粒度）。</summary>
public sealed class CronScheduleTests
{
    private static DateTimeOffset At(int minute, int hour, int day, int month, DayOfWeek dow)
        => new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("0 9 * * *", 0, 9, 15, 3, true)]    // 每天 9:00
    [InlineData("0 9 * * *", 1, 9, 15, 3, false)]   // 9:01 不命中
    [InlineData("0 9 * * *", 0, 8, 15, 3, false)]   // 8:00 不命中
    [InlineData("*/5 * * * *", 0, 10, 1, 1, true)]  // 每 5 分钟
    [InlineData("*/5 * * * *", 3, 10, 1, 1, false)]
    [InlineData("*/5 * * * *", 5, 10, 1, 1, true)]
    [InlineData("30 8,18 * * *", 30, 18, 20, 6, true)] // 每天 8:30 与 18:30
    [InlineData("30 8,18 * * *", 30, 9, 20, 6, false)]
    [InlineData("0 12 1 * *", 0, 12, 1, 7, true)]   // 每月 1 号 12:00
    [InlineData("0 12 1 * *", 0, 12, 2, 7, false)]
    [InlineData("0 0 * * 1", 0, 0, 8, 6, true)]     // 每周一 0:00（2026-06-08 是周一）
    [InlineData("0 0 * * 0", 0, 0, 14, 6, true)]    // 每周日 0:00（2026-06-14 是周日）
    [InlineData("0 0 * * 7", 0, 0, 14, 6, true)]    // 7 视为周日
    [InlineData("15 9-17 * * *", 15, 17, 3, 5, true)] // 9-17 点每小时的 15 分
    [InlineData("15 9-17 * * *", 15, 18, 3, 5, false)]
    [InlineData("*/10 */2 * * *", 20, 4, 3, 5, true)] // 每 2 小时的每 10 分钟
    [InlineData("*/10 */2 * * *", 25, 4, 3, 5, false)]
    public void Matches_CommonExpressions(string expr, int minute, int hour, int day, int month, bool expected)
    {
        Assert.True(CronSchedule.TryParse(expr, out var cron, out var error), $"解析失败：{expr} {error}");
        Assert.Equal(expected, cron!.Matches(At(minute, hour, day, month, new DateTime(2026, month, day).DayOfWeek)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0 9 * *")]            // 4 段
    [InlineData("0 9 * * * *")]        // 6 段
    [InlineData("61 * * * *")]         // 分钟越界
    [InlineData("* 24 * * *")]         // 小时越界
    [InlineData("* * 0 * *")]          // 日越界（1-31）
    [InlineData("* * * 13 *")]         // 月越界
    [InlineData("* * * * 8")]          // 周越界
    [InlineData("a * * * *")]          // 非数字
    [InlineData("*/0 * * * *")]        // 步长 0
    [InlineData("1- * * * *")]         // 残缺范围
    public void TryParse_InvalidExpressions_ReturnsFalse(string expr)
    {
        Assert.False(CronSchedule.TryParse(expr, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error), "非法表达式应给出错误说明");
    }

    [Fact]
    public void TryParse_Weekday7_NormalizesToSunday()
    {
        Assert.True(CronSchedule.TryParse("0 0 * * 7", out var cron, out _));
        // 2026-06-14 是周日：cron 命中（7 已归一化为 0）
        Assert.True(cron!.Matches(new DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(cron.Matches(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))); // 周一不命中
    }

    [Fact]
    public void Matches_UsesUtcMinutePrecision()
    {
        Assert.True(CronSchedule.TryParse("30 * * * *", out var cron, out _));
        Assert.True(cron!.Matches(new DateTimeOffset(2026, 3, 5, 23, 30, 59, TimeSpan.Zero))); // 秒忽略，分钟命中
        Assert.False(cron.Matches(new DateTimeOffset(2026, 3, 5, 23, 31, 0, TimeSpan.Zero)));
    }
}
