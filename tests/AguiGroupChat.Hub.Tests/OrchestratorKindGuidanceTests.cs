using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 组织编排提示词里的「技能类型（kind）引导」。
///
/// 回归背景：早先编排提示词只给了 prompt / shell / http 三种，<b>完全没提 dotnet</b>，
/// 且说“操作文件/磁盘 → 用 shell”。于是模型要为「导出 Word 文档」造技能时只能选 shell，
/// 写不出脚本就去写 Python（python3 + python-docx）—— 而服务端容器根本没有 Python，
/// 那个技能一跑就是 command not found / 退出码 127。本仓库真实踩到过。
/// </summary>
public sealed class OrchestratorKindGuidanceTests
{
    [Fact]
    public void Prompt_MentionsDotnetForDocumentWork()
    {
        var prompt = AgentOrchestrator.BuildPromptForTest("做一个文案推广组，定稿后要能导出 Word 文档", null);

        // dotnet 必须出现，且明确指向文档/图片/计算这类“要真干活”的能力
        Assert.Contains("dotnet", prompt);
        Assert.Contains("C# 源码", prompt);
        Assert.Contains("Run(string input)", prompt);
        // 必须给出可编译的写法提示与 NuGet 引用方式
        Assert.Contains("#r", prompt);
    }

    [Fact]
    public void Prompt_ForbidsShellForDocumentWork()
    {
        var prompt = AgentOrchestrator.BuildPromptForTest("需要生成 Word 报告", null);

        // 明确禁止把文档处理之类的活写成 shell（这正是当初翻车的路径）
        Assert.Contains("禁则", prompt);
        Assert.Contains("shell", prompt);
    }

    [Fact]
    public void Prompt_CarriesSandboxRuntimeList()
    {
        // 沙箱真实能力必须进提示词，否则模型仍会写出 python3 之类跑不通的脚本
        var prompt = AgentOrchestrator.BuildPromptForTest("随便建个组", null);
        Assert.Contains("python", prompt);

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Assert.Contains("不可用", prompt);
            Assert.Contains("node", prompt);
        }
    }

    [Fact]
    public void Prompt_ForbidsDotnetWhenCallerLacksPermission()
    {
        // dotnet 仅系统管理员可建（OrgApplyEngine 会校验）。
        // 非管理员编排时必须引导模型避开，否则选了 dotnet 会在落库阶段被直接拒掉。
        var prompt = AgentOrchestrator.BuildPromptForTest("做一个推广组", null, allowDotnet: false);

        Assert.Contains("禁止", prompt);
        Assert.Contains("dotnet", prompt);
        Assert.Contains("系统管理员", prompt);
        // 非管理员分支不应再出现“优先选 dotnet”的引导
        Assert.DoesNotContain("优先选它", prompt);
    }

    [Fact]
    public void Prompt_AdminBranchSaysPreferDotnet()
    {
        var admin = AgentOrchestrator.BuildPromptForTest("做一个推广组", null, allowDotnet: true);
        Assert.Contains("优先选它", admin);
        Assert.DoesNotContain("禁止", admin.Replace("禁则将", "XX"));
    }

    [Fact]
    public void Prompt_KeepsMemoryProfileAndConnectionGuidance()
    {
        // 改动 kind 段不应破坏原有的记忆档位与连接原则（回归护栏）
        var prompt = AgentOrchestrator.BuildPromptForTest("建组", null);
        Assert.Contains("memoryProfile", prompt);
        Assert.Contains("broad", prompt);
        Assert.Contains("deep", prompt);
        Assert.Contains("assignmentIds", prompt);
        Assert.Contains("escalationAgentId", prompt);
        // 双向连接的要求（曾修过“只有问题提升没有任务指派”的缺陷）
        Assert.Contains("任务指派", prompt);
        Assert.Contains("问题提升", prompt);
    }
}
