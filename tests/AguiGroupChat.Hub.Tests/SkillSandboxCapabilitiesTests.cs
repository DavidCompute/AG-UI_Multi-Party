using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能生成 / 修复的提示词必须带上<b>服务端沙箱的真实能力清单</b>。
///
/// 回归背景：早先提示词只说“目标是 Linux，写 bash 语法”，没说沙箱里到底装了什么。
/// 模型在“生成 Word / 处理图片 / 解析 JSON”这类需求上会很自然地选 Python
/// （<c>python3 - &lt;&lt;'PY' ... from docx import Document</c>），而镜像里根本没有 python3，
/// 技能一跑就是 command not found / 退出码 127。
/// 实测踩到过：技能库里出现自建的 shell 技能 docx_report_2（Python + python-docx），必然失败。
/// </summary>
public sealed class SkillSandboxCapabilitiesTests
{
    [Fact]
    public void GenerationPrompt_WarnsAboutMissingRuntimes()
    {
        var prompt = SkillDefinitionGenerator.BuildPromptForTest("把 Markdown 转成 Word 文档", preferClient: false, allowDotnet: true);

        // 缺失清单必须点名那些最容易被误用的（写 python 缺 python 是最典型的翻车）
        Assert.Contains("python", prompt);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Assert.Contains("不可用", prompt);
            Assert.Contains("node", prompt);
            Assert.Contains("pwsh", prompt);
        }
        // 并且必须给出可行的替代路线，而不是只说“不许用”
        Assert.Contains("dotnet", prompt);
    }

    [Fact]
    public void GenerationPrompt_UsesGenericWordingOutsideContainer()
    {
        // Windows 桌面版 / macOS 自托管：宿主即用户本机，能力取决于该机安装情况，
        // 不能把容器清单写死成“可用/不可用”。
        var prompt = SkillDefinitionGenerator.BuildPromptForTest("查一下本机进程", preferClient: false, allowDotnet: true);
        var d = SkillSandboxCapabilities.Describe();

        Assert.False(string.IsNullOrWhiteSpace(d));
        Assert.Contains("dotnet", d); // 无论哪个平台都要给 C# 这条退路
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.DoesNotContain("Linux 容器", d);
        else
            Assert.Contains("Linux 容器", d);
    }

    [Fact]
    public void Describe_ForClientTarget_DoesNotImposeServerContainerList()
    {
        // 回归：本机(client)技能跑在用户自己的机器上，能力未知。
        // 早先无条件拼接服务端容器清单，会让本机技能被错误告知“没有 pwsh/python”，引导出错误方案。
        var client = SkillSandboxCapabilities.Describe(forClient: true);
        Assert.DoesNotContain("Linux 容器", client);
        Assert.DoesNotContain("command not found", client);
        Assert.Contains("本机", client);
        Assert.Contains("dotnet", client); // 仍要给不依赖外部命令的退路
    }

    [Fact]
    public void GenerationPrompt_ForServerTarget_KeepsContainerList()
    {
        // server 目标才套容器清单（Docker 部署下它就是那个 Linux 容器）。
        var server = SkillSandboxCapabilities.Describe(forClient: false);
        var client = SkillSandboxCapabilities.Describe(forClient: true);
        Assert.NotEqual(server, client);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            Assert.Contains("Linux 容器", server);
    }

    [Fact]
    public void Describe_NeverClaimsPythonIsAvailable()
    {
        // 核心不变量：任何平台都不得暗示 python 可直接用 —— 这正是那个失败技能的成因。
        var d = SkillSandboxCapabilities.Describe();
        Assert.Contains("python", d);
        Assert.DoesNotContain("可用】" + "python", d);
    }

    [Fact]
    public void Describe_InitiallyBoundsShellAndPointsToDotnet()
    {
        // shell 只该做“bash + coreutils + curl + perl 够用”的事；需要 Python 生态时必须转 dotnet。
        var d = SkillSandboxCapabilities.Describe();
        Assert.Contains("dotnet", d);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Assert.Contains("bash", d);
            Assert.Contains("curl", d);
            Assert.Contains("kind=dotnet", d);
            // 容器里不允许现场装包（无 apt/pip/npm 安装权限）
            Assert.Contains("apt", d);
        }
    }
}
