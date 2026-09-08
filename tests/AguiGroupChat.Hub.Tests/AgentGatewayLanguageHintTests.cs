using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>多语言自适应回复提示的语言检测（AgentGateway.DetectReplyLanguageHint）单元测试。</summary>
public sealed class AgentGatewayLanguageHintTests
{
    [Theory]
    [InlineData("帮我写一个 V2 版本的需求大纲", true)]   // 中文主导
    [InlineData("今天知聚里讨论了发布计划，结论如下：……", true)] // 中文 + 少量拉丁
    [InlineData("Please help me draft the Q3 roadmap and budget estimate.", true)] // 英文
    [InlineData("Could you also send me the file link?", true)]
    [InlineData("すみません、昨日の議事録を教えてください。", true)] // 日文
    [InlineData("오늘 회의 요약 좀 부탁드립니다.", true)]             // 韩文
    [InlineData("123 456", false)]                         // 无字母 → 不注入
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("OK", false)]                              // 过短英文 → 不注入（避免误判）
    public void DetectReplyLanguageHint_Basic(string? content, bool hasHint)
    {
        var hint = AgentGateway.DetectReplyLanguageHint(content);
        Assert.Equal(hasHint, hint is not null);
    }

    [Theory]
    [InlineData("帮我看看这段 Python 代码 has_many 的问题", "中文")] // 中英混杂以中文为主
    [InlineData("你好 hello 世界 how are you 吗？", "中文")]
    [InlineData("Kubernetes 又部署失败了，请帮忙排查一下", "中文")]
    public void DetectReplyLanguageHint_MixedPrefersChinese(string content, string expectedLang)
    {
        var hint = AgentGateway.DetectReplyLanguageHint(content);
        Assert.NotNull(hint);
        Assert.Contains(expectedLang, hint!);
    }
}
