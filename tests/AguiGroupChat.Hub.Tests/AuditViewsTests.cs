using AguiGroupChat.Web;
using System.Text.Json;
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
    public void Restore_UnwrapsUsersAndReadsSharedPool()
    {
        const string json = """
            {"users":{"u1":{"views":[{"name":"A","days":5}],"defaultView":"A"}},"shared":[{"name":"S","days":10}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var state = new AuditViewsState();

        AuditViewsApi.Restore(state, doc.RootElement);

        Assert.True(state.Users.ContainsKey("u1"));
        Assert.Single(state.Users["u1"].Views!);
        Assert.Equal(5, state.Users["u1"].Views![0].Days);
        Assert.Equal("A", state.Users["u1"].DefaultView);
        Assert.Single(state.Shared);
        Assert.Equal("S", state.Shared[0].Name);
    }

    [Fact]
    public void Restore_AcceptsLegacyRootMapWithoutUsersWrapper()
    {
        const string json = """{"u1":[{"name":"A","days":7}]}""";
        using var doc = JsonDocument.Parse(json);
        var state = new AuditViewsState();

        AuditViewsApi.Restore(state, doc.RootElement);

        Assert.Single(state.Users["u1"].Views!);
        Assert.Equal("", state.Users["u1"].DefaultView);
        Assert.Empty(state.Shared);
    }

    [Fact]
    public void Restore_KeepsSharedDefaultOnlyWhenItExists()
    {
        const string ok = """
            {"users":{},"shared":[{"name":"S"}],"sharedDefaultView":"S"}
            """;
        using (var doc = JsonDocument.Parse(ok))
        {
            var state = new AuditViewsState();
            AuditViewsApi.Restore(state, doc.RootElement);
            Assert.Equal("S", state.SharedDefaultView);
        }

        const string bad = """
            {"users":{},"shared":[{"name":"S"}],"sharedDefaultView":"missing"}
            """;
        using (var doc = JsonDocument.Parse(bad))
        {
            var state = new AuditViewsState();
            AuditViewsApi.Restore(state, doc.RootElement);
            Assert.Null(state.SharedDefaultView);
        }
    }

    [Fact]
    public void Restore_KeepsUsageOnlyForSharedViews()
    {
        const string json = """
            {"users":{},"shared":[{"name":"S"}],"sharedUsage":{"S":7,"Gone":3}}
            """;
        using var doc = JsonDocument.Parse(json);
        var state = new AuditViewsState();

        AuditViewsApi.Restore(state, doc.RootElement);

        Assert.Equal(7, state.SharedUsage["S"]);
        Assert.False(state.SharedUsage.ContainsKey("Gone")); // 共享池里已不存在的计数丢弃
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
