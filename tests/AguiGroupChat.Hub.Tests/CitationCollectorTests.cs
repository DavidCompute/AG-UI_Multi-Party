using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>引用来源采集器（<see cref="CitationCollector"/>）：去重、摘录截断、条数上限。</summary>
public sealed class CitationCollectorTests
{
    [Fact]
    public void Add_DedupesClampsAndCaps()
    {
        var c = new CitationCollector();
        Assert.True(c.IsEmpty);

        c.Add("kb", "产品手册", "manual.pdf", 0.8123, new string('x', 500));
        c.Add("kb", "产品手册", "manual.pdf", 0.99, "dup"); // 同来源：去重

        Assert.Single(c.Items);
        Assert.Equal("kb", c.Items[0].Kind);
        Assert.Equal("产品手册", c.Items[0].Title);
        Assert.Equal("manual.pdf", c.Items[0].Detail);
        Assert.Equal(0.8123, c.Items[0].Score);
        Assert.Equal(CitationCollector.MaxSnippetChars, c.Items[0].Snippet.Length); // 摘录截断
        Assert.False(c.IsEmpty);

        // 不同来源继续收录，但受条数上限约束
        for (var i = 0; i < 30; i++) c.Add("memory", "user_" + i, "", 0.5, "s");
        Assert.Equal(CitationCollector.MaxItems, c.Items.Count);
    }
}
