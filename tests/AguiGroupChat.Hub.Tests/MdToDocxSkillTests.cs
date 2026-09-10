using System.IO.Compression;
using System.Text.Json;
using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// Markdown → Word（.docx）的 .NET 技能（md_to_docx）。
///
/// 背景：技能库里曾有一个用 Python 写的同类技能（docx_report_2，shell + python-docx），
/// 而服务端容器没有 python3，一跑就是 command not found / 退出码 127。这里改为纯 .NET 实现：
/// 用平台自带运行时编译执行，不依赖任何外部解释器。
///
/// 本测试直接跑<b>待入库的技能源码本体</b>，走真实 DotnetSkillHost 编译执行链路
/// （与生产同一路径），断言产物是合法 docx 且带 produce_file 标记（前端可下载的前提）。
/// </summary>
[Collection(EnvVarCollection.Name)] // 改 AGUI_DOCX_OUT（进程级）→ 必须串行，避免污染并行用例
public sealed class MdToDocxSkillTests
{
    /// <summary>技能源码：与准备入库的正文一致（从仓库内的源文件读取，避免测试里再抄一份）。</summary>
    private static string SkillSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AguiGroupChat.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "tools", "md-to-docx-skill", "Skill.cs");
        Assert.True(File.Exists(path), "找不到技能源文件：" + path);
        return File.ReadAllText(path);
    }

    private static DotnetSkillHost NewHost()
        => new(NullLogger<DotnetSkillHost>.Instance,
            Path.Combine(Path.GetTempPath(), "agui-md2docx-" + Guid.NewGuid().ToString("N")));

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "agui-md2docx-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void RealSkill_ProducesValidDocxWithProduceFileMarker()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", outDir);
        try
        {
            var md = string.Join("\n",
                "# 九月推广方案",
                "",
                "## 一、目标",
                "本季度聚焦**品牌曝光**与用户增长，覆盖三个渠道。",
                "",
                "- 短视频平台：主打种草内容",
                "- 社群：私域复购运营",
                "- 搜索：长尾词拦截",
                "",
                "## 二、执行节奏",
                "1. 第一周完成素材筹备",
                "2. 第二周开始投放",
                "",
                "> 注：预算以最终审批为准。",
                "",
                "```",
                "预算样例: 10000",
                "```",
                "",
                "---",
                "",
                "收尾段落。");

            var json = JsonSerializer.Serialize(new { markdown = md });
            var host = NewHost();
            var result = host.Run(SkillSource(), json, CancellationToken.None);

            Assert.DoesNotContain("编译失败", result);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);

            // produce_file 标记是「前端可下载」的前提（网关据此入库为 att_xxx）
            var pf = doc.RootElement.GetProperty("produce_file");
            var path = pf.GetProperty("path").GetString()!;
            Assert.True(File.Exists(path), "产物不存在：" + path);
            Assert.EndsWith(".docx", path);
            Assert.Equal("九月推广方案.docx", pf.GetProperty("name").GetString());
            Assert.Equal(new FileInfo(path).Length, pf.GetProperty("bytes").GetInt64());
            Assert.True(pf.GetProperty("bytes").GetInt64() > 0);

            // 是可打开的 docx（zip 容器 + document.xml），且内容确实落进去了
            using var zip = ZipFile.OpenRead(path);
            Assert.Contains(zip.Entries, e => e.FullName == "word/document.xml");
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();
            Assert.Contains("九月推广方案", xml);
            Assert.Contains("短视频平台", xml);
            Assert.Contains("预算以最终审批为准", xml);
            // 行内标记符应被剔除（不应把 ** 原样写进文档）
            Assert.DoesNotContain("**", xml);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", null); }
    }

    [Fact]
    public void TitleOverride_WinsOverFirstHeading()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", outDir);
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                markdown = "# 原稿标题\n\n正文内容。",
                title = "交付标题",
                author = "文案组",
                date = "2026年9月",
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

            Assert.Equal("交付标题.docx", Path.GetFileName(path));
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();
            Assert.Contains("交付标题", xml);
            Assert.Contains("文案组", xml);
            Assert.Contains("2026年9月", xml);
            // 原标题与显式标题不同 → 应作为一级标题保留（不丢内容）
            Assert.Contains("原稿标题", xml);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", null); }
    }

    [Fact]
    public void MissingMarkdown_ReturnsClearError_NotException()
    {
        var result = NewHost().Run(SkillSource(), "{}", CancellationToken.None);
        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("markdown", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void InvalidJson_ReturnsClearError()
    {
        var result = NewHost().Run(SkillSource(), "not json at all", CancellationToken.None);
        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("JSON", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void SameTitleTwice_DoesNotOverwrite()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", outDir);
        try
        {
            var json = JsonSerializer.Serialize(new { markdown = "# 重复标题\n\n内容。" });
            var host = NewHost();
            var first = JsonDocument.Parse(host.Run(SkillSource(), json, CancellationToken.None))
                .RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
            var second = JsonDocument.Parse(host.Run(SkillSource(), json, CancellationToken.None))
                .RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

            Assert.NotEqual(first, second);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
        }
        finally { Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", null); }
    }

    [Fact]
    public void MarkdownMarkers_NeverLeakIntoDocument()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", outDir);
        try
        {
            var md = "# 标题\n\n## 小节\n\n- 要点一\n- 要点二\n\n1. 第一\n2. 第二\n\n> 引用文字\n\n普通段落。";
            var json = JsonSerializer.Serialize(new { markdown = md });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();

            // 标记字符不得残留为正文：内容在，但 "- " / "1. " / "> " / "## " 前缀不得跟着进文档
            Assert.Contains("要点一", xml);
            Assert.Contains("引用文字", xml);
            Assert.Contains("普通段落", xml);
            Assert.DoesNotContain("- 要点一", xml);
            Assert.DoesNotContain("1. 第一", xml);
            Assert.DoesNotContain("> 引用文字", xml);
            Assert.DoesNotContain("## 小节", xml);
            // 无序列表以项目符号呈现
            Assert.Contains("• 要点一", xml);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_DOCX_OUT", null); }
    }
}
