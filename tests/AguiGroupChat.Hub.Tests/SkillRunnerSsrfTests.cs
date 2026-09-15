using AguiGroupChat.Agents;
using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 附件 ID → 服务器路径的解析（att_xxx）。
///
/// <para>
/// 模型只看得到附件 ID，而 docx/pptx/xlsx/pdf 技能吃的是<b>路径</b>。
/// 没有这层解析，“用我上传的模板/文档出一份稿”走到技能那一步就断了：
/// 技能收到字面量 att_xxx，只能报“找不到文件”。
/// </para>
/// </summary>
public sealed class SkillRunnerAttachmentTests
{
    /// <summary>把入参原样回显的技能，用来观察“技能到底收到了什么”。</summary>
    private const string EchoSkill = "public class Skill { public static string Run(string input) => input; }";

    private static SkillRunner Runner(Func<string, string?> resolver)
        => new SkillRunner(
            Path.Combine(Path.GetTempPath(), "agui-skill-att-" + Guid.NewGuid().ToString("N")),
            NullLoggerFactory.Instance,
            allowPrivateEndpoints: false,
            resolveAttachment: resolver);

    private static AgentSkillDefinition Dotnet(string body)
        => new() { SkillId = "att_echo", Name = "att_echo", Kind = AgentSkillKind.Dotnet, Body = body };

    [Fact]
    public async Task DotnetSkill_SeesResolvedPathInsteadOfAttachmentId()
    {
        var file = Path.Combine(Path.GetTempPath(), "agui-att-" + Guid.NewGuid().ToString("N") + ".pptx");
        File.WriteAllText(file, "x");
        var runner = Runner(id => id == "att_abc123" ? file : null);

        var res = await runner.InvokeAsync(Dotnet(EchoSkill),
            "{\"template\":\"att_abc123\"}", CancellationToken.None);

        Assert.Contains(file, res);
        Assert.DoesNotContain("att_abc123", res);
    }

    /// <summary>解析不到的 ID 必须原样保留：技能自己会报“找不到文件”，而不是被换成别的东西。</summary>
    [Fact]
    public async Task UnknownAttachmentId_IsPassedThroughUnchanged()
    {
        var runner = Runner(_ => null);
        var res = await runner.InvokeAsync(Dotnet(EchoSkill),
            "{\"template\":\"att_missing999\"}", CancellationToken.None);
        Assert.Contains("att_missing999", res);
    }

    /// <summary>解析器抛异常不能把技能调用搞挂。</summary>
    [Fact]
    public async Task ThrowingResolver_FallsBackToRawId()
    {
        var runner = Runner(_ => throw new InvalidOperationException("boom"));
        var res = await runner.InvokeAsync(Dotnet(EchoSkill),
            "{\"template\":\"att_boom123\"}", CancellationToken.None);
        Assert.Contains("att_boom123", res);
    }

    /// <summary>没注入解析器（如未部署附件存储）时，行为与从前完全一致。</summary>
    [Fact]
    public async Task WithoutResolver_QueryIsUntouched()
    {
        var runner = new SkillRunner(
            Path.Combine(Path.GetTempPath(), "agui-skill-att-" + Guid.NewGuid().ToString("N")),
            NullLoggerFactory.Instance);
        var res = await runner.InvokeAsync(Dotnet(EchoSkill),
            "{\"template\":\"att_notouch1\"}", CancellationToken.None);
        Assert.Contains("att_notouch1", res);
    }
}

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
