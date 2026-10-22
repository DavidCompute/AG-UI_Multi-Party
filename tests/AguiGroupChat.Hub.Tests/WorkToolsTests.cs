using AguiGroupChat.Agents;
using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>工作型工具（work_*）：工作区沙箱、路径越界、写读回环、命令白名单与元字符拦截。</summary>
public sealed class WorkToolsTests
{
    private static (WorkTools Tools, string Root) Create(AgentOptions? options = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "agui-work-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var tools = new WorkTools(options ?? new AgentOptions(), root, "agent_test", NullLoggerFactory.Instance);
        return (tools, root);
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* 忽略 */ }
    }

    [Fact]
    public async Task WriteReadList_Roundtrip()
    {
        var (tools, root) = Create();
        try
        {
            var write = await tools.WriteFile("notes/a.txt", "hello 世界");
            Assert.Contains("已写入", write);
            Assert.Contains("hello 世界", tools.ReadFile("notes/a.txt"));
            Assert.Contains("a.txt", tools.ListDir("notes"));
            Assert.Contains("notes/", tools.ListDir());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task PathTraversal_AndAbsolutePaths_Rejected()
    {
        var (tools, root) = Create();
        try
        {
            var parent = Path.GetDirectoryName(root)!;
            var outside = Path.Combine(parent, "outside-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            await File.WriteAllTextAsync(outside, "secret");
            try
            {
                Assert.Contains("越出工作区", tools.ReadFile("../" + Path.GetFileName(outside)));
                Assert.Contains("相对路径", await tools.WriteFile(Path.Combine(root, "x.txt"), "x"));
            }
            finally { try { File.Delete(outside); } catch { /* 忽略 */ } }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task WriteFile_OverLimit_Rejected()
    {
        var options = new AgentOptions { WorkToolMaxWriteChars = 5 };
        var (tools, root) = Create(options);
        try
        {
            Assert.Contains("过长", await tools.WriteFile("a.txt", "123456"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task RunCommand_WithoutAllowlist_Rejected()
    {
        var (tools, root) = Create();
        try
        {
            Assert.Contains("未配置", await tools.RunCommand("whoami"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task RunCommand_MetaCharacters_Rejected()
    {
        var options = new AgentOptions { WorkToolCommands = "whoami" };
        var (tools, root) = Create(options);
        try
        {
            Assert.Contains("元字符", await tools.RunCommand("whoami & echo x"));
            Assert.Contains("元字符", await tools.RunCommand("whoami | more"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task RunCommand_Path_Rejected()
    {
        var options = new AgentOptions { WorkToolCommands = "whoami" };
        var (tools, root) = Create(options);
        try
        {
            var path = OperatingSystem.IsWindows() ? @"C:\Windows\System32\whoami.exe" : "/usr/bin/whoami";
            Assert.Contains("不允许路径", await tools.RunCommand(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task RunCommand_NotAllowlisted_Rejected()
    {
        var options = new AgentOptions { WorkToolCommands = "dotnet" };
        var (tools, root) = Create(options);
        try
        {
            Assert.Contains("不在白名单", await tools.RunCommand("whoami"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task RunCommand_Whitelisted_Executes()
    {
        var options = new AgentOptions { WorkToolCommands = "whoami", WorkToolTimeoutMs = 10_000 };
        var (tools, root) = Create(options);
        try
        {
            var result = await tools.RunCommand("whoami");
            Assert.Contains("退出码：0", result);
        }
        finally { Cleanup(root); }
    }
}
