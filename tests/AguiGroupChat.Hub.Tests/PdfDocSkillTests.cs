using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf.IO;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 内置「PDF 文档生成」技能（pdf_doc）。
///
/// 与内置 pptx / docx 技能同一条执行链路：平台自带运行时编译（Roslyn）后执行。
/// 本测试直接跑<b>待入库的技能源码本体</b>（tools/pdf-skills/pdf_doc.cs），断言：
/// 1. 产物是合法 PDF（%PDF- 头 + PdfReader 能回读 + 页数正确）且带 produce_file 标记；
/// 2. 中文真的渲染出来了 —— PDF 内含 /Type0 + CIDFontType + /FontFile（CID 字体已嵌入）；
/// 3. 文件体积合理（&lt; 400KB / 单页）—— 防止 CFF 字体未子集化导致 13MB 的回归；
/// 4. 字体发现失败 / 非法 JSON / 空 blocks / 不可写路径 → 可读中文错误，不崩。
///
/// 多个用例在<b>同一进程</b>里反复执行该技能，这也是对「字体解析器只装一次 + 跨 ALC 复用」
/// 设计的端到端验证（若实现有误，第二次执行会抛
/// "You must not change font resolver after is was once used."）。
/// </summary>
[Collection(EnvVarCollection.Name)] // 改 AGUI_PDF_OUT / AGUI_PDF_FONT（进程级）→ 与其它改环境变量的用例串行
public sealed class PdfDocSkillTests
{
    // ---- 技能源码与宿主（与 PptxDeckSkillTests 同构） ----

