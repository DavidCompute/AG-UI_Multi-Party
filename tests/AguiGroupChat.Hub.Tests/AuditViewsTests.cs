using AguiGroupChat.Web;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>审计筛选视图（1.0.328）：服务端清洗规则——名称必填、天数钳制、工具白名单、日期格式、上限 20。</summary>
public sealed class AuditViewsTests
{
    [Fact]
    public void Clean_TrimsClampsAndOverwritesSameName()
    {
        var views = new List<AuditViewDefinition>
        {
            new() { Name = "  按员工看命令  ", Days = 0, Tool = "command" },
            new() { Name = "按员工看命令", Days = 999, Tool = "bogus", Date = "2026-13-99", Q = new string('x', 300) },
            new() { Name = "   ", Days = 7 },
            new() { Name = "按天", Days = 30, Tool = "other", Date = "2026-10-01", GroupByMsg = true },
        };

        var cleaned = AuditViewsApi.Clean(views);

        Assert.Equal(2, cleaned.Count);
        var first = cleaned[0];
        Assert.Equal("按员工看命令", first.Name);
        Assert.Equal(90, first.Days);       // 999 → 90
        Assert.Equal("", first.Tool);       // 未知工具 → 清空
        Assert.Equal("", first.Date);       // 非法日期 → 清空
        Assert.Equal(200, first.Q!.Length); // 关键词截断

        var second = cleaned[1];
        Assert.Equal("按天", second.Name);
        Assert.Equal(30, second.Days);
        Assert.Equal("other", second.Tool);
        Assert.Equal("2026-10-01", second.Date);
        Assert.True(second.GroupByMsg);
    }

    [Fact]
    public void Clean_DefaultsInvalidDaysAndCapsCount()
    {
        var negative = AuditViewsApi.Clean([new AuditViewDefinition { Name = "n", Days = -5 }]);
        Assert.Equal(7, negative[0].Days); // ≤0 回落到 7

        var many = Enumerable.Range(0, 25)
            .Select((i) => new AuditViewDefinition { Name = "v" + i })
            .ToList();
        Assert.Equal(20, AuditViewsApi.Clean(many).Count);
    }

    [Fact]
    public void Clean_TruncatesLongNames()
    {
        var longName = new string('a', 40);
        var cleaned = AuditViewsApi.Clean([new AuditViewDefinition { Name = longName }]);
        Assert.Equal(30, cleaned[0].Name!.Length);
    }

    [Fact]
    public void CleanDefault_RequiresAnExistingView()
    {
        var views = AuditViewsApi.Clean([
            new AuditViewDefinition { Name = "A" },
            new AuditViewDefinition { Name = "B" },
        ]);

        Assert.Equal("B", AuditViewsApi.CleanDefault("  B  ", views));
        Assert.Equal("", AuditViewsApi.CleanDefault("missing", views));
        Assert.Equal("", AuditViewsApi.CleanDefault(null, views));
        Assert.Equal("", AuditViewsApi.CleanDefault("", views));
    }
}
