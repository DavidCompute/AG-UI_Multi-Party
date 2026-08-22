using AguiGroupChat.Agents.Tools;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>工作型智能体的文件 / 命令工具（工作区沙箱 + 命令白名单）。</summary>
public sealed class AgentWorkToolsTests
{
    private static string NewWorkDir()
        => Path.Combine(Path.GetTempPath(), $"agui-ws-{Guid.NewGuid():N}");

    private static (AgentWorkSpace Space, AgentWorkTools Tools) Create()
    {
        var root = NewWorkDir();
        var space = new AgentWorkSpace(root);
        space.EnsureRoot();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        return (space, new AgentWorkTools(space, services, NullLoggerFactory.Instance));
    }

    [Fact]
    public void WorkSpace_RejectsPathTraversal()
    {
        var (space, _) = Create();
        Assert.Null(space.ContainsResolve("../escape.txt"));
        Assert.Null(space.ContainsResolve("a/../../etc/passwd"));
        Assert.Null(space.ContainsResolve("C:\\Windows\\system32")); // 绝对路径越界
        // 正常相对路径解析到工作区内
        Assert.NotNull(space.ContainsResolve("sub/file.txt"));
    }

    [Fact]
    public void WriteFile_ThenReadFile_RoundTrips()
    {
        var (space, tools) = Create();
        Assert.Contains("已写入", tools.WriteFile("hello.txt", "你好，世界\n", append: false));
        Assert.Contains("你好", tools.ReadFile("hello.txt"));
        // 追加
        Assert.Contains("已追加", tools.WriteFile("hello.txt", "第二行", append: true));
        Assert.Contains("第二行", tools.ReadFile("hello.txt"));
        space.EnsureRoot();
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public void WriteFile_RejectsPathOutsideWorkspace()
    {
        var (space, tools) = Create();
        Assert.Contains("越界", tools.WriteFile("../../evil.txt", "x", append: false));
        Assert.Contains("越界" , tools.ReadFile("../secret.txt"));
        space.EnsureRoot();
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public void ListDir_ListsFiles()
    {
        var (space, tools) = Create();
        tools.WriteFile("a.txt", "a", append: false);
        tools.WriteFile("b.txt", "b", append: false);
        Assert.Contains("a.txt", tools.ListDir());
        Assert.Contains("b.txt", tools.ListDir());
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public void Shell_WhitelistAllowsLs_RejectsDangerous()
    {
        var (space, tools) = Create();
        tools.WriteFile("sample.txt", "hello", append: false);
        var result = tools.ShellAsync("ls").GetAwaiter().GetResult();
        Assert.Contains("sample.txt", result); // 白名单内 ls
        var blocked = tools.ShellAsync("sudo rm -rf /").GetAwaiter().GetResult();
        Assert.Contains("不在白名单", blocked); // 危险命令拒绝
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public void PublishFile_SavesAsAttachment_RejectsNonWhitelistedType()
    {
        var root = NewWorkDir();
        var space = new AgentWorkSpace(root);
        space.EnsureRoot();
        // 装配一个真实附件存储（临时目录）
        var uploads = Path.Combine(Path.GetTempPath(), $"agui-att-{Guid.NewGuid():N}");
        var attStore = new AttachmentStore(uploads);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton(attStore)
            .BuildServiceProvider();
        var tools = new AgentWorkTools(space, services, NullLoggerFactory.Instance);

        // 写产物 → 发布为附件
        tools.WriteFile("report.md", "# 项目报告\n进度 80%", append: false);
        var published = tools.PublishFile("report.md");
        Assert.StartsWith("PUBLISH_FILE:", published); // JSON 标记（网关据此回灌群附件）
        Assert.Contains("/ag-ui/files", published);    // 附件下载地址
        Assert.Single(attStore.ListAllFiles()); // 附件已进存储

        // 非白名单类型（.sh 脚本）拒绝
        tools.WriteFile("run.sh", "#!/bin/sh\necho hi", append: false);
        Assert.Contains("不在可分享白名单", tools.PublishFile("run.sh"));

        // 越界路径拒绝
        Assert.Contains("越界", tools.PublishFile("../secret.md"));
    }

    [Fact]
    public void CopyFile_And_RenameFile_WorkInsideWorkspace()
    {
        var (space, tools) = Create();
        tools.WriteFile("src.md", "# 副本", append: false);

        // 复制
        Assert.Contains("已复制", tools.CopyFile("src.md", "copy.md"));
        Assert.Contains("# 副本", tools.ReadFile("copy.md"));

        // 重命名/移动
        Assert.Contains("已重命名", tools.RenameFile("copy.md", "moved.md"));
        Assert.False(File.Exists(space.ContainsResolve("copy.md"))); // 原文件已移动
        Assert.True(File.Exists(space.ContainsResolve("moved.md")));

        // 越界拒绝
        Assert.Contains("越界", tools.CopyFile("src.md", "../../evil.md"));
        Assert.Contains("越界", tools.RenameFile("src.md", "../x.md"));

        space.EnsureRoot();
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public async Task Remember_And_ReadNotes_PersistAcrossCalls()
    {
        var (space, tools) = Create();
        Assert.Contains("还没有备忘", tools.ReadNotes()); // 初始无备忘
        Assert.Contains("已记入", tools.Remember("完成模块一：采集网页"));
        var content = tools.ReadNotes();
        Assert.Contains("完成模块一", content);
        Assert.True(File.Exists(space.ContainsResolve("NOTES.md")));
        space.EnsureRoot();
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public async Task FetchUrl_RejectsLoopbackAndNonHttp()
    {
        var (space, tools) = Create();
        // 本机/内网地址（SSRF 防护）
        Assert.Contains("拒绝访问本机", await tools.FetchUrl("http://localhost:8080/x", "x.md"));
        Assert.Contains("拒绝访问本机", await tools.FetchUrl("http://127.0.0.1/", "x.md"));
        Assert.Contains("拒绝访问本机", await tools.FetchUrl("http://192.168.1.1/", "x.md"));
        // 非 http/https
        Assert.Contains("仅支持 http/https", await tools.FetchUrl("file:///etc/passwd", "x.md"));
        // 越界目标
        Assert.Contains("越界", await tools.FetchUrl("http://example.com/a", "../../evil.md"));
        space.EnsureRoot();
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public void ListTree_RecursesFilesWithSize()
    {
        var (space, tools) = Create();
        tools.WriteFile("a.md", "A", append: false);
        tools.WriteFile("sub/b.txt", "BB", append: false);
        var tree = tools.ListTree();
        Assert.Contains("a.md", tree);
        Assert.Contains("sub/b.txt", tree);
        space.EnsureRoot();
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public void ReadBatch_ReadsMultipleFiles_RejectsMissing()
    {
        var (space, tools) = Create();
        tools.WriteFile("r1.md", "内容一", append: false);
        tools.WriteFile("r2.md", "内容二", append: false);
        var out1 = tools.ReadBatch("r1.md,r2.md");
        Assert.Contains("内容一", out1);
        Assert.Contains("内容二", out1);
        // 缺失文件友好提示
        Assert.Contains("越界或不存在", tools.ReadBatch("nope.md"));
        // 越界
        Assert.Contains("越界", tools.ReadBatch("../secret.md"));
        space.EnsureRoot();
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public void BatchRename_MovesByExtension()
    {
        var (space, tools) = Create();
        tools.WriteFile("docs/v1.md", "x", append: false);
        tools.WriteFile("docs/v2.md", "y", append: false);
        var res = tools.BatchRename(".md", "docs", "final");
        Assert.Contains("已迁移 2 个", res);
        Assert.True(File.Exists(space.ContainsResolve("final/v1.md")));
        Assert.True(File.Exists(space.ContainsResolve("final/v2.md")));
        // 越界目标
        Assert.Contains("越界", tools.BatchRename(".md", "docs", "../../out"));
        space.EnsureRoot();
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public void Archive_FileAndDir_CreatesZip()
    {
        var (space, tools) = Create();
        tools.WriteFile("report.md", "# 报告", append: false);
        // 目录打包
        Assert.Contains("已打包", tools.Archive("report.md", "report.zip"));
        Assert.True(File.Exists(space.ContainsResolve("report.zip")));
        // 根目录拒绝
        Assert.Contains("不能归档", tools.Archive("", "root.zip"));
        space.EnsureRoot();
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public void Remove_DeletesFile_ButRefusesRoot()
    {
        var (space, tools) = Create();
        tools.WriteFile("delme.txt", "x", append: false);
        Assert.Contains("已删除文件", tools.Remove("delme.txt"));
        Assert.False(File.Exists(space.ContainsResolve("delme.txt")));
        // 根目录保护
        Assert.Contains("不能删除", tools.Remove("."));
        // 越界
        Assert.Contains("越界", tools.Remove("../../etc/passwd"));
        space.EnsureRoot();
        Directory.Delete(space.Root, true);
    }

    [Fact]
    public void PlanWrite_Read_MarkDone_RoundTrips()
    {
        var (space, tools) = Create();
        Assert.Contains("还没有计划", tools.PlanRead());
        Assert.Contains("已规划 2 步", tools.PlanWrite("整理报告", "\n采集网页\n生成报告\n".Trim()));
        var plan = tools.PlanRead();
        Assert.Contains("任务计划：整理报告", plan);
        Assert.Contains("1. 采集网页", plan);
        Assert.Contains("2. 生成报告", plan);
        // 标记第 1 步完成
        Assert.Contains("第 1 步已完成（1/2）", tools.PlanMarkDone(1));
        Assert.Contains("- [x] 1.", tools.PlanRead());
        // 找不到步骤
        Assert.Contains("找不到第 9 步", tools.PlanMarkDone(9));
        space.EnsureRoot();
        Directory.Delete(space.Root, true);
    }

    /// <summary>AgentCatalog.ReadPlan：解析工作区 PLAN.md 为结构化步骤（标题 / 序号 / 完成状态），供消息可视化。</summary>
    [Fact]
    public void Catalog_ReadPlan_ParsesStepListWithTitleAndDone()
    {
        var root = NewWorkDir();
        var options = new AguiGroupChat.Agents.AgentOptions
        {
            Provider = "mock",
            WorkToolsEnabled = true,
            WorkSpaceRoot = root,
            Agents =
            {
                new AguiGroupChat.Agents.AgentDefinition
                {
                    AgentId = "agent_plan", Nickname = "计划助", Description = "", Instructions = "你是计划助手",
                    TriggerMode = AguiGroupChat.Hub.Models.AgentTriggerMode.Mentioned, EnableWorkTools = true,
                },
            },
        };
        var svc = new ServiceCollection().BuildServiceProvider();
        var catalog = new AguiGroupChat.Agents.AgentCatalog(options, NullLoggerFactory.Instance, svc);
        var def = catalog.GetDefinition("agent_plan")!;

        // 用工作工具写入 PLAN.md 并打勾
        var tools = new AgentWorkTools(new AgentWorkSpace(Path.Combine(root, "agent_plan")), svc, NullLoggerFactory.Instance);
        tools.PlanWrite("整理报告", "采集网页\n生成报告");
        tools.PlanMarkDone(1);

        var (title, steps) = catalog.ReadPlan("agent_plan");
        Assert.Equal("任务计划：整理报告", title); // PlanWrite 写入时带「任务计划：」前缀
        Assert.Equal(2, steps.Count);
        Assert.Equal("采集网页", steps[0].Text);
        Assert.True(steps[0].Done);
        Assert.Equal("生成报告", steps[1].Text);
        Assert.False(steps[1].Done);

        // 非工作型 / 无 PLAN.md → 空
        Assert.Empty(catalog.ReadPlan("agent_nonexistent").Steps);

        try { Directory.Delete(root, true); } catch { }
    }
}
