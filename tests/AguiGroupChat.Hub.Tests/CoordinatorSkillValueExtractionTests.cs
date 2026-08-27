using System.Reflection;
using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>协调计划在执行“参数化技能”前，从上一步输出中提取干净输入（如 URL/地址）的提取逻辑。</summary>
public sealed class CoordinatorSkillValueExtractionTests
{
    private static string? Extract(string? text)
    {
        var m = typeof(AgentGateway).GetMethod("ExtractCleanValueForSkill", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string?)m.Invoke(null, new object[] { text! });
    }

    [Fact]
    public void ExtractsUrl_FromProseAnswer()
    {
        // 配置管理员带着解释给了地址 → 应提取出干净 URL
        var result = Extract("Exchange OWA 服务器地址是 https://mail.example.com/owa 哦。");
        Assert.Equal("https://mail.example.com/owa", result);
    }

    [Fact]
    public void ExtractsHost_PublicPath()
    {
        var result = Extract("请访问 https://mail.lingtong.com 测试");
        Assert.Equal("https://mail.lingtong.com", result);
    }

    [Fact]
    public void FallsBack_TrimWhenNoUrl()
    {
        var result = Extract("  192.168.10.5:8443  ");
        Assert.Equal("192.168.10.5:8443", result);
    }

    [Fact]
    public void ReturnsNull_OnBlank()
    {
        Assert.Null(Extract("   "));
        Assert.Null(Extract(null));
    }
}
