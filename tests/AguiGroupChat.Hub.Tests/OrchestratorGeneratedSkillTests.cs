using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 编排器生成的 dotnet 技能必须真能编译运行。
///
/// 本测试用一段<b>真实由编排器生成</b>的技能正文（见 tools/orchestrator-gen-sample.cs），
/// 走生产同款 DotnetSkillHost 编译执行，确认「调整 kind 引导后模型产出的是可用 .NET 代码」，
/// 而不是像修复前那样产出跑不通的 Python 脚本。
/// </summary>
public sealed class OrchestratorGeneratedSkillTests
{
    private static string SamplePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AguiGroupChat.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tools", "orchestrator-gen-sample.cs");
    }

    [Fact]
    public void OrchestratorGeneratedDotnetSkill_CompilesAndRuns()
    {
        var path = SamplePath();
        Assert.True(File.Exists(path), "缺少编排生成样本：" + path);
        var src = File.ReadAllText(path);

        // 编排引导的成果：正文应是 C#（而不是 python3 / shell 脚本）
        Assert.Contains("public class Skill", src);
        Assert.Contains("Run(string input)", src);
        Assert.DoesNotContain("python3", src);
        Assert.DoesNotContain("import ", src);

        var host = new DotnetSkillHost(NullLogger<DotnetSkillHost>.Instance,
            Path.Combine(Path.GetTempPath(), "agui-orch-gen-" + Guid.NewGuid().ToString("N")));
        var output = host.Run(src, "这是一段测试文本，用于统计字数。", CancellationToken.None);

        Assert.DoesNotContain("编译失败", output);
        Assert.Contains("字符数", output);
    }
}
