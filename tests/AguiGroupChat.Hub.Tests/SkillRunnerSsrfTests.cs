using AguiGroupChat.Agents;
using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>HTTP 技能 SSRF 防护与「AllowPrivateSkillEndpoints」放行开关。</summary>
public sealed class SkillRunnerSsrfTests
{
    private static SkillRunner Runner(bool allowPrivate)
        => new SkillRunner(
            Path.Combine(Path.GetTempPath(), "agui-skill-ssrf-" + Guid.NewGuid().ToString("N")),
            NullLoggerFactory.Instance,
            allowPrivateEndpoints: allowPrivate);

    [Theory]
    [InlineData("http://127.0.0.1:8080/", true)]
    [InlineData("http://localhost:11434/", true)]
    [InlineData("http://10.0.0.5/", true)]
    [InlineData("http://172.16.2.3/", true)]
    [InlineData("http://192.168.1.10/", true)]
    [InlineData("http://169.254.169.254/", true)]
    [InlineData("http://8.8.8.8/", false)]
    [InlineData("https://example.com/", false)]
    public void Default_Deny_Private_and_Loopback(string url, bool denied)
    {
        var r = Runner(allowPrivate: false);
        Assert.Equal(denied, r.IsPrivateOrLoopback(new Uri(url)));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://192.168.1.10/")]
    [InlineData("http://10.0.0.5/")]
    public void AllowPrivate_True_Permits_Internal(string url)
    {
        var r = Runner(allowPrivate: true);
        Assert.False(r.IsPrivateOrLoopback(new Uri(url))); // 放行
    }

    [Fact]
    public void AllowPrivate_True_Still_Permits_Public()
    {
        var r = Runner(allowPrivate: true);
        Assert.False(r.IsPrivateOrLoopback(new Uri("https://example.com/")));
    }
}