    private static string SkillSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AguiGroupChat.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "tools", "pdf-skills", "pdf_doc.cs");
        Assert.True(File.Exists(path), "找不到技能源文件：" + path);
        return File.ReadAllText(path);
    }

    private static DotnetSkillHost NewHost()
        => new(NullLogger<DotnetSkillHost>.Instance,
            // NuGet 缓存根必须<b>全进程共用一个</b>：若按用例新建（GUID 目录），每个用例都会把
            // PDFsharp / ImageSharp 等依赖全量重下一次 —— 实测跑久了会吃掉上百 GB 磁盘。
            Path.Combine(Path.GetTempPath(), "agui-pdf-skill-nuget-cache"));

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "agui-pdf-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>4×4 PNG（内嵌 base64，避免测试依赖仓库里的二进制资产）。</summary>
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAANElEQVR42hXIIQEAMAhFQZKQhBDoaUIsyc+EphB7E2fO/PYeCAPzIiBM/UgCwuSPICBM9D59ZymRzPwZugAAAABJRU5ErkJggg==";

    private static string WriteTinyPng(string dir)
    {
        var p = Path.Combine(dir, "figure.png");
        File.WriteAllBytes(p, Convert.FromBase64String(TinyPngBase64));
        return p;
    }

    // ---- 中文字体存在性（CI 机器可能没有 → 跳过而不是失败） ----

    private static readonly string[] FontCandidates =
    {
        "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
        "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc",
        "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc",
        @"C:\Windows\Fonts\simhei.ttf",
        @"C:\Windows\Fonts\Deng.ttf",
        @"C:\Windows\Fonts\simsun.ttc",
        @"/System/Library/Fonts/PingFang.ttc",
    };

    private sealed class TestFontCatalog
    {
        public static bool HasChineseFont()
        {
            var env = Environment.GetEnvironmentVariable("AGUI_PDF_FONT");
            if (!string.IsNullOrWhiteSpace(env)) return File.Exists(env);
            return FontCandidates.Any(File.Exists);
        }
    }

    /// <summary>
    /// 条件跳过（xUnit v2 惯用法：在 discovery 阶段设置 Skip）。
    /// CI 机器可能没有中文字体 → 相关用例显示为“已跳过”而不是失败；本地有字体时真跑。
    /// （注：xUnit 2.9.3 的 <c>SkipException</c> 不会被 runner 当作 skip，别用。）
    /// </summary>
    private sealed class ChineseFontFactAttribute : FactAttribute
    {
        public ChineseFontFactAttribute()
        {
            if (!TestFontCatalog.HasChineseFont())
                Skip = "本机/容器未找到可用的中文字体（simhei.ttf / DroidSansFallbackFull.ttf / wqy-microhei.ttc 等），"
                     + "跳过依赖真实字体渲染的断言。";
        }
    }

    /// <summary>同 <see cref="ChineseFontFactAttribute"/>，用于 [Theory]。</summary>
    private sealed class ChineseFontTheoryAttribute : TheoryAttribute
    {
        public ChineseFontTheoryAttribute()
        {
            if (!TestFontCatalog.HasChineseFont())
                Skip = "本机/容器未找到可用的中文字体，跳过依赖真实字体渲染的断言。";
        }
    }

    // ---- 断言辅助 ----

    private static Encoding Latin => Encoding.Latin1;

    private static string RawText(string path) => Latin.GetString(File.ReadAllBytes(path));

    /// <summary>把页面内容流解压后拼成文本（颜色等数值只存在内容流里，且内容流是 FlateDecode 压缩的）。</summary>
    private static string PageContentText(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var sb = new StringBuilder();
        for (var p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            var count = page.Contents.Elements.Count;
            for (var i = 0; i < count; i++)
            {
                var stream = page.Contents.Elements.GetDictionary(i)?.Stream?.UnfilteredValue;
                if (stream is not null) sb.Append(Latin.GetString(stream));
            }
        }
        return sb.ToString();
    }

    private static string RunRaw(string json, string outDir)
    {
        Environment.SetEnvironmentVariable("AGUI_PDF_OUT", outDir);
        var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
        // 调试用：最近一次原始返回（编译失败 / 运行错误都在这里看）
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pdf-skill-last-result.json"), result); } catch { }
        return result;
    }

    private static (JsonDocument Doc, string Path) RunOk(string json, string outDir)
    {
        var result = RunRaw(json, outDir);
        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
        var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
        return (doc, path);
    }

    private static void UsingOut(string outDir, Action body)
    {
        Environment.SetEnvironmentVariable("AGUI_PDF_OUT", outDir);
        try { body(); }
        finally { Environment.SetEnvironmentVariable("AGUI_PDF_OUT", null); }
    }

    /// <summary>一份覆盖主要内容块的完整文档。</summary>
    private static string FullDocumentJson(string imagePath) => JsonSerializer.Serialize(new
    {
        title = "知聚平台能力白皮书",
        subtitle = "多数字员工协作平台 · 技术版",
        author = "产品与研发中心",
        date = "2026-09",
        docType = "report",
        toc = true,
        blocks = new object[]
        {
            new { type = "h1", text = "一、平台概述" },
            new { type = "p", text = "知聚把单聊式 AI 升级为**多角色协作空间**，聊天记录向量化为长期记忆，越用越懂团队。" },
            new { type = "h2", text = "1.1 核心能力" },
            new { type = "list", ordered = false, items = new[] { "协作：多角色同场会商", "记忆：RAG 长期记忆并可治理", "交付：直接产出可下载文件" } },
            new { type = "list", ordered = true, items = new[] { "第一步：导入组织架构", "第二步：编队数字员工", "第三步：发起群聊任务" } },
            new { type = "callout", kind = "info", title = "重点", text = "所有产物都落在可下载目录，前端可直接点击下载。" },
            new { type = "quote", text = "让组织知道什么、记得什么，比单个模型有多强更重要。", cite = "产品原则" },
            new { type = "h3", text = "1.2 选型对照" },
            new { type = "table", headers = new[] { "维度", "私有化", "SaaS" }, rows = new object[] { new[] { "数据位置", "内网", "云端" }, new[] { "运维成本", "较高", "低" } }, caption = "表 1 部署形态对照" },
            new { type = "chart", chartType = "bar", title = "活跃团队增长", categories = new[] { "Q1", "Q2", "Q3", "Q4" }, series = new object[] { new { name = "活跃团队", values = new[] { 120.0, 260.0, 430.0, 610.0 } } }, yLabel = "个" },
            new { type = "chart", chartType = "line", title = "留存趋势", categories = new[] { "1月", "2月", "3月" }, series = new object[] { new { name = "留存", values = new[] { 0.72, 0.81, 0.88 } } } },
            new { type = "chart", chartType = "pie", title = "能力分布", categories = new[] { "协作", "记忆", "交付" }, series = new object[] { new { name = "占比", values = new[] { 40.0, 35.0, 25.0 } } } },
            new { type = "chart", chartType = "doughnut", title = "成本结构", categories = new[] { "算力", "存储" }, series = new object[] { new { name = "占比", values = new[] { 70.0, 30.0 } } } },
            new { type = "image", path = imagePath, caption = "图 1 内嵌 PNG 图片", widthMm = 80.0 },
            new { type = "code", language = "csharp", code = "var skill = \"pdf_doc\";\nConsole.WriteLine(skill);" },
            new { type = "p", text = "正文里的 *斜体*、**粗体** 与 `等宽` 标记都应正常渲染。", },
            new { type = "pagebreak" },
            new { type = "h1", text = "二、附录" },
            new { type = "divider" },
            new { type = "caption", text = "以上数据为示例，不代表真实业务指标。" },
        },
    });

    // ===================== 1) 完整文档 =====================

    [ChineseFontFact]
    public void RealSkill_ProducesValidPdfWithProduceFileMarker()
    {
        var outDir = TempDir();
        UsingOut(outDir, () =>
        {
            var imagePath = WriteTinyPng(outDir);
            var (doc, path) = RunOk(FullDocumentJson(imagePath), outDir);
            try
            {
                Assert.True(File.Exists(path), "产物不存在：" + path);
                Assert.EndsWith(".pdf", path);
                Assert.Equal("知聚平台能力白皮书.pdf", doc.RootElement.GetProperty("produce_file").GetProperty("name").GetString());
                Assert.True(doc.RootElement.GetProperty("produce_file").GetProperty("bytes").GetInt64() > 0);
                Assert.Equal("report", doc.RootElement.GetProperty("docType").GetString());
                Assert.Equal(21, doc.RootElement.GetProperty("blocks").GetInt32());

                // 合法 PDF：头 + 能回读 + 页数一致
                var head = Latin.GetString(File.ReadAllBytes(path).Take(5).ToArray());
                Assert.Equal("%PDF-", head);
                using var reopen = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                Assert.Equal(doc.RootElement.GetProperty("pages").GetInt32(), reopen.PageCount);
                Assert.True(reopen.PageCount >= 4, "完整文档应至少 4 页（封面 + 目录 + 正文 + 分页），实际 " + reopen.PageCount);

                // 中文嵌入为 CID 字体（换机器不会丢字）
                var raw = RawText(path);
                Assert.Contains("/Type0", raw);
                Assert.Contains("CIDFontType", raw);
                Assert.Contains("/FontFile", raw);
                Assert.Contains("/ToUnicode", raw);

                // 体积合理（glyf 字体子集化；CFF 未子集化会到 10MB+）
                Assert.True(new FileInfo(path).Length < 2_000_000,
                    "完整文档体积异常偏大：" + new FileInfo(path).Length + " 字节（疑似字体未子集化）");
            }
            finally { doc.Dispose(); }
        });
    }

    // ===================== 2) 中文渲染 + 体积闸 =====================

    [ChineseFontFact]
    public void ChineseText_IsRenderedAsEmbeddedCidFont_AndStaysSmall()
    {
        var outDir = TempDir();
        UsingOut(outDir, () =>
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "中文渲染校验",
                docType = "minimal",
                cover = false,
                blocks = new object[]
                {
                    new { type = "h1", text = "中文标题：知聚平台" },
                    new { type = "p", text = "这是一段中文正文，用来验证字形真的被嵌入 PDF，而不是空白或方框。" },
                },
            });
            var (doc, path) = RunOk(json, outDir);
            try
            {
                Assert.Equal(1, doc.RootElement.GetProperty("pages").GetInt32());
                using var reopen = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                Assert.Equal(1, reopen.PageCount);

                var bytes = File.ReadAllBytes(path);
                // 单页中文文档必须远小于 400KB（这正是 CFF 字体不做子集化时会变成 13MB 的回归闸）
                Assert.True(bytes.Length < 400 * 1024, "单页中文 PDF 体积过大：" + bytes.Length + " 字节");
                var raw = Latin.GetString(bytes);
                Assert.Contains("/Type0", raw);
                Assert.Contains("CIDFontType", raw);
                Assert.Contains("/FontFile", raw);
            }
            finally { doc.Dispose(); }
        });
    }

    // ===================== 3) 文档类型（8 种封面版式都要能真跑通） =====================

    [ChineseFontTheory]
    [InlineData("report")]
    [InlineData("proposal")]
    [InlineData("resume")]
    [InlineData("academic")]
    [InlineData("minimal")]
    [InlineData("editorial")]
    [InlineData("magazine")]
    [InlineData("terminal")]
    public void EveryDocType_ProducesValidPdf(string docType)
    {
        var outDir = TempDir();
        UsingOut(outDir, () =>
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "版式测试 " + docType,
                subtitle = "副标题",
                author = "作者",
                date = "2026-09",
                docType,
                blocks = new object[]
                {
                    new { type = "h1", text = "章节标题" },
                    new { type = "p", text = "正文段落，验证版心与留白。" },
                    new { type = "callout", kind = "warn", text = "注意：这是一条警示。" },
                    new { type = "table", headers = new[] { "指标", "值" }, rows = new object[] { new[] { "页数", "2" } } },
                },
            });
            var (doc, path) = RunOk(json, outDir);
            try
            {
                Assert.Equal(docType, doc.RootElement.GetProperty("docType").GetString());
                Assert.True(doc.RootElement.GetProperty("pages").GetInt32() >= 2, "应含封面 + 正文");
                Assert.Equal("%PDF-", Latin.GetString(File.ReadAllBytes(path).Take(5).ToArray()));
                using var reopen = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                Assert.True(reopen.PageCount >= 2);
            }
            finally { doc.Dispose(); }
        });
    }

    // ===================== 4) REFORMAT：Markdown 重排 =====================

    [ChineseFontFact]
    public void MarkdownInput_IsReformattedIntoPdf()
    {
        var outDir = TempDir();
        UsingOut(outDir, () =>
        {
            var markdown = string.Join("\n", new[]
            {
                "# 季度复盘",
                "",
                "本季度重点是把**单聊助手**升级为多角色协作空间。",
                "",
                "## 关键结论",
                "",
                "- 协作：多角色同场会商",
                "- 记忆：RAG 长期记忆",
                "1. 导入组织架构",
                "2. 编队数字员工",
                "",
                "> 让组织知道什么、记得什么，比单个模型有多强更重要。",
                "",
                "| 维度 | 说明 |",
                "| --- | --- |",
                "| 成本 | 低 |",
                "| 效果 | 好 |",
                "",
                "```csharp",
                "var ok = true;",
                "```",
            });
            var json = JsonSerializer.Serialize(new { title = "季度复盘", docType = "minimal", markdown });
            var (doc, path) = RunOk(json, outDir);
            try
            {
                // h1 + 段 + h2 + ul + ol + quote + table + code = 8 块
                Assert.Equal(8, doc.RootElement.GetProperty("blocks").GetInt32());
                Assert.Equal("%PDF-", Latin.GetString(File.ReadAllBytes(path).Take(5).ToArray()));
                using var reopen = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                Assert.True(reopen.PageCount >= 1);
            }
            finally { doc.Dispose(); }
        });
    }

    // ===================== 5) 同名去重不覆盖 =====================

    [ChineseFontFact]
    public void SameTitle_DoesNotOverwriteExistingFile()
    {
        var outDir = TempDir();
        UsingOut(outDir, () =>
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "重复标题",
                docType = "minimal",
                cover = false,
                blocks = new object[] { new { type = "p", text = "第一次。" } },
            });
            var (d1, p1) = RunOk(json, outDir);
            d1.Dispose();
            var (d2, p2) = RunOk(json, outDir);
            d2.Dispose();
            Assert.NotEqual(p1, p2);
            Assert.EndsWith("重复标题.pdf", p1);
            Assert.EndsWith("重复标题-2.pdf", p2);
            Assert.True(File.Exists(p1));
            Assert.True(File.Exists(p2));
        });
    }

    // ===================== 6) 字体发现失败 → 可读中文错误 =====================

    [Fact]
    public void MissingOrBrokenFont_ReturnsReadableChineseError()
    {
        var outDir = TempDir();
        var bogus = Path.Combine(outDir, "not-a-font.ttf");
        File.WriteAllBytes(bogus, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Environment.SetEnvironmentVariable("AGUI_PDF_OUT", outDir);
        Environment.SetEnvironmentVariable("AGUI_PDF_FONT", bogus);
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "字体失败",
                blocks = new object[] { new { type = "p", text = "正文" } },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var msg = doc.RootElement.GetProperty("message").GetString()!;
            Assert.Contains("字体", msg);
            Assert.Contains(bogus, msg);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGUI_PDF_FONT", null);
            Environment.SetEnvironmentVariable("AGUI_PDF_OUT", null);
        }
    }

    [Fact]
    public void MissingFontPath_ReturnsReadableChineseError()
    {
        var outDir = TempDir();
        var missing = Path.Combine(outDir, "no-such-font.ttf");
        Environment.SetEnvironmentVariable("AGUI_PDF_OUT", outDir);
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "字体缺失",
                fontPath = missing,
                blocks = new object[] { new { type = "p", text = "正文" } },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            Assert.Contains("字体", doc.RootElement.GetProperty("message").GetString());
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PDF_OUT", null); }
    }

    // ===================== 7) 其它错误路径 =====================

    [Fact]
    public void InvalidJson_FailsWithReadableMessage()
    {
        var outDir = TempDir();
        UsingOut(outDir, () =>
        {
            var result = NewHost().Run(SkillSource(), "{\"title\": \"x\", }", CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            Assert.Contains("JSON", doc.RootElement.GetProperty("message").GetString());
        });
    }

    [Fact]
    public void EmptyBlocks_FailsWithReadableMessage()
    {
        var outDir = TempDir();
        UsingOut(outDir, () =>
        {
            var json = JsonSerializer.Serialize(new { title = "空文档", blocks = Array.Empty<object>() });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            Assert.Contains("blocks", doc.RootElement.GetProperty("message").GetString());
        });
    }

    [ChineseFontFact]
    public void UnknownBlockType_FailsWithReadableMessage()
    {
        var outDir = TempDir();
        UsingOut(outDir, () =>
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "未知块",
                blocks = new object[] { new { type = "carousel", text = "?" } },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            Assert.Contains("carousel", doc.RootElement.GetProperty("message").GetString());
        });
    }

    [ChineseFontFact]
    public void UnwritableOutputPath_FailsWithReadableMessage()
    {
        var outDir = TempDir();
        UsingOut(outDir, () =>
        {
            // 把「文件」当成目录用 → Directory.CreateDirectory 必然失败
            var blocker = Path.Combine(outDir, "blocker");
            File.WriteAllText(blocker, "x");
            var json = JsonSerializer.Serialize(new
            {
                title = "不可写",
                outputPath = Path.Combine(blocker, "out.pdf"),
                blocks = new object[] { new { type = "p", text = "正文" } },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            Assert.Contains("无法", doc.RootElement.GetProperty("message").GetString());
        });
    }

    [ChineseFontFact]
    public void MissingImage_FailsWithReadableMessage()
    {
        var outDir = TempDir();
        UsingOut(outDir, () =>
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "缺图",
                blocks = new object[] { new { type = "image", path = Path.Combine(outDir, "nope.png") } },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            Assert.Contains("图片", doc.RootElement.GetProperty("message").GetString());
        });
    }

    // ===================== 8) 强调色 / 覆盖参数生效 =====================

    [ChineseFontFact]
    public void AccentOverrideAndTocOptions_AreHonoured()
    {
        var outDir = TempDir();
        UsingOut(outDir, () =>
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "覆盖参数",
                docType = "minimal",
                accent = "#B03A2E",
                toc = new { title = "目录", items = new[] { "章节一", "章节二" } },
                blocks = new object[]
                {
                    new { type = "h1", text = "章节一" },
                    new { type = "p", text = "正文一" },
                    new { type = "h1", text = "章节二" },
                    new { type = "p", text = "正文二" },
                },
            });
            var (doc, path) = RunOk(json, outDir);
            try
            {
                // 强调色确实写进了内容流：B03A2E → 176/58/46 → PDFsharp 按 3 位小数写 fillcolor
                var content = PageContentText(path);
                Assert.Contains("0.69 0.227 0.18 rg", content);
                using var reopen = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                Assert.True(reopen.PageCount >= 2);
            }
            finally { doc.Dispose(); }
        });
    }

    [Fact]
    public void SkillSource_UsesTpaProvidedPdfSharp_WithoutNuGetDirective()
    {
        // PDFsharp 是平台依赖（在 TPA 里）→ 技能正文不该写 #r 引用指令（会白跑一次联网还原）
        var src = SkillSource();
        foreach (var line in src.Split('\n'))
        {
            var trimmed = line.TrimStart();
            Assert.False(trimmed.StartsWith("#r", StringComparison.Ordinal),
                "技能正文不应有 #r 引用指令（PDFsharp 已在 TPA）：" + line);
        }
        Assert.Contains("using PdfSharp.Pdf;", src);
        Assert.Contains("using PdfSharp.Drawing;", src);
        Assert.Contains("using PdfSharp.Fonts;", src);
        Assert.Contains("GlobalFontSettings.FontResolver", src);
        Assert.Contains("public static string Run(string input)", src);
    }
}
