using System.IO.Compression;
using System.Text.Json;
using AguiGroupChat.Agents.Tools;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 内置「演示文稿（.pptx）生成」技能（pptx_deck）。
///
/// 与内置 docx 技能同一条执行链路：平台自带运行时编译（Roslyn）后执行，不依赖 Node /
/// Python / PptxGenJS。本测试直接跑<b>待入库的技能源码本体</b>（tools/pptx-skills/pptx_deck.cs），
/// 断言产物是合法 pptx（zip 容器 + presentation.xml / slideN.xml）且带 produce_file 标记。
/// </summary>
[Collection(EnvVarCollection.Name)] // 改 AGUI_PPTX_OUT（进程级）→ 与其它改环境变量的用例串行
public sealed class PptxDeckSkillTests
{
    private static string SkillSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AguiGroupChat.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "tools", "pptx-skills", "pptx_deck.cs");
        Assert.True(File.Exists(path), "找不到技能源文件：" + path);
        return File.ReadAllText(path);
    }

    private static DotnetSkillHost NewHost()
        => new(NullLogger<DotnetSkillHost>.Instance,
            Path.Combine(Path.GetTempPath(), "agui-pptx-" + Guid.NewGuid().ToString("N")));

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "agui-pptx-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>一份覆盖主要页型的完整演示稿。</summary>
    private static string FullDeckJson() => JsonSerializer.Serialize(new
    {
        title = "知聚平台产品介绍",
        subtitle = "多数字员工协作平台 · 对外宣讲版",
        author = "产品与市场部",
        date = "2026-09",
        theme = "tech",
        slides = new object[]
        {
            new { type = "cover", title = "知聚平台产品介绍", subtitle = "让团队与数字员工在同一知聚里协作", author = "产品与市场部", date = "2026-09" },
            new { type = "toc", title = "目录", items = new[] { "产品定位", "核心能力", "应用场景", "落地节奏" } },
            new { type = "section", title = "一、产品定位", subtitle = "我们在解决什么问题" },
            new { type = "content", title = "产品定位", bullets = new[] { "协作方式：把单聊式 AI 升级为多角色协作空间", "记忆能力：聊天记录向量化为长期记忆，越用越懂团队", "交付能力：数字员工可直接产出可下载的文档" }, note = "开场用一句话讲清定位" },
            new { type = "twoCol", title = "能力对比", left = new { heading = "传统单聊助手", bullets = new[] { "单人单会话", "无组织概念", "不产出文件" } }, right = new { heading = "知聚", bullets = new[] { "多人多角色协作", "可按职能编队", "直接交付 Word / Excel / PPT" } } },
            new { type = "kpi", title = "关键指标", items = new object[] { new { value = "12", label = "内置岗位模板" }, new { value = "98.9%", label = "可用性" }, new { value = "< 2s", label = "首字延迟" } } },
            new { type = "chart", title = "crescimento 趋势", chartType = "bar", categories = new[] { "Q1", "Q2", "Q3", "Q4" }, series = new object[] { new { name = "活跃团队", values = new[] { 120.0, 260.0, 430.0, 610.0 } } }, yLabel = "个" },
            new { type = "table", title = "选型对照", headers = new[] { "维度", "私有化", "SaaS" }, rows = new object[] { new[] { "数据位置", "内网", "云端" }, new[] { "运维成本", "较高", "低" } } },
            new { type = "quote", text = "让组织知道什么、记得什么，比单个模型有多强更重要。", cite = "产品原则" },
            new { type = "summary", title = "小结", bullets = new[] { "定位：多数字员工协作平台", "能力：协作 / 记忆 / 交付 / 治理", "下一步：申请试用并导入组织架构" } },
            new { type = "end", title = "谢谢", subtitle = "欢迎提问" },
        },
    });

    [Fact]
    public void RealSkill_ProducesValidPptxWithProduceFileMarker()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var host = NewHost();
            var result = host.Run(SkillSource(), FullDeckJson(), CancellationToken.None);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "pptx-skill-last-result.json"), result);

            Assert.DoesNotContain("编译失败", result);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);

            // produce_file 是「前端可下载」的前提（网关据此入库为 att_xxx）
            var pf = doc.RootElement.GetProperty("produce_file");
            var path = pf.GetProperty("path").GetString()!;
            Assert.True(File.Exists(path), "产物不存在：" + path);
            Assert.EndsWith(".pptx", path);
            Assert.Equal("知聚平台产品介绍.pptx", pf.GetProperty("name").GetString());
            Assert.True(pf.GetProperty("bytes").GetInt64() > 0);
            Assert.Equal(11, doc.RootElement.GetProperty("slides").GetInt32());

            // 是结构完整的 pptx：zip 容器 + presentation.xml + 母版 + 版式 + 每页 slideN.xml
            using var zip = ZipFile.OpenRead(path);
            Assert.Contains(zip.Entries, e => e.FullName == "[Content_Types].xml");
            Assert.Contains(zip.Entries, e => e.FullName == "ppt/presentation.xml");
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("ppt/slideMasters/", StringComparison.Ordinal));
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("ppt/slideLayouts/", StringComparison.Ordinal));
            var slideXmls = zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                                                && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToList();
            Assert.Equal(11, slideXmls.Count);

            // 内容确实写进了页面（不是空壳）
            var all = string.Join("\n", slideXmls.Select(e =>
            {
                using var r = new StreamReader(e.Open());
                return r.ReadToEnd();
            }));
            Assert.Contains("知聚平台产品介绍", all);
            Assert.Contains("记忆能力", all);
            Assert.Contains("谢谢", all);
            // 每页都应带页码徽标（封面除外）
            Assert.Contains("01", all);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    [Fact]
    public void ThemeAndFontOverrides_AreApplied()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "主题覆盖测试",
                theme = "dark",
                themeColors = new { primary = "112233", accent = "ABCDEF" },
                fontTitle = "Arial",
                slides = new object[]
                {
                    new { type = "cover", title = "主题覆盖测试" },
                    new { type = "content", title = "要点", bullets = new[] { "甲", "乙" } },
                },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

            using var zip = ZipFile.OpenRead(path);
            var slide1 = zip.Entries.First(e => e.FullName == "ppt/slides/slide1.xml");
            using var r = new StreamReader(slide1.Open());
            var xml = r.ReadToEnd();
            Assert.Contains("112233", xml);        // themeColors.primary 生效
            Assert.Contains("Arial", xml);         // fontTitle 生效
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    [Fact]
    public void EmptySlides_FailsWithReadableMessage()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var json = JsonSerializer.Serialize(new { title = "空", slides = Array.Empty<object>() });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("slides", doc.RootElement.GetProperty("message").GetString());
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>
    /// 最关键的一道：用 OpenXML 官方校验器过一遍 schema。
    /// 结构不合法的话 PowerPoint 会提示“文件已损坏 / 需要修复”，那等于技能没交付成功。
    /// 只断言<b>错误</b>为空；提示类（Information）不影响打开，不当作失败。
    /// </summary>
    [Fact]
    public void ProducedPptx_PassesOpenXmlSchemaValidation()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var result = NewHost().Run(SkillSource(), FullDeckJson(), CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

            using var pres = PresentationDocument.Open(path, false);
            var errors = new OpenXmlValidator().Validate(pres)
                .Where(e => e.ErrorType == ValidationErrorType.Schema)
                .Select(e => $"{e.Description} @ {e.Path?.XPath}")
                .Take(20)
                .ToList();
            Assert.True(errors.Count == 0, "OpenXML 校验未通过：\n" + string.Join("\n", errors));
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    [Fact]
    public void SpeakerNotes_AreWrittenIntoNotesSlide()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "备注测试",
                slides = new object[]
                {
                    new { type = "content", title = "要点", bullets = new[] { "甲" }, notes = "这里是演讲者备注" },
                },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

            using var zip = ZipFile.OpenRead(path);
            var notes = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("ppt/notesSlides/", StringComparison.Ordinal));
            Assert.NotNull(notes);
            using var r = new StreamReader(notes!.Open());
            Assert.Contains("这里是演讲者备注", r.ReadToEnd());
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }
}
