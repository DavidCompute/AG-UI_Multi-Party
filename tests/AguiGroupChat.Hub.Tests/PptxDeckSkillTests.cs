using System.IO.Compression;
using System.Net;
using System.Text.Json;
using AguiGroupChat.Agents.BuiltinSkills;
using AguiGroupChat.Agents.Tools;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.Fonts;
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
            // NuGet 缓存根必须<b>全进程共用一个</b>：若按用例新建（GUID 目录），每个用例都会把
            // DocumentFormat.OpenXml / ImageSharp 等依赖全量重下一次 —— 实测跑久了会吃掉上百 GB 磁盘。
            Path.Combine(Path.GetTempPath(), "agui-pptx-skill-nuget-cache"));

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

    /// <summary>
    /// OPC 包的<b>跨部件必备关系</b>必须齐备——这是 PowerPoint 判「需要修复」的典型来源，
    /// 而 OpenXmlValidator 只校验单部件 schema，<b>完全盖不到</b>（实测踩到：
    /// 只建 notesSlide 不建 notesMaster、母版不挂主题时，校验器 0 错误，PowerPoint 仍要求修复）。
    /// </summary>
    [Fact]
    public void Package_HasAllRequiredCrossPartRelationships()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "结构完整性",
                slides = new object[]
                {
                    new { type = "cover", title = "封面", notes = "备注一" },
                    new { type = "content", title = "要点", bullets = new[] { "甲" }, notes = "备注二" },
                    new { type = "end", title = "谢谢" },
                },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var parsed = JsonDocument.Parse(result);
            Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean(), result);
            var path = parsed.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

            using var pres = PresentationDocument.Open(path, false);
            var presPart = pres.PresentationPart!;

            // 1) 幻灯片母版必须挂主题（主题提供颜色与字体，无主题的 sldMaster 不合法）
            var master = presPart.SlideMasterParts.First();
            Assert.NotNull(master.ThemePart);

            // 2) 版式必须回指其母版
            var layout = master.SlideLayoutParts.First();
            Assert.NotNull(layout.SlideMasterPart);

            // 3) 有备注页 → 必须有备注母版，且由 presentation 与每个备注页分别关联
            var slides = presPart.SlideParts.ToList();
            var notesSlides = slides.Select(s => s.NotesSlidePart).Where(n => n is not null).ToList();
            Assert.NotEmpty(notesSlides);
            Assert.NotNull(presPart.NotesMasterPart);
            Assert.NotNull(presPart.NotesMasterPart!.ThemePart);
            foreach (var n in notesSlides)
            {
                Assert.NotNull(n!.NotesMasterPart);
                // 备注页回指它所属的幻灯片
                Assert.NotNull(n.SlidePart);
            }

            // 4) presentation.xml 里要有 notesMasterIdLst（仅建部件不够）
            Assert.Single(pres.PresentationPart!.Presentation.NotesMasterIdList!.ChildElements);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>
    /// 图表字体必须<b>真的含中文字形</b>。
    ///
    /// <para>
    /// 真实踩到：图表字体原先只按“族名命中候选名单”就选定，而容器里前几位候选
    /// （Microsoft YaHei / SimHei / SimSun / Arial）都不存在，第一个命中的是
    /// <c>DejaVu Sans</c>（纯拉丁），于是图表的中文标题 / 分类标签 / 系列名全变缺字（乱码），
    /// 英文坐标数字却正常。修复后改为“命中还要验字形覆盖”，本用例钉住这条不变式。
    /// </para>
    ///
    /// 未装了中文字体的环境（精简容器）会没有中文字体可用，此时跳过而不是误报失败。
    /// </summary>
    [Fact]
    public void ChartFont_MustHaveChineseGlyphs()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "图表字体",
                slides = new object[]
                {
                    new
                    {
                        type = "chart", title = "图表字体", chartType = "bar",
                        categories = new[] { "第一季度", "第二季度" },
                        series = new object[] { new { name = "活跃团队", values = new[] { 10.0, 20.0 } } },
                        yLabel = "团队数",
                    },
                },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);

            // 技能会报出实际选中的图表字体
            var font = doc.RootElement.GetProperty("chartFont").GetString();
            var cjk = doc.RootElement.GetProperty("chartFontCjk").GetBoolean();
            Assert.False(string.IsNullOrWhiteSpace(font));

            var anyCjkFontInstalled = SystemFonts.Collection.Families.Any(CanRenderCjk);
            if (!anyCjkFontInstalled)
                return; // 环境里确实没有中文字体：不是本用例要考的问题
            Assert.True(cjk,
                $"环境里有含中文字形的字体，但图表选了「{font}」（无中文字形）——图表中文会缺字。");
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>
    /// 图表技能必须把 <c>SixLabors.Fonts</c> 钉在 1.0.1+。
    ///
    /// <para>
    /// 真实踩到：图表文字里的横线被画成<b>竖线</b>（破折号“—”变竖向），`（）「」【】《》`
    /// 被旋转 90°。根因不在字体——同一份字体用 FreeType/PIL 渲染是横排正确的 ——
    /// 而是 <b>SixLabors.Fonts 1.0.0 的 shaping 错误地对 CJK 字体套用了竖排（vert）字形替换</b>。
    /// ImageSharp 2.1.5 对它的依赖是 “&gt;= 1.0.0”，NuGet 解析器取最低满足版，于是默认落到 1.0.0。
    /// 显式钉 1.0.1 即修复（仍为 Apache-2.0）。
    /// </para>
    ///
    /// 本用例钉住这个声明：否则别人清理“看起来多余”的引用时，缺陷会静默回归。
    /// </summary>
    [Fact]
    public void ChartSkill_PinsSixLaborsFontsAtLeast101()
    {
        foreach (var (label, source) in new[]
                 {
                     ("pptx_deck", SkillSource()),
                     ("docx_report", BuiltinDocxSkills.Build("docx_report", "docx_report.skill.txt", "x", "x").Body!),
                 })
        {
            var directives = AguiGroupChat.SkillHosting.SkillNuGetParser.ParseReferences(source);
            var fonts = directives.Where(d => d.PackageId.Equals("SixLabors.Fonts", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(fonts.Count > 0, $"{label} 未显式引用 SixLabors.Fonts（会解析到 1.0.0，导致图表标点被竖排替换）");
            foreach (var d in fonts)
            {
                Assert.NotNull(d.Version);
                var v = Version.Parse(d.Version!);
                Assert.True(v >= new Version(1, 0, 1),
                    $"{label} 把 SixLabors.Fonts 钉在 {d.Version}，低于 1.0.1：图表里的“—（）「」” 会被错误地竖排渲染。");
            }
        }
    }

    // ===== 设计系统：命名调色板 =====

    private static readonly string[] NamedPalettes =
    [
        "modern-wellness", "business-authority", "nature-outdoors", "vintage-academic",
        "soft-creative", "bohemian", "vibrant-tech", "craft-artisan",
        "tech-night", "education-charts", "forest-eco", "elegant-fashion",
        "art-food", "luxury-mysterious", "pure-tech-blue", "coastal-coral",
        "vibrant-orange-mint", "platinum-white-gold",
    ];

    /// <summary>只含封面 + 一页内容的最短演示稿，用来快速验证某个主题/风格。</summary>
    private static string MiniDeck(string extraJson)
        => "{\"title\":\"配色检查\"," + extraJson +
           ",\"slides\":[{\"type\":\"cover\",\"title\":\"配色检查\",\"subtitle\":\"对比度校验\"}," +
           "{\"type\":\"content\",\"title\":\"要点\",\"bullets\":[\"第一条要点\",\"第二条要点\"]}]}";

    private static (string Path, JsonDocument Doc) RenderDeck(string json)
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "pptx-skill-last-result.json"), result);
            Assert.DoesNotContain("编译失败", result);
            var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            return (doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!, doc);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    // ---- WCAG 相对亮度（与技能里同一套口径）----

    private static double RelLum(string hex)
    {
        double Ch(int v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        var r = Convert.ToInt32(hex.Substring(0, 2), 16);
        var g = Convert.ToInt32(hex.Substring(2, 2), 16);
        var b = Convert.ToInt32(hex.Substring(4, 2), 16);
        return 0.2126 * Ch(r) + 0.7152 * Ch(g) + 0.0722 * Ch(b);
    }

    private static double Contrast(string a, string b)
    {
        var la = RelLum(a); var lb = RelLum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>彩度（max-min）/max：用来判断一个颜色是不是“能当底色”。</summary>
    private static double Chroma(string hex)
    {
        int C(int i) => Convert.ToInt32(hex.Substring(i * 2, 2), 16);
        var mx = Math.Max(C(0), Math.Max(C(1), C(2)));
        var mn = Math.Min(C(0), Math.Min(C(1), C(2)));
        return mx == 0 ? 0 : (mx - mn) / (double)mx;
    }

    /// <summary>HSL 色相（0~360）；灰阶返回 0。</summary>
    private static double Hue(string hex)
    {
        double C(int i) => Convert.ToInt32(hex.Substring(i * 2, 2), 16) / 255.0;
        double r = C(0), g = C(1), b = C(2);
        var mx = Math.Max(r, Math.Max(g, b));
        var mn = Math.Min(r, Math.Min(g, b));
        var d = mx - mn;
        if (d < 1e-6) return 0;
        double h;
        if (mx == r) h = ((g - b) / d) % 6;
        else if (mx == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static double HueDistance(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>
    /// 18 套命名调色板逐套渲染，并断言派生出来的配色满足可读性底线。
    ///
    /// <para>
    /// 调色板是从外部设计文档搬进来的，色值本身不保证“当正文色好不好看/看不看得清”——
    /// 比如某套的次深色是饱和红、某套全是浅粉。所以技能里加了一道 EnsureContrast 兜底，
    /// 本用例就是那道兜底的钉子：没有它，换一套调色板就可能做出看不清字的稿子。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("modern-wellness")]
    [InlineData("business-authority")]
    [InlineData("nature-outdoors")]
    [InlineData("vintage-academic")]
    [InlineData("soft-creative")]
    [InlineData("bohemian")]
    [InlineData("vibrant-tech")]
    [InlineData("craft-artisan")]
    [InlineData("tech-night")]
    [InlineData("education-charts")]
    [InlineData("forest-eco")]
    [InlineData("elegant-fashion")]
    [InlineData("art-food")]
    [InlineData("luxury-mysterious")]
    [InlineData("pure-tech-blue")]
    [InlineData("coastal-coral")]
    [InlineData("vibrant-orange-mint")]
    [InlineData("platinum-white-gold")]
    public void NamedPalette_RendersAndKeepsTextReadable(string palette)
    {
        var (path, doc) = RenderDeck(MiniDeck("\"theme\":\"" + palette + "\""));
        Assert.True(File.Exists(path), "产物不存在：" + path);

        var p = doc.RootElement.GetProperty("palette");
        string C(string k) => p.GetProperty(k).GetString()!;

        // 正文 / 标题 7:1（AAA 正文级）：幻灯片是投影/压图看的，4.5 只是及格线，看起来会发灰。
        Assert.True(Contrast(C("text"), C("bg")) >= 7.0,
            $"{palette}：正文色 {C("text")} 在底色 {C("bg")} 上对比度只有 {Contrast(C("text"), C("bg")):F2}（要求 ≥7）");
        Assert.True(Contrast(C("primary"), C("bg")) >= 7.0,
            $"{palette}：主色 {C("primary")} 在底色 {C("bg")} 上对比度只有 {Contrast(C("primary"), C("bg")):F2}（要求 ≥7）");
        Assert.True(Contrast(C("secondary"), C("bg")) >= 3.0,
            $"{palette}：副色 {C("secondary")} 在底色 {C("bg")} 上对比度只有 {Contrast(C("secondary"), C("bg")):F2}");
        Assert.True(Contrast(C("accent"), C("bg")) >= 3.0,
            $"{palette}：强调色 {C("accent")} 在底色 {C("bg")} 上对比度只有 {Contrast(C("accent"), C("bg")):F2}");
        // 画在强调色/主色“块”上的字（页码徽标、章节副标题）
        Assert.True(Contrast(C("onAccent"), C("accent")) >= 4.5,
            $"{palette}：页码徽标字 {C("onAccent")} 在强调色 {C("accent")} 上对比度只有 {Contrast(C("onAccent"), C("accent")):F2}");
        Assert.True(Contrast(C("onPrimary"), C("primary")) >= 3.0,
            $"{palette}：章节副标题 {C("onPrimary")} 在主色 {C("primary")} 上对比度只有 {Contrast(C("onPrimary"), C("primary")):F2}");

        // 底色必须是“面”而不是一个饱和色。
        // 实测踩到：education-charts 的最亮色是 E9C46A（亮黄），直接拿去做 bg 就是一张黄底幻灯片。
        // 例外：近黑（RelLum ≤ 0.10）的“彩度”是没意义的（比如 000814 也算彩度 1.0）。
        var bgChroma = Chroma(C("bg"));
        var bgLum = RelLum(C("bg"));
        Assert.True(bgChroma <= 0.30 || bgLum <= 0.10,
            $"{palette}：底色 {C("bg")} 彩度 {bgChroma:F2}、亮度 {bgLum:F3}，不像背景色（应该接近白/黑）");

        // 强调色必须与主色“看得出来不是同一个颜色”：色相拉开 40° 且不比 1.5 更近，或明暗拉开 2.2。
        // 实测踩到：forest-eco 选出 3A5A40，与主色 344E41 色相差 4°、对比度 1.11，强调线等于白画。
        var apContrast = Contrast(C("accent"), C("primary"));
        var apHue = HueDistance(Hue(C("accent")), Hue(C("primary")));
        Assert.True(apContrast >= 2.2 || (apHue >= 40 && apContrast >= 1.5),
            $"{palette}：强调色 {C("accent")} 与主色 {C("primary")} 色相差 {apHue:F0}°、对比度 {apContrast:F2}，基本糊在一起");

        // 卡片/面板底（light）必须是“面”：不能拿调色板里那个中间调饱和色直接铺（实测：
        // education-charts 的橙 F4A261、art-food 的琥珀 E09F3E 铺满卡片，整份稿子一片色块，观感廉价）。
        if (bgLum > 0.20)
        {
            Assert.True(Chroma(C("light")) <= 0.32,
                $"{palette}：卡片面 {C("light")} 彩度 {Chroma(C("light")):F2} 偏高，看起来是一块颜色而不是一个面");
            var surfaceContrast = Contrast(C("light"), C("bg"));
            Assert.True(surfaceContrast >= 1.06 && surfaceContrast <= 3.2,
                $"{palette}：卡片面 {C("light")} 与底色 {C("bg")} 对比度 {surfaceContrast:F2}，要么看不出卡片、要么太重");
        }

        // 主色不能是“发灰”的色：标题发灰是观感差的主要来源之一（近黑除外）。
        if (RelLum(C("primary")) > 0.05)
            Assert.True(Chroma(C("primary")) >= 0.15,
                $"{palette}：主色 {C("primary")} 彩度 {Chroma(C("primary")):F2} 偏低，标题会发灰");

        // 强调色要是“点色”，不是另一个灰。
        Assert.True(Chroma(C("accent")) >= 0.35,
            $"{palette}：强调色 {C("accent")} 彩度 {Chroma(C("accent")):F2} 偏低，做不了点色");
    }

    /// <summary>历史主题名必须继续可用（老调用方不能因为这次改造而产出变化/报错）。</summary>
    [Theory]
    [InlineData("business", "1F3864", "FFFFFF")]
    [InlineData("tech", "0B2545", "FFFFFF")]
    [InlineData("warm", "6B2D2D", "FFFDF9")]
    [InlineData("minimal", "222222", "FFFFFF")]
    [InlineData("dark", "FFFFFF", "0D1117")]
    [InlineData("vivid", "2B2D42", "FFFFFF")]
    public void LegacyTheme_KeepsItsOriginalColors(string theme, string primary, string bg)
    {
        var (_, doc) = RenderDeck(MiniDeck("\"theme\":\"" + theme + "\""));
        var p = doc.RootElement.GetProperty("palette");
        Assert.Equal(primary, p.GetProperty("primary").GetString());
        Assert.Equal(bg, p.GetProperty("bg").GetString());
    }

    // ===== 设计系统：版式风格 =====

    /// <summary>
    /// 4 种 style 都要能渲染，并把实际生效的风格名回显出来。
    /// （“真的改变了版式”由 <see cref="Styles_AreOrderedByMargin"/> 断言，这里只查参数接通。）
    /// </summary>
    [Theory]
    [InlineData("sharp")]
    [InlineData("soft")]
    [InlineData("rounded")]
    [InlineData("pill")]
    public void Style_RendersAndEchoesName(string style)
    {
        var (path, doc) = RenderDeck(MiniDeck("\"style\":\"" + style + "\""));
        Assert.Equal(style, doc.RootElement.GetProperty("style").GetString());
        Assert.True(File.Exists(path));
    }

    /// <summary>未识别的 style 必须安全回落到默认 soft（不报错、不半途而废）。</summary>
    [Fact]
    public void UnknownStyle_FallsBackToSoft()
    {
        var (_, doc) = RenderDeck(MiniDeck("\"style\":\"neon-cyberpunk\""));
        Assert.Equal("soft", doc.RootElement.GetProperty("style").GetString());
    }

    /// <summary>4 种风格的页边距必须严格递增（sharp &lt; soft &lt; rounded &lt; pill）。</summary>
    [Fact]
    public void Styles_AreOrderedByMargin()
    {
        // 独立跑一遍，不依赖 Theory 的执行顺序/并行
        var margins = new Dictionary<string, long>();
        foreach (var style in new[] { "sharp", "soft", "rounded", "pill" })
        {
            var (path, _) = RenderDeck(MiniDeck("\"style\":\"" + style + "\""));
            using var zip = ZipFile.OpenRead(path);
            var slide = zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml");
            using var r = new StreamReader(slide.Open());
            var xml = r.ReadToEnd();
            var xs = System.Text.RegularExpressions.Regex.Matches(xml, "<a:off x=\"(\\d+)\"")
                .Select(m => long.Parse(m.Groups[1].Value)).ToList();
            margins[style] = xs.Min();
        }
        Assert.True(margins["sharp"] < margins["soft"], $"sharp({margins["sharp"]}) 应比 soft({margins["soft"]}) 更窄");
        Assert.True(margins["soft"] < margins["rounded"], $"soft({margins["soft"]}) 应比 rounded({margins["rounded"]}) 更窄");
        Assert.True(margins["rounded"] < margins["pill"], $"rounded({margins["rounded"]}) 应比 pill({margins["pill"]}) 更宽");
    }

    // ===== 新页型 =====

    /// <summary>新增的 4 种页型（大数字 / 网格 / 时间轴 / 图标行）都要能产出合法 pptx 且内容确实落进页面。</summary>
    [Fact]
    public void NewLayoutTypes_RenderAndValidate()
    {
        var json = JsonSerializer.Serialize(new
        {
            title = "新页型检查",
            theme = "education-charts",
            style = "rounded",
            slides = new object[]
            {
                new { type = "cover", title = "新页型检查" },
                new { type = "stats", title = "关键数据", items = new object[]
                    { new { value = "3.2×", label = "交付提速" }, new { value = "98.9%", label = "可用性" }, new { value = "12", label = "内置岗位" } } },
                new { type = "grid", title = "能力矩阵", cols = 2, items = new object[]
                    { new { title = "协作", text = "多角色同场会商" }, new { title = "记忆", text = "RAG 长期记忆" },
                      new { title = "交付", text = "直接产出文件" }, new { title = "治理", text = "权限与审计" } } },
                new { type = "timeline", title = "落地节奏", items = new object[]
                    { new { title = "调研", detail = "梳理岗位" }, new { title = "试点", detail = "单团队验证" },
                      new { title = "推广", detail = "全公司铺开" } } },
                new { type = "iconRows", title = "核心价值", items = new object[]
                    { new { icon = "1", title = "更省事", text = "一句话产出成品文件" },
                      new { icon = "2", title = "更懂行", text = "记忆沉淀行业经验" } } },
                // content + layout 别名（让模型不必记多个 type）
                new { type = "content", title = "子类型别名", layout = "grid", items = new object[]
                    { new { title = "别名生效", text = "layout=grid 应走网格渲染" }, new { title = "第二格", text = "…" } } },
            },
        });

        var (path, doc) = RenderDeck(json);
        Assert.Equal(6, doc.RootElement.GetProperty("slides").GetInt32());

        using var zip = ZipFile.OpenRead(path);
        var text = string.Join("\n", zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .Select(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }));
        Assert.Contains("3.2×", text);
        Assert.Contains("交付提速", text);
        Assert.Contains("能力矩阵", text);
        Assert.Contains("多角色同场会商", text);
        Assert.Contains("落地节奏", text);
        Assert.Contains("试点", text);
        Assert.Contains("更懂行", text);
        Assert.Contains("别名生效", text);

        // 时间轴的序号圆是真的“椭圆”形状（不是矩形色块）
        Assert.Contains("prst=\"ellipse\"", text);

        // 全量 schema 校验（新页型也可能写出非法 DrawingML）
        using var ppt = PresentationDocument.Open(path, false);
        var validator = new OpenXmlValidator();
        var errors = validator.Validate(ppt).ToList();
        Assert.True(errors.Count == 0,
            "新页型产物未通过 OpenXML schema 校验：\n" +
            string.Join("\n", errors.Take(10).Select(e => e.Description + " @ " + e.Path?.XPath)));
    }

    // ===== 原生图表（ChartPart）=====

    /// <summary>
    /// 原生图表必须产出真正的 ChartPart + 嵌入数据工作簿，且通过 schema 校验。
    ///
    /// <para>
    /// 这是本项目风险最高的一类改动：DrawingML 图表的子元素顺序是 schema 强制的，
    /// 顺序错了 PowerPoint 就报“需要修复”，而这类错误单部件校验器未必拦得住（曾经踩过）。
    /// 所以这里把能做的不依赖 PowerPoint 的检查全做上：部件存在、关系存在、嵌入工作簿是合法 xlsx、
    /// ChartSpace 通过 OpenXmlValidator、且缓存值与输入一致。
    /// </para>
    ///
    /// <para>仍然需要在 Windows 上用 PowerPoint 真开一次做人工验收（见 README “原生图表”一节）。</para>
    /// </summary>
    [Fact]
    public void NativeCharts_ProduceEditableChartParts()
    {
        var json = JsonSerializer.Serialize(new
        {
            title = "原生图表检查",
            theme = "education-charts",
            slides = new object[]
            {
                new { type = "cover", title = "原生图表检查" },
                new { type = "chart", title = "活跃团队", chartType = "bar-native",
                      categories = new[] { "Q1", "Q2", "Q3" },
                      series = new object[] { new { name = "团队数", values = new[] { 120.0, 260.0, 430.0 } } } },
                new { type = "chart", title = "增长", chartType = "line-native",
                      categories = new[] { "一月", "二月" },
                      series = new object[] { new { name = "环比", values = new[] { 1.5, 2.25 } } } },
                new { type = "chart", title = "占比", chartType = "pie-native",
                      categories = new[] { "甲", "乙" },
                      series = new object[] { new { name = "占比", values = new[] { 70.0, 30.0 } } } },
            },
        });

        var (path, doc) = RenderDeck(json);
        Assert.Equal(3, doc.RootElement.GetProperty("nativeCharts").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("nativeChartFallback").ValueKind);

        using var zip = ZipFile.OpenRead(path);
        // SDK 把图表部件放在 ppt/slides/charts/ 下（OPC 路径本身任意，靠 content-type + 关系认）
        var chartParts = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/charts/chart", StringComparison.Ordinal)
                     && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, chartParts.Count);

        // 每张图都要有嵌入数据工作簿（否则“编辑数据”拿不到表格）
        var embedded = zip.Entries
            .Where(e => e.FullName.Contains("/charts/embeddings/", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, embedded.Count);
        foreach (var e in embedded)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ms.Position = 0;
            // 嵌入工作簿必须是可打开的 xlsx
            using var wb = SpreadsheetDocument.Open(ms, false);
            var sheet = wb.WorkbookPart!.WorksheetParts.First().Worksheet;
            Assert.NotNull(sheet);
        }

        // 幻灯片上要有指向 chart 的 graphicData，否则图表只是个空框
        var slide2 = zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml");
        using (var r = new StreamReader(slide2.Open()))
        {
            var xml = r.ReadToEnd();
            Assert.Contains("drawingml/2006/chart", xml);
            Assert.Contains("r:id=", xml);
        }

        // ChartSpace 的缓存值确实写进去了（不依赖 PowerPoint 就能看出数据对错）
        using (var r = new StreamReader(zip.Entries.First(e =>
                   e.FullName.StartsWith("ppt/slides/charts/chart1.xml", StringComparison.Ordinal)).Open()))
        {
            var xml = r.ReadToEnd();
            Assert.Contains("团队数", xml);
            Assert.Contains("430", xml);     // 最后一个缓存值
            Assert.Contains("Sheet1!", xml); // 数据源引用
        }

        // 包整体仍然通过 schema 校验（新增 ChartPart 不能把整包弄非法）
        using var ppt = PresentationDocument.Open(path, false);
        var errors = new OpenXmlValidator().Validate(ppt).ToList();
        Assert.True(errors.Count == 0,
            "原生图表产物未通过 schema 校验：\n" +
            string.Join("\n", errors.Take(10).Select(e => e.Description + " @ " + e.Path?.XPath)));
    }

    /// <summary>不支持原生的图型（如 doughnut）要如实降级到渲图，并在返回里说明。</summary>
    [Fact]
    public void NativeChart_UnsupportedKind_FallsBackAndSaysSo()
    {
        var json = JsonSerializer.Serialize(new
        {
            title = "降级检查",
            slides = new object[]
            {
                new { type = "cover", title = "降级检查" },
                new { type = "chart", title = "环形图", chartType = "doughnut-native",
                      categories = new[] { "甲", "乙" },
                      series = new object[] { new { name = "占比", values = new[] { 60.0, 40.0 } } } },
            },
        });

        var (path, doc) = RenderDeck(json);
        Assert.Equal(0, doc.RootElement.GetProperty("nativeCharts").GetInt32());
        Assert.Equal("doughnut", doc.RootElement.GetProperty("nativeChartFallback").GetString());
        // 降级后仍然出了图表（PNG），不是空页
        using var zip = ZipFile.OpenRead(path);
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal));
    }

    // ===== 读取既有 pptx / 套模板 =====

    private static JsonDocument RunRaw(string json)
    {
        var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
        Assert.DoesNotContain("编译失败", result);
        return JsonDocument.Parse(result);
    }

    /// <summary>读取模式：把既有 pptx 的文本按页读回来。</summary>
    [Fact]
    public void ReadAction_ReturnsPerSlideText()
    {
        var (srcPath, _) = RenderDeck(MiniDeck("\"theme\":\"forest-eco\""));
        Assert.True(File.Exists(srcPath));

        var json = JsonSerializer.Serialize(new { action = "read", path = srcPath });
        using var doc = RunRaw(json);

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), "读取应成功");
        Assert.Equal("read", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("slides").GetInt32());

        var text = doc.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("配色检查", text);
        Assert.Contains("第一条要点", text);

        // 逐页文本也要给出来，便于模型按页处理
        var perSlide = doc.RootElement.GetProperty("slideTexts");
        Assert.Equal(2, perSlide.GetArrayLength());
        Assert.Contains("第二条要点", perSlide[1].GetProperty("texts").EnumerateArray().Select(x => x.GetString()));
    }

    /// <summary>读取一个不存在的路径要给出可读错误，而不是抛异常 / 假装成功。</summary>
    [Fact]
    public void ReadAction_MissingFile_FailsReadably()
    {
        var missing = Path.Combine(Path.GetTempPath(), "不存在的文件-" + Guid.NewGuid().ToString("N") + ".pptx");
        using var doc = RunRaw(JsonSerializer.Serialize(new { action = "read", path = missing }));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("找不到文件", doc.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// 套模板出稿：保留模板的母版 / 版式 / 主题，把我们的内容渲染进去。
    ///
    /// <para>
    /// 关键断言是“<b>不动原件</b>”和“<b>只有一套母版</b>”——
    /// 前者是用户资产安全，后者能证明我们真的用上了模板而不是又自己拼了一套。
    /// </para>
    /// </summary>
    [Fact]
    public void TemplateMode_ReusesTemplateMasterAndLeavesTheOriginalUntouched()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            // 先用技能自己生成一份当作“用户模板”
            var templateJson = JsonSerializer.Serialize(new
            {
                title = "公司模板",
                theme = "business-authority",
                slides = new object[]
                {
                    new { type = "cover", title = "公司模板封面" },
                    new { type = "content", title = "模板原有页", bullets = new[] { "这页默认应该被清掉" } },
                },
            });
            using var t = RunRaw(templateJson);
            var templatePath = t.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
            var before = File.ReadAllBytes(templatePath);

            var outPath = Path.Combine(outDir, "套模板产物.pptx");
            var json = JsonSerializer.Serialize(new
            {
                title = "套模板产物",
                template = templatePath,
                outputPath = outPath,
                slides = new object[]
                {
                    new { type = "cover", title = "套模板产物", subtitle = "沿用模板的皮" },
                    new { type = "content", title = "新内容页", bullets = new[] { "内容来自这次请求" } },
                },
            });
            using var r = RunRaw(json);
            Assert.True(r.RootElement.GetProperty("ok").GetBoolean(),
                r.RootElement.GetProperty("message").GetString());
            Assert.Equal(2, r.RootElement.GetProperty("slides").GetInt32());

            // 原件必须一字未改
            Assert.Equal(before, File.ReadAllBytes(templatePath));

            using var zip = ZipFile.OpenRead(outPath);
            // 只有一套版本 / 主题：证明用的是模板的，不是我们又新建了一套
            // （主题的存放路径跟创建方式有关：SDK 会放在 ppt/slideMasters/theme/ 下，所以按名字匹配）
            Assert.Single(zip.Entries, e => e.FullName.StartsWith("ppt/slideMasters/slideMaster", StringComparison.Ordinal)
                                         && e.FullName.EndsWith(".xml", StringComparison.Ordinal));
            Assert.Single(zip.Entries, e => e.FullName.Contains("/theme", StringComparison.Ordinal)
                                         && e.FullName.EndsWith(".xml", StringComparison.Ordinal));
            Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                                                 && e.FullName.EndsWith(".xml", StringComparison.Ordinal)));

            var all = string.Join("\n", zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                .Select(e => { using var sr = new StreamReader(e.Open()); return sr.ReadToEnd(); }));
            Assert.Contains("套模板产物", all);
            Assert.Contains("内容来自这次请求", all);
            // 默认不保留模板原有页
            Assert.DoesNotContain("这页默认应该被清掉", all);

            // 仍然通过 schema 校验
            using var ppt = PresentationDocument.Open(outPath, false);
            var errors = new OpenXmlValidator().Validate(ppt).ToList();
            Assert.True(errors.Count == 0,
                "套模板产物未通过 schema 校验：\n" +
                string.Join("\n", errors.Take(10).Select(e => e.Description + " @ " + e.Path?.XPath)));

            // 配色应来自模板（business-authority 的 accent1 = 2B2D42 作为 primary）
            Assert.Equal("2B2D42", r.RootElement.GetProperty("palette").GetProperty("primary").GetString());
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>
    /// 套深色底模板不能变成白底。
    ///
    /// <para>
    /// 实测踩到：底色原本只读主题色板的 lt1，而 lt1 几乎总是 sysClr(window)（永远是白），
    /// 于是拿一份 tech-night 深色模板出稿，产出来却是白底。
    /// 真正的底色写在母版/幻灯片的 &lt;p:bg&gt; 里，要从那里读。
    /// </para>
    /// </summary>
    [Fact]
    public void TemplateMode_KeepsDarkTemplateBackground()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            using var t = RunRaw(JsonSerializer.Serialize(new
            {
                title = "深色模板",
                theme = "tech-night",
                slides = new object[] { new { type = "cover", title = "深色模板封面" } },
            }));
            var templatePath = t.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

            var outPath = Path.Combine(outDir, "深色套模板.pptx");
            using var r = RunRaw(JsonSerializer.Serialize(new
            {
                title = "深色套模板",
                template = templatePath,
                outputPath = outPath,
                slides = new object[] { new { type = "cover", title = "深色套模板" } },
            }));
            Assert.True(r.RootElement.GetProperty("ok").GetBoolean(),
                r.RootElement.GetProperty("message").GetString());
            Assert.Equal("000814", r.RootElement.GetProperty("palette").GetProperty("bg").GetString());

            using var zip = ZipFile.OpenRead(outPath);
            // 不要写死 slide1.xml：模板旧的 slide 部件删掉后，SDK 不会回收名字，新页可能是 slide2.xml
            var slideEntries = zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                         && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToList();
            Assert.Single(slideEntries);
            using var sr = new StreamReader(slideEntries[0].Open());
            var xml = sr.ReadToEnd();
            Assert.Contains("<p:bg>", xml);
            Assert.Contains("000814", xml);
            Assert.DoesNotContain("val=\"FFFFFF\"/></a:solidFill><a:effectLst/></p:bgPr>", xml);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>
    /// keepTemplateSlides=true：保留模板原有页，把新页追加在后面，且 <c>slides</c> 报的是**整份稿子的总页数**。
    ///
    /// <para>
    /// 报“本次新生成的页数”是个陷阱：调用方/用户会以为模板原有页丢了（实测确实报成 1 而实际 3 页）。
    /// </para>
    /// </summary>
    [Fact]
    public void TemplateMode_KeepTemplateSlides_AppendsAndReportsTotal()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            using var t = RunRaw(JsonSerializer.Serialize(new
            {
                title = "保留模板",
                theme = "minimal",
                slides = new object[]
                {
                    new { type = "cover", title = "模板封面页" },
                    new { type = "content", title = "模板第二页", bullets = new[] { "应被保留" } },
                },
            }));
            var templatePath = t.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

            var outPath = Path.Combine(outDir, "保留产物.pptx");
            using var r = RunRaw(JsonSerializer.Serialize(new
            {
                title = "保留产物",
                template = templatePath,
                outputPath = outPath,
                keepTemplateSlides = true,
                slides = new object[] { new { type = "content", title = "追加的新页", bullets = new[] { "新增内容" } } },
            }));
            Assert.True(r.RootElement.GetProperty("ok").GetBoolean(),
                r.RootElement.GetProperty("message").GetString());
            Assert.Equal(3, r.RootElement.GetProperty("slides").GetInt32());   // 2 + 1，不是 1

            using var zip = ZipFile.OpenRead(outPath);
            var slides = zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                         && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToList();
            Assert.Equal(3, slides.Count);
            var joined = string.Join("\n", slides.Select(e =>
            {
                using var sr = new StreamReader(e.Open());
                return sr.ReadToEnd();
            }));
            Assert.Contains("模板第二页", joined);   // 模板原有页保留
            Assert.Contains("新增内容", joined);     // 新页追加
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>模板路径与输出路径相同时必须报错：否则会覆盖用户的原件。</summary>
    [Fact]
    public void TemplateMode_SamePathAsTemplate_IsRejected()
    {
        var (srcPath, _) = RenderDeck(MiniDeck("\"theme\":\"minimal\""));
        using var doc = RunRaw(JsonSerializer.Serialize(new
        {
            title = "覆盖测试",
            template = srcPath,
            outputPath = srcPath,
            slides = new object[] { new { type = "cover", title = "x" } },
        }));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("会覆盖原件", doc.RootElement.GetProperty("message").GetString());
    }

    // ===== 文字溢出：量准 + 缩字号 + 分页 / 截断 =====

    /// <summary>
    /// 文字过多时的处理链条：**先缩字号，缩到下限还放不下就分页（不丢内容）**。
    ///
    /// <para>
    /// 实测踩过的根因：估算高度的公式把行高系数当成 1.0、段前距的单位算小了 100 倍，
    /// 结果“缩字号”几乎从不触发，文字直接溢出自己的框 —— 以前之所以看起来尚可，
    /// 是因为文件里的 <c>&lt;a:normAutofit/&gt;</c> 让 LibreOffice 替我们缩了，
    /// 而 PowerPoint 打开时并不重算 autofit。
    /// </para>
    ///
    /// <para>
    /// 本用例把 20 条长要点顶上去：必须拆成多页（页数 > 输入页数）、每一条都不能丢、
    /// 标题带「（n/m）」，并且自检不得报 <c>textOverflow</c>。
    /// </para>
    /// </summary>
    [Fact]
    public void TooManyBullets_PaginateInsteadOfOverflowing()
    {
        var bullets = Enumerable.Range(1, 20)
            .Select(i => $"第 {i} 条要点：平台治理需要统一账号体系权限模型与审计日志，"
                       + "覆盖组织架构的全部层级，确保任何一次权限变更都能追溯到操作人、时间与影响范围。")
            .ToArray();
        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "要点分页",
            slides = new object[]
            {
                new { type = "cover", title = "要点分页" },
                new { type = "content", title = "二十条要点", bullets },
            },
        }));

        var qa = doc.RootElement.GetProperty("qa");
        Assert.Equal(0, qa.GetProperty("issueCount").GetInt32());
        var slides = qa.GetProperty("slides").GetInt32();
        Assert.True(slides > 2, "20 条长要点应该拆成多页，实际只有 " + slides + " 页");
        Assert.True(ShapesOutsideCanvas(path).Count == 0);

        // 不丢内容：每条要点都能在产物里找到，且续页标题带「（n/m）」
        using var back = RunRaw(JsonSerializer.Serialize(new { action = "read", path }));
        var all = back.RootElement.GetProperty("text").GetString()!;
        for (var i = 1; i <= 20; i++)
            Assert.Contains("第 " + i + " 条要点", all);
        Assert.Contains("（1/", all);
    }

    /// <summary>
    /// 超长标题不能顶进正文区：要么缩字号、要么扩大标题框，并且自检不报 textOverflow。
    /// （以前标题是写死 28pt、框写死 60pt，长标题直接盖到正文上。）
    /// </summary>
    [Fact]
    public void LongTitle_ShrinksAndStaysOutOfTheBody()
    {
        var longTitle = "这是一个刻意写得非常非常长以至于在标题区域一行放不下并且需要折成两行才能完整显示的内容页标题，用来验证标题自动缩放";
        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "长标题",
            slides = new object[]
            {
                new { type = "cover", title = "长标题" },
                new { type = "content", title = longTitle, bullets = new[] { "要点一" } },
            },
        }));

        Assert.Equal(0, doc.RootElement.GetProperty("qa").GetProperty("issueCount").GetInt32());
        Assert.True(ShapesOutsideCanvas(path).Count == 0);

        using var zip = ZipFile.OpenRead(path);
        using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open());
        var xml = sr.ReadToEnd();
        // 标题确实被缩过（原始设计字号是 2800 = 28pt）
        Assert.DoesNotContain("sz=\"2800\"", xml);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(xml, "sz=\"(\\d+)\"").Count > 0);
    }

    /// <summary>
    /// 卡片 / 示意图层这类**结构固定的框**不能分页，就退成“缩到下限 + 截断 + 报警”：
    /// 必须<b>明说</b>截掉了多少字（不静默丢内容），且自检不报 textOverflow。
    /// </summary>
    [Fact]
    public void OverlongCardText_IsTrimmedWithAVisibleWarning()
    {
        var longText = string.Concat(Enumerable.Repeat(
            "平台治理需要统一账号体系权限模型与审计日志覆盖组织架构的全部层级。", 8));
        var (_, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "卡片截断",
            slides = new object[]
            {
                new { type = "cover", title = "卡片截断" },
                new
                {
                    type = "matrix", title = "四象限",
                    items = new object[]
                    {
                        new { title = "左上", text = longText },
                        new { title = "右上", text = longText },
                        new { title = "左下", text = longText },
                        new { title = "右下", text = longText },
                    },
                },
            },
        }));

        Assert.Equal(0, doc.RootElement.GetProperty("qa").GetProperty("issueCount").GetInt32());
        var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(x => x.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("四象限 1") && w.Contains("截掉"));
    }

    /// <summary>
    /// 替换文字变长时要如实报警：<c>replaceText</c> 不重排版（这是既定边界），
    /// 但用户不能拿到一份“文字压到别的元素上”的稿子却不知道原因。
    /// </summary>
    [Fact]
    public void ReplaceText_ThatNoLongerFits_WarnsInsteadOfSilence()
    {
        var (src, _) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "替换文字",
            slides = new object[]
            {
                new { type = "cover", title = "替换文字" },
                new { type = "content", title = "要点", bullets = new[] { "短文案" } },
            },
        }));
        var longText = string.Concat(Enumerable.Repeat("替换之后这段话变得非常长非常长非常长。", 30));
        using var doc = RunRaw(JsonSerializer.Serialize(new
        {
            action = "edit", path = src, outputPath = Path.Combine(TempDir(), "edited.pptx"),
            ops = new object[]
            {
                new { op = "replaceText", slides = new[] { 2 }, map = new Dictionary<string, string> { ["短文案"] = longText } },
            },
        }));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(),
            doc.RootElement.GetProperty("message").GetString());
        var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(x => x.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("放不进原文本框"));
    }

    // ===== 页型级越界防护 =====

    /// <summary>把每页所有形状的右/下边界与画布比对，返回越界描述（空 = 没越界）。</summary>
    private static List<string> ShapesOutsideCanvas(string path)
    {
        const long W = 12192000, H = 6858000, Slack = 1000;
        // a:off / a:ext 无论挂在 a:xfrm 还是 p:xfrm 下，都在 drawingml 命名空间
        System.Xml.Linq.XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var bad = new List<string>();
        using var zip = ZipFile.OpenRead(path);
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                                              && e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
        {
            using var sr = new StreamReader(e.Open());
            var doc = System.Xml.Linq.XDocument.Parse(sr.ReadToEnd());
            // 注意：形状的 xfrm 在 drawingml 命名空间（p:spPr/a:xfrm），
            // 但表格/图表的 graphicFrame 用的是 <p:xfrm>（presentation 命名空间）——
            // 只找 a:xfrm 会整个漏掉表格，实测踩到：表格早已画出页面，检查却“通过”。
            // 按 local name 匹配，两种都覆盖。
            foreach (var xfrm in doc.Descendants().Where(x => x.Name.LocalName == "xfrm"))
            {
                var off = xfrm.Element(a + "off");
                var ext = xfrm.Element(a + "ext");
                if (off is null || ext is null) continue;
                var x = long.Parse(off.Attribute("x")!.Value);
                var y = long.Parse(off.Attribute("y")!.Value);
                var cx = long.Parse(ext.Attribute("cx")!.Value);
                var cy = long.Parse(ext.Attribute("cy")!.Value);
                if (x < 0 || y < 0 || x + cx > W + Slack || y + cy > H + Slack)
                    bad.Add($"{e.FullName}: x={x} y={y} cx={cx} cy={cy} → 右={x + cx} 下={y + cy}");
            }
        }
        return bad;
    }

    /// <summary>
    /// 页型级的「内容太多不越界」：目录 / 两栏 / 表格 / 指标卡。
    ///
    /// <para>
    /// 这四类原本都是固定高度文本框，内容一多就画出页面。此用例将它们全部压满
    /// （长条目、长单元格、多行），断言每页所有形状仍在画布内。
    /// 表格尤其狠：“行高写死 + 单元格文字换行”组合会让真个表撑出页面。
    /// </para>
    /// </summary>
    [Fact]
    public void StressPages_KeepEveryShapeInsideCanvas()
    {
        var longLine = new string('字', 60);
        var json = JsonSerializer.Serialize(new
        {
            title = "页型越界压力测试",
            theme = "education-charts",
            slides = new object[]
            {
                new { type = "cover", title = "页型越界压力测试" },
                // 目录：20 项、每项 60 字
                new { type = "toc", title = "目录",
                      items = Enumerable.Range(1, 20).Select(i => $"第{i}章 {longLine}").ToArray() },
                // 两栏：每栏 15 条长要点
                new { type = "twoCol", title = "对比",
                      left = new { heading = "方案一 " + longLine,
                                   bullets = Enumerable.Range(1, 15).Select(i => $"要点{i} {longLine}").ToArray() },
                      right = new { heading = "方案二 " + longLine,
                                    bullets = Enumerable.Range(1, 15).Select(i => $"要点{i} {longLine}").ToArray() } },
                // 表格：30 行 × 5 列，单元格都是长文本
                new { type = "table", title = "明细",
                      headers = new[] { "维度", longLine, longLine, longLine, longLine },
                      rows = Enumerable.Range(1, 30)
                          .Select(i => new[] { $"行{i}", longLine, longLine, longLine, longLine }).ToArray() },
                // 指标卡：4 张，标签长到确实装不下（不极端就不会触发缩字号，断言也就失去意义）
                new { type = "kpi", title = "指标",
                      items = Enumerable.Range(1, 4)
                          .Select(i => new { value = $"{i}23.45%", label = "标签" + new string('字', 120) }).ToArray() },
            },
        });

        var (path, doc) = RenderDeck(json);
        // 页数不再固定：超长表格会被自动拆页（这就是本次新增的能力），所以断言 >= 基础页数
        var pages = doc.RootElement.GetProperty("slides").GetInt32();
        Assert.True(pages >= 5, $"至少应有 5 页（封面/目录/两栏/表格/kpi），实际 {pages}");

        var bad = ShapesOutsideCanvas(path);
        Assert.True(bad.Count == 0,
            "有形状画出了画布（内容太多未自适应）：\n" + string.Join("\n", bad.Take(12)));

        // 上面那条只盖到“形状本身越界”（表格的 graphicFrame 高度是算出来的，能被抓到）。
        // 而 toc / twoCol / kpi 是**固定高度的文本框**：内容撑出去时文本框的 xfrm 仍在界内，
        // 几何检查看不见。所以另镐一条：**缩字号必须真的发生了**。
        // 拿“题面字号”当基准，压力输入下应该出现明显更小的 sz。
        using (var zip = ZipFile.OpenRead(path))
        {
            System.Xml.Linq.XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            System.Xml.Linq.XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";

            // 只取**正文区**那一个形状里的字号。
            // 【别对整个页面取 min】页面上还有标题（28pt）、强调线、右下角页码徽标（11pt），
            // 拿全页 min 永远是 1100（徽标），于是不管有没有缩字号都会“通过”——实测踩过。
            long[] BodySizes(string entry, long bodyY, long bodyH)
            {
                using var sr = new StreamReader(zip.Entries.First(e => e.FullName == entry).Open());
                var doc = System.Xml.Linq.XDocument.Parse(sr.ReadToEnd());
                var sizes = new List<long>();
                foreach (var sp in doc.Descendants(p + "sp"))
                {
                    var off = sp.Descendants(a + "off").FirstOrDefault();
                    if (off is null) continue;
                    var y = long.Parse(off.Attribute("y")!.Value);
                    if (y < bodyY || y > bodyY + bodyH) continue;
                    foreach (System.Text.RegularExpressions.Match m in
                             System.Text.RegularExpressions.Regex.Matches(sp.ToString(), "sz=\"(\\d+)\""))
                        sizes.Add(long.Parse(m.Groups[1].Value));
                }
                Assert.NotEmpty(sizes);
                return sizes.ToArray();
            }

            const long bodyY = 1524000, bodyH = 4495800;   // soft 风格下的正文区

            // 页序会因为自动拆页而变，所以按内容找页，不写死 slideN
            string SlideContaining(string marker)
            {
                foreach (var e in zip.Entries.Where(x => x.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                                                      && x.FullName.EndsWith(".xml", StringComparison.Ordinal)))
                {
                    using var sr = new StreamReader(e.Open());
                    if (sr.ReadToEnd().Contains(marker)) return e.FullName;
                }
                Assert.Fail($"没有找到包含「{marker}」的页");
                return "";
            }

            // 目录（题面 18pt=1800）——压力输入下必然需要缩字号
            Assert.True(BodySizes(SlideContaining("第1章"), bodyY, bodyH).Min() < 1800,
                "目录项这久多还没有缩字号：内容会溢出正文区");
            // 两栏（题面：小标题 19pt / 要点 15pt）
            Assert.True(BodySizes(SlideContaining("方案一"), bodyY, bodyH).Min() < 1500,
                "两栏要点这久多还没有缩字号");
            // 指标卡（题面：数字 32pt / 标签 13pt）
            Assert.True(BodySizes(SlideContaining("23.45%"), bodyY, bodyH).Min() < 1300,
                "指标卡标签这久长还没有缩字号");
        }
    }

    /// <summary>
    /// 超长表格自动拆页：不再“只显示前几行 + 说还有 N 行”，而是真的分到多页。
    ///
    /// <para>
    /// 断言包括：页数变多、每页标题带「（n/m）」、**每一行都还在**（拆页不能丢数据）、
    /// 且不存在「另有 N 行未显示」这类截断提示。
    /// </para>
    /// </summary>
    [Fact]
    public void LongTable_IsSplitAcrossPagesWithoutLosingRows()
    {
        const int rowCount = 30;
        var json = JsonSerializer.Serialize(new
        {
            title = "长表拆页",
            slides = new object[]
            {
                new { type = "cover", title = "长表拆页" },
                new { type = "table", title = "明细",
                      headers = new[] { "序", "名称", "说明" },
                      rows = Enumerable.Range(1, rowCount)
                          .Select(i => new[] { $"R{i}", $"项目{i}", "这是一段较长的说明文字，用来把单元格塞满以触发换行" }).ToArray() },
            },
        });

        var (path, doc) = RenderDeck(json);
        var pages = doc.RootElement.GetProperty("slides").GetInt32();
        Assert.True(pages > 2, $"30 行表应该被拆成多页，实际总页数 {pages}");

        using var zip = ZipFile.OpenRead(path);
        var tablePages = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                     && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .Select(e => { using var sr = new StreamReader(e.Open()); return sr.ReadToEnd(); })
            .Where(x => x.Contains("明细")).ToList();
        Assert.True(tablePages.Count > 1, "应该有多张续表");

        var all = string.Join("\n", tablePages);
        // 拆页必须覆盖到每一行（不能丢数据），也不能再出现截断提示
        for (var i = 1; i <= rowCount; i++)
            Assert.Contains($"R{i}", all);
        Assert.DoesNotContain("行未显示", all);
        // 标题要能看出是续表
        Assert.Contains("（1/", all);

        Assert.True(ShapesOutsideCanvas(path).Count == 0, "拆页后的表格仍在画布外");
    }

    /// <summary>
    /// 每个**对外声明的**页型都真的接上了渲染器，而不是静默回落到默认要点页。
    ///
    /// <para>
    /// 实测踩到：`case "twoCol"` 写成了驼峰，而分发前 type 已 <c>ToLowerInvariant()</c>，
    /// 于是 <b>twoCol 从来没生效过</b> —— 每一页都静默变成要点页，文档里却写着支持两栏。
    /// 这类“类型没接上”的 bug 不会报错、不会崩，只会默默给你错的东西。
    /// </para>
    ///
    /// <para>
    /// 做法：拿同一批内容分别以“真实页型”和“不存在的页型”各出一份，
    /// 逐页比 XML——两者一模一样就说明这个页型根本没接上（content 本来就走默认，故排除）。
    /// </para>
    /// </summary>
    [Fact]
    public void EveryDocumentedSlideType_IsActuallyWired()
    {
        var types = new[]
        {
            "cover", "toc", "section", "twoCol", "table", "kpi", "stats",
            "grid", "timeline", "iconRows", "quote", "image", "chart", "summary", "end",
            // 进度与示意（插图）页型：漏写 case 会静默回落成默认要点页，这个用例就是拦它
            "progress", "pyramid", "funnel", "matrix", "cycle", "stack", "hero",
        };

        string Build(IEnumerable<string> typeNames)
            => JsonSerializer.Serialize(new
            {
                title = "页型分发检查",
                slides = typeNames.Select(SlideOf).ToArray(),
            });

        var (realPath, realDoc) = RenderDeck(Build(types));
        var (fallbackPath, fallbackDoc) = RenderDeck(
            Build(types.Select(_ => "definitely-not-a-page-type")));
        Assert.Equal(types.Length, realDoc.RootElement.GetProperty("slides").GetInt32());

        static string[] Slides(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                         && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                .OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal)
                .Select(e => { using var sr = new StreamReader(e.Open()); return sr.ReadToEnd(); })
                .ToArray();
        }

        var real = Slides(realPath);
        var fallback = Slides(fallbackPath);
        Assert.Equal(types.Length, real.Length);
        Assert.Equal(types.Length, fallback.Length);

        var notWired = new List<string>();
        for (var i = 0; i < types.Length; i++)
            if (real[i] == fallback[i]) notWired.Add(types[i]);

        Assert.True(notWired.Count == 0,
            "下列页型没有接上渲染器（与“不存在的页型”产出完全相同，即静默回落到了默认要点页）："
            + string.Join(", ", notWired));
    }

    /// <summary>一份“什么字段都给了”的幻灯片，用于把差异**只留在 type 上**。</summary>
    private static Dictionary<string, object?> SlideOf(string type) => new()
    {
        ["type"] = type,
        ["title"] = "标题",
        ["subtitle"] = "副标题",
        ["text"] = "引言正文",
        ["cite"] = "出处",
        ["items"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["value"] = "1", ["label"] = "标签", ["title"] = "小标题",
                ["detail"] = "说明", ["text"] = "正文", ["icon"] = "1",
            },
        },
        ["bullets"] = new object[] { "要点一", "要点二" },
        ["left"] = new Dictionary<string, object?> { ["heading"] = "左栏", ["bullets"] = new object[] { "左一" } },
        ["right"] = new Dictionary<string, object?> { ["heading"] = "右栏", ["bullets"] = new object[] { "右一" } },
        ["headers"] = new object[] { "列一", "列二" },
        ["rows"] = new object[] { new object[] { "a", "b" } },
        ["categories"] = new object[] { "甲", "乙" },
        ["series"] = new object[]
        {
            new Dictionary<string, object?> { ["name"] = "系列", ["values"] = new object[] { 1.0, 2.0 } },
        },
        ["chartType"] = "bar",
        ["path"] = Path.Combine(Path.GetTempPath(), "不存在的图片-" + Guid.NewGuid().ToString("N") + ".png"),
    };

    // ===== 内置图标 =====

    /// <summary>
    /// 内置图标：名字认得出→画成 PNG 嵌进去（不再是“1~2 个字”）；认不出→回退成文字，不能变成空白。
    /// 同时确认图标没画出彩色圆。
    /// </summary>
    [Fact]
    public void IconRows_RendersBuiltInIconsAsImages()
    {
        var json = JsonSerializer.Serialize(new
        {
            title = "图标检查",
            theme = "forest-eco",
            slides = new object[]
            {
                new { type = "cover", title = "图标检查" },
                new { type = "iconRows", title = "图标", items = new object[]
                    {
                        new { icon = "check", title = "甲", text = "说明甲" },
                        new { icon = "star",  title = "乙", text = "说明乙" },
                        new { icon = "9",     title = "丙", text = "说明丙" },   // 不是图标名
                    } },
            },
        });

        var (path, _) = RenderDeck(json);
        using var zip = ZipFile.OpenRead(path);
        var media = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal)
                     && e.FullName.EndsWith(".png", StringComparison.Ordinal)).ToList();
        // 两个图标名 → 两张 PNG；第三个不是图标名，不应该也画图
        Assert.Equal(2, media.Count);

        // 每张图标 PNG 都要是合法的 PNG（首 8 字节签名）
        foreach (var e in media)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var b = ms.ToArray();
            Assert.True(b.Length > 100, "图标 PNG 太小：" + b.Length);
            Assert.Equal(0x89, b[0]);
            Assert.Equal((byte)'P', b[1]);
        }

        // 未知名字仍然以文字回退（不能变成空白）
        using (var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open()))
        {
            var slide = sr.ReadToEnd();
            Assert.Contains("9", slide);
            Assert.Contains("说明甲", slide);
        }

        // 图标不能画出圆外
        Assert.True(ShapesOutsideCanvas(path).Count == 0);
    }

    /// <summary>与技能内同一口径的字形覆盖判定。</summary>
    private static bool CanRenderCjk(SixLabors.Fonts.FontFamily family)
    {
        try
        {
            var font = family.CreateFont(16);
            foreach (var ch in "知聚平台中数据报告")
            {
                if (!font.TryGetGlyphs(new SixLabors.Fonts.Unicode.CodePoint(ch), out var glyphs)
                    || glyphs is null || glyphs.Count == 0)
                    return false;
            }
            return true;
        }
        catch { return false; }
    }

    // ====================================================================
    // 版式变体 / 图文混排 / 进度仪表 / 新图表 / 字体配对 / QA / 原地编辑
    // ====================================================================

    /// <summary>一份 16×16 的真 PNG（红底蓝块），用于图文混排用例。</summary>
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAL0lEQVR4nGM8YWPDQApgIkk1AxkaWOCsMJsePOpWHSmhl5OYRjUQAUgOJcbBl/gA3ngFoWwd6YkAAAAASUVORK5CYII=";

    /// <summary>把内置小 PNG 写到临时目录，返回路径（图片类用例的输入）。</summary>
    private static string TinyPng(string name = "tiny.png")
    {
        var dir = TempDir();
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Convert.FromBase64String(TinyPngBase64));
        return path;
    }

    /// <summary>
    /// 所有版式变体都要能出稿、且不越界。
    ///
    /// <para>
    /// 设计文档里每个页型给了多种排法（封面 4 种、章节 3 种、目录 3 种、小结 3 种）。
    /// 变体是“枚举”出来的，漏一个 case 就会静默回落到默认版式（以前 twoCol 就这么死过），
    /// 所以这里把每一个变体都摆上去，断言不发 warnings、且 QA 不报越界。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("cover", "left", "left")]
    [InlineData("cover", "center", "center")]
    [InlineData("cover", "image", "image")]
    [InlineData("cover", "split", "split")]
    [InlineData("section", "number", "number")]
    [InlineData("section", "bar", "bar")]
    [InlineData("section", "full", "full")]
    [InlineData("toc", "list", "list")]
    [InlineData("toc", "grid", "grid")]
    [InlineData("toc", "sidebar", "sidebar")]
    [InlineData("summary", "list", "list")]
    [InlineData("summary", "cta", "cta")]
    [InlineData("summary", "split", "split")]
    public void EveryVariant_RendersWithoutOverflow(string type, string variant, string _)
    {
        var img = TinyPng();
        var expectedTitle = type switch { "toc" => "目录", "summary" => "小结", _ => "变体标题" };
        var slide = type switch
        {
            "cover" => new { type, title = expectedTitle, subtitle = "副标题", author = "知聚", date = "2026-09", variant, path = img },
            "section" => new { type, title = expectedTitle, subtitle = "一句话说明", variant } as object,
            "toc" => (object)new
            {
                type, title = expectedTitle, variant,
                items = new[] { "一、产品概览", "二、核心能力", "三、交付流程", "四、总结展望" },
            },
            _ => new
            {
                type, title = expectedTitle, variant,
                bullets = new[] { "要点一：已经完成", "要点二：正在推进" },
                items = new[] { "确认试点范围", "排期联调", "上线评估" },
                actions = new[] { "确认试点", "排期联调" },
                contact = "team@example.com",
            },
        };

        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "变体检查",
            theme = "pure-tech-blue",
            slides = new object[] { new { type = "cover", title = "变体检查" }, slide },
        }));

        Assert.Empty(doc.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.True(ShapesOutsideCanvas(path).Count == 0,
            type + "/" + variant + " 有形状画出画布：" + string.Join("; ", ShapesOutsideCanvas(path)));
        // 该变体确实渲染出了自己的内容（漏 case 会静默回落成默认版式）
        using var zip = ZipFile.OpenRead(path);
        using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open());
        Assert.Contains(expectedTitle, sr.ReadToEnd());
    }

    /// <summary>
    /// 同一页型的各个变体必须真的<b>长得不一样</b>。
    ///
    /// <para>
    /// 上一个用例只断言“标题在”——而一个没实现的变体会<b>静默回落</b>到默认版式，标题照样在：
    /// 以前 <c>case "twoCol"</c> 写成驼峰就永远匹配不上、两栏页型从没生效过，而这类断言拦不住。
    /// 所以这里把同页型的全部变体放一起，直接比较生成的页面 XML 是否两两不同。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("cover", "left,center,image,split")]
    [InlineData("section", "number,bar,full")]
    [InlineData("toc", "list,grid,sidebar")]
    [InlineData("summary", "list,cta,split")]
    public void VariantsOfSameType_ProduceDifferentPages(string type, string variantsCsv)
    {
        var variants = variantsCsv.Split(',');
        var img = TinyPng();
        var slides = new List<object> { new { type = "cover", title = "变体对比" } };
        foreach (var v in variants)
        {
            slides.Add(type switch
            {
                "cover" => new { type, title = "同一标题", variant = v, path = img } as object,
                "section" => (object)new { type, title = "同一标题", variant = v },
                "toc" => new
                {
                    type, title = "同一标题", variant = v,
                    items = new[] { "一、甲", "二、乙", "三、丙" },
                },
                _ => new
                {
                    type, title = "同一标题", variant = v,
                    bullets = new[] { "甲项", "乙项" },
                    items = new[] { "行动甲", "行动乙" },
                    contact = "a@b.c",
                },
            });
        }

        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new { title = "变体对比", theme = "forest-eco", slides }));
        Assert.Empty(doc.RootElement.GetProperty("warnings").EnumerateArray());

        using var zip = ZipFile.OpenRead(path);
        var pages = new List<string>();
        // slide2..slideN 对应各变体
        for (var i = 2; i <= variants.Length + 1; i++)
        {
            using var sr = new StreamReader(zip.Entries.First(e => e.FullName == $"ppt/slides/slide{i}.xml").Open());
            // 页码徽标带页号（02/03/…），每页必然不同——先把它抹平，否则比的是页号不是版式
            pages.Add(System.Text.RegularExpressions.Regex.Replace(sr.ReadToEnd(), "<a:t>\\d{2}</a:t>", "<a:t>NN</a:t>"));
        }
        Assert.Equal(variants.Length, pages.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// 图文混排：图左文右 / 文左图右 / 半出血叠字 / 图廊。
    /// 以前只有“整页一张图”，报告类幻灯片最常用的左图右文做不到。
    /// </summary>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("bleed")]
    [InlineData("gallery")]
    public void ImageLayouts_EmbedPictureAndTextWithoutOverflow(string variant)
    {
        var img = TinyPng("a.png");
        var img2 = TinyPng("b.png");
        var slide = variant == "gallery"
            ? (object)new
            {
                type = "image", title = "图廊", variant,
                images = new object[] { new { path = img, caption = "第一张" }, new { path = img2, caption = "第二张" } },
            }
            : new
            {
                type = "image", title = "图文混排", variant, path = img,
                heading = "小标题", bullets = new[] { "左文右图", "图片按框裁切" },
            };

        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "图文检查",
            slides = new object[] { new { type = "cover", title = "图文检查" }, slide },
        }));

        Assert.Empty(doc.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.True(ShapesOutsideCanvas(path).Count == 0,
            variant + " 有形状画出画布：" + string.Join("; ", ShapesOutsideCanvas(path)));
        using var zip = ZipFile.OpenRead(path);
        // 图被真的嵌进包里（不是只画了个占位框）
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal));
        using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open());
        var xml = sr.ReadToEnd();
        Assert.Contains("<p:pic>", xml);
        if (variant != "bleed") Assert.Contains("图", xml);
    }

    /// <summary>图片缺失时要如实报 warning 并画占位块，不能静默生成一页空白。</summary>
    [Fact]
    public void ImageLayout_MissingFile_WarnsAndDrawsPlaceholder()
    {
        var missing = Path.Combine(Path.GetTempPath(), "不存在-" + Guid.NewGuid().ToString("N") + ".png");
        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "缺图检查",
            slides = new object[]
            {
                new { type = "cover", title = "缺图检查" },
                new { type = "image", title = "缺图", variant = "left", path = missing },
            },
        }));

        var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("图片不存在"));
        using var zip = ZipFile.OpenRead(path);
        using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open());
        Assert.Contains("图片不存在", sr.ReadToEnd());
    }

    /// <summary>进度页（横条）：标签、数值、完成度条都要在，且不越界。</summary>
    [Fact]
    public void ProgressPage_RendersLabelsValuesAndBars()
    {
        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "进度检查",
            slides = new object[]
            {
                new { type = "cover", title = "进度检查" },
                new { type = "progress", title = "项目进度", items = new object[]
                    {
                        new { label = "需求确认", value = 100 },
                        new { label = "开发", value = 72 },
                        new { label = "测试", value = 35.5 },
                    } },
            },
        }));

        using var zip = ZipFile.OpenRead(path);
        using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open());
        var xml = sr.ReadToEnd();
        Assert.Contains("需求确认", xml);
        Assert.Contains("100%", xml);
        Assert.Contains("72%", xml);
        Assert.Contains("35.5%", xml);
        Assert.True(ShapesOutsideCanvas(path).Count == 0);
        Assert.Equal(0, doc.RootElement.GetProperty("qa").GetProperty("issueCount").GetInt32());
    }

    /// <summary>
    /// 环形仪表：必须是<b>环</b>（中心镂空）而不是实心饼。
    /// 与饼图“实心扇区”那个坑同源——几何画错时“看着像画了”，只有量像素能发现。
    /// </summary>
    [Fact]
    public void ProgressRing_IsHollowRingNotDisk()
    {
        var (path, _) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "环形检查",
            slides = new object[]
            {
                new { type = "cover", title = "环形检查" },
                new { type = "progress", title = "环形仪表", variant = "ring", items = new object[]
                    {
                        new { label = "覆盖率", value = 86 },
                        new { label = "可用率", value = 62 },
                    } },
            },
        }));

        using var zip = ZipFile.OpenRead(path);
        var media = zip.Entries.Where(e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, media.Count); // 两个环
        foreach (var e in media)
        {
            var ink = PngInkRatio(e);
            Assert.True(ink > 0.05, "环形太稀：" + ink);
            Assert.True(ink < 0.45, "环形看起来是实心饼：" + ink);
        }
    }

    /// <summary>散点图：按 x/y 两个数值轴画点（分类轴折线图表达不了相关性）。</summary>
    [Fact]
    public void ScatterChart_RendersPointsAndTitles()
    {
        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "散点检查",
            slides = new object[]
            {
                new { type = "cover", title = "散点检查" },
                new { type = "chart", title = "投入与收益", chartType = "scatter", xLabel = "投入(人日)", yLabel = "收益",
                    series = new object[]
                    {
                        new { name = "试点", points = new object[] { new[] { 1.0, 2.0 }, new[] { 2.0, 3.5 }, new[] { 3.0, 4.0 }, new[] { 5.0, 7.0 } } },
                        new { name = "对照", points = new object[] { new[] { 1.0, 1.2 }, new[] { 3.0, 2.2 }, new[] { 5.0, 3.1 } } },
                    } },
            },
        }));

        Assert.Empty(doc.RootElement.GetProperty("warnings").EnumerateArray());
        using var zip = ZipFile.OpenRead(path);
        var chart = zip.Entries.First(e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal));
        Assert.True(PngInkRatio(chart) > 0.02, "散点图几乎没画出东西");
    }

    /// <summary>散点图给了错了入参（只有 values）要报可读错误，而不是假装出了一张空图。</summary>
    [Fact]
    public void ScatterChart_WithoutPoints_FailsReadably()
    {
        var json = JsonSerializer.Serialize(new
        {
            title = "散点缺数据",
            slides = new object[]
            {
                new { type = "cover", title = "散点缺数据" },
                new { type = "chart", title = "散点", chartType = "scatter", categories = new[] { "A" },
                    series = new object[] { new { name = "s", values = new[] { 1.0 } } } },
            },
        });
        using var doc = RunRaw(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("散点图缺少数据", doc.RootElement.GetProperty("message").GetString());
    }

    /// <summary>雷达图：多维对比（把 categories 当各维度轴）。</summary>
    [Fact]
    public void RadarChart_RendersAxesAndLabels()
    {
        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "雷达检查",
            slides = new object[]
            {
                new { type = "cover", title = "雷达检查" },
                new { type = "chart", title = "能力雷达", chartType = "radar",
                    categories = new[] { "协作", "记忆", "交付", "安全", "生态" },
                    series = new object[]
                    {
                        new { name = "知聚", values = new[] { 9.0, 8.0, 9.0, 7.0, 8.0 } },
                        new { name = "基座", values = new[] { 6.0, 5.0, 4.0, 7.0, 6.0 } },
                    } },
            },
        }));

        Assert.Empty(doc.RootElement.GetProperty("warnings").EnumerateArray());
        using var zip = ZipFile.OpenRead(path);
        var chart = zip.Entries.First(e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal));
        Assert.True(PngInkRatio(chart) > 0.02, "雷达图几乎没画出东西");
    }

    /// <summary>
    /// 命名字体配对：只换拉丁字面，<c>a:ea</c>（汉字）必须仍然是中文字体。
    /// 否则拿 Georgia 去排汉字会整段掉到 fallback。
    /// </summary>
    [Fact]
    public void FontPair_SetsLatinFacesAndKeepsEastAsianFont()
    {
        var (path, doc) = RenderDeck(MiniDeck("\"theme\":\"forest-eco\",\"fontPair\":\"georgia-calibri\""));

        var fonts = doc.RootElement.GetProperty("fonts");
        Assert.Equal("Georgia", fonts.GetProperty("title").GetString());
        Assert.Equal("Calibri", fonts.GetProperty("body").GetString());
        Assert.Equal("georgia-calibri", fonts.GetProperty("pair").GetString());

        using var zip = ZipFile.OpenRead(path);
        using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open());
        var xml = sr.ReadToEnd();
        Assert.Contains("typeface=\"Georgia\"", xml);            // 标题：拉丁用配对字体
        Assert.Contains("typeface=\"Calibri\"", xml);            // 正文：拉丁用配对字体
        Assert.Contains("typeface=\"微软雅黑\"", xml);            // 东亚：仍是中文字体
        // 汉字不能落在 Georgia 上
        Assert.DoesNotContain("<a:ea typeface=\"Georgia\"", xml);
    }

    /// <summary>显式给了中文字体（fontTitle:"宋体"）时，a:ea 要跟着它，而不是被默认值盖掉。</summary>
    [Fact]
    public void CjkFont_FollowsExplicitChineseFontFace()
    {
        var (path, doc) = RenderDeck(MiniDeck("\"fontTitle\":\"宋体\""));
        Assert.Equal("宋体", doc.RootElement.GetProperty("fonts").GetProperty("cjk").GetString());
        using var zip = ZipFile.OpenRead(path);
        using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open());
        Assert.Contains("<a:ea typeface=\"宋体\"", sr.ReadToEnd());
    }

    /// <summary>
    /// 「标题下强调线」默认不画（设计规范把它列为 AI 生成稿的典型特征），
    /// 传 titleRule:true 才加回来。
    /// </summary>
    [Fact]
    public void TitleRule_OffByDefault_OnWhenRequested()
    {
        string SlideOfThisDeck(string extra)
        {
            var (path, _) = RenderDeck(MiniDeck(extra));
            using var zip = ZipFile.OpenRead(path);
            using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open());
            return sr.ReadToEnd();
        }

        // 那条线是唯一 cx=838200 / cy=45720 的矩形
        bool HasRule(string xml) => xml.Contains("cx=\"838200\"") && xml.Contains("cy=\"45720\"");

        Assert.False(HasRule(SlideOfThisDeck("\"theme\":\"forest-eco\"")), "默认不应画标题下强调线");
        Assert.True(HasRule(SlideOfThisDeck("\"theme\":\"forest-eco\",\"titleRule\":true")), "titleRule:true 时应画出来");
    }

    // ---- 出稿后自检（QA）----

    /// <summary>占位符与“空正文”必须被自检抓到——否则模型会把它当成完成的稿子交出去。</summary>
    [Fact]
    public void Qa_FlagsPlaceholdersAndEmptyBody()
    {
        var (_, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "自检检查",
            slides = new object[]
            {
                new { type = "cover", title = "自检检查" },
                new { type = "content", title = "没写正文的页" },
                new { type = "content", title = "占位符页", bullets = new[] { "TODO: 补充数据" } },
            },
        }));

        var qa = doc.RootElement.GetProperty("qa");
        Assert.True(qa.GetProperty("ok").GetBoolean());
        var kinds = qa.GetProperty("issues").EnumerateArray()
            .Select(i => i.GetProperty("kind").GetString()!).ToList();
        Assert.Contains("emptyBody", kinds);
        Assert.Contains("placeholder", kinds);
    }

    /// <summary>自检通过时不能乱报（正常稿子的 issueCount 必须是 0）。</summary>
    [Fact]
    public void Qa_PassesOnAHealthyDeck()
    {
        var (_, doc) = RenderDeck(FullDeckJson());
        var qa = doc.RootElement.GetProperty("qa");
        Assert.Equal(0, qa.GetProperty("issueCount").GetInt32());
        Assert.Equal(11, qa.GetProperty("slides").GetInt32());
    }

    /// <summary>action=qa：只自检既有文件，不生成新文件。</summary>
    [Fact]
    public void QaAction_InspectsExistingFile()
    {
        var (srcPath, _) = RenderDeck(MiniDeck("\"theme\":\"forest-eco\""));
        using var doc = RunRaw(JsonSerializer.Serialize(new { action = "qa", path = srcPath }));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("qa", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("slides").GetInt32());
        // 外部文件不知道页型 → 不做“只有标题”判定（封面天然只有一句话，误报会淹没真问题）
        var kinds = doc.RootElement.GetProperty("issues").EnumerateArray()
            .Select(i => i.GetProperty("kind").GetString()!).ToList();
        Assert.DoesNotContain("titleOnly", kinds);
    }

    // ---- 原地编辑既有 pptx ----

    /// <summary>
    /// 编辑既有稿：删页 / 重排 / 复制页 / 替换文字 / 追加新页。
    /// 以前只能“读文本”和“套模板重出一份”，用户说“把第 3 页删掉”是做不到的。
    /// </summary>
    [Fact]
    public void Edit_DeleteReorderDuplicateReplaceAndAppend()
    {
        var (src, _) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "待编辑稿",
            slides = new object[]
            {
                new { type = "cover", title = "原始封面" },
                new { type = "content", title = "第二页", bullets = new[] { "待替换文字", "保留这项" } },
                new { type = "content", title = "第三页", bullets = new[] { "第三页内容" } },
                new { type = "content", title = "第四页", bullets = new[] { "第四页内容" } },
                new { type = "end", title = "谢谢" },
            },
        }));
        var outPath = Path.Combine(TempDir(), "edited.pptx");

        using var doc = RunRaw(JsonSerializer.Serialize(new
        {
            action = "edit", path = src, outputPath = outPath,
            ops = new object[]
            {
                new { op = "reorder", order = new[] { 1, 3, 2, 4, 5 } },
                new { op = "replaceText", slides = new[] { 3 }, map = new Dictionary<string, string> { ["待替换文字"] = "已替换文字" } },
                new { op = "duplicate", slide = 1, count = 1 },
                new { op = "delete", slides = new[] { 5 } },
                new { op = "append", slides = new object[] { new { type = "content", title = "新追加的页", bullets = new[] { "由 append 生成" } } } },
            },
        }));

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), doc.RootElement.GetProperty("message").GetString());
        Assert.True(File.Exists(outPath));
        Assert.True(File.Exists(src), "原文件必须还在");
        Assert.True(ShapesOutsideCanvas(outPath).Count == 0);

        // 用 read 把结果读回来核对页序与内容。逐步推演（页号均为“当时”的顺序）：
        //   初始：1 封面 | 2 第二页 | 3 第三页 | 4 第四页 | 5 谢谢
        //   reorder[1,3,2,4,5] → 封面 | 第三页 | 第二页 | 第四页 | 谢谢
        //   duplicate 第1页      → 封面 | 封面副本 | 第三页 | 第二页 | 第四页 | 谢谢
        //   delete [5]           → 封面 | 封面副本 | 第三页 | 第二页 | 谢谢
        //   append               → … | 新追加的页（共 6 页）
        using var back = RunRaw(JsonSerializer.Serialize(new { action = "read", path = outPath }));
        Assert.Equal(6, back.RootElement.GetProperty("slides").GetInt32());
        var perSlide = back.RootElement.GetProperty("slideTexts").EnumerateArray().ToList();
        List<string> TextsOf(int i) => perSlide[i].GetProperty("texts").EnumerateArray()
            .Select(x => x.GetString()!).ToList();

        Assert.Contains("原始封面", TextsOf(0));
        Assert.Contains("原始封面", TextsOf(1));            // duplicate 出来的副本
        Assert.Contains("第三页内容", TextsOf(2));          // reorder 生效：第三页排到了第二页前面
        var fourth = TextsOf(3);
        Assert.Contains("已替换文字", fourth);                 // replaceText 生效
        Assert.DoesNotContain("待替换文字", fourth);
        Assert.Contains("谢谢", TextsOf(4));
        Assert.Contains("由 append 生成", TextsOf(5));        // append 加在末尾
        // 被删掉的页真的没了（不是只解了引用、slideN.xml 还留在包里）
        var all = back.RootElement.GetProperty("text").GetString()!;
        Assert.DoesNotContain("第四页内容", all);
        Assert.Equal(6, ZipFile.OpenRead(outPath).Entries
            .Count(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                     && e.FullName.EndsWith(".xml", StringComparison.Ordinal)));
    }

    /// <summary>编辑输出路径与源文件相同时必须拒绝（不能把用户原件改了）。</summary>
    [Fact]
    public void Edit_RefusesToOverwriteTheSourceFile()
    {
        var (src, _) = RenderDeck(MiniDeck("\"theme\":\"forest-eco\""));
        using var doc = RunRaw(JsonSerializer.Serialize(new
        {
            action = "edit", path = src, outputPath = src,
            ops = new object[] { new { op = "delete", slides = new[] { 2 } } },
        }));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("会覆盖原件", doc.RootElement.GetProperty("message").GetString());
    }

    /// <summary>不能把页删光（一份零页的 pptx 是坏文件）；错误要说清楚。</summary>
    [Fact]
    public void Edit_RefusesToDeleteEverySlide()
    {
        var (src, _) = RenderDeck(MiniDeck("\"theme\":\"forest-eco\""));
        using var doc = RunRaw(JsonSerializer.Serialize(new
        {
            action = "edit", path = src, outputPath = Path.Combine(TempDir(), "o.pptx"),
            ops = new object[] { new { op = "delete", slides = new[] { 1, 2 } } },
        }));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("至少保留一页", doc.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// 含原生图表的页不能复制：图表与内嵌工作簿的克隆会破坏包结构，
    /// 宁可明确报错，也不要产出“需要修复”的文件。
    /// </summary>
    [Fact]
    public void Edit_DuplicatingChartSlide_ReportsClearError()
    {
        var (src, _) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "图表复制",
            slides = new object[]
            {
                new { type = "cover", title = "图表复制" },
                new { type = "chart", title = "原生图表", chartType = "bar-native",
                    categories = new[] { "A", "B" }, series = new object[] { new { name = "s", values = new[] { 1.0, 2.0 } } } },
            },
        }));
        using var doc = RunRaw(JsonSerializer.Serialize(new
        {
            action = "edit", path = src, outputPath = Path.Combine(TempDir(), "o.pptx"),
            ops = new object[] { new { op = "duplicate", slide = 2, count = 1 } },
        }));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("不支持复制含图表页", doc.RootElement.GetProperty("message").GetString());
    }

    /// <summary>分页出稿：页号越界要报可读错误。</summary>
    [Fact]
    public void Edit_OutOfRangeSlideNumber_FailsReadably()
    {
        var (src, _) = RenderDeck(MiniDeck("\"theme\":\"forest-eco\""));
        using var doc = RunRaw(JsonSerializer.Serialize(new
        {
            action = "edit", path = src, outputPath = Path.Combine(TempDir(), "o.pptx"),
            ops = new object[] { new { op = "delete", slides = new[] { 9 } } },
        }));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("页号越界", doc.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// 全部内置图标都要真画出东西。
    ///
    /// <para>
    /// 图标是按名字 <c>switch</c> 画的：名字写错/漏 case 会得到一张<b>全透明</b>的 PNG，
    /// 圆里看起来空空的，而“文件生成成功”这件事完全看不出异常。所以逐个量墨迹占比。
    /// </para>
    /// </summary>
    [Fact]
    public void AllDocumentedIcons_ProduceNonBlankPngs()
    {
        var icons = new[]
        {
            "check", "cross", "arrow", "star", "dot", "warn", "lock", "user", "chart", "clock", "gear", "bulb",
            "money", "target", "rocket", "shield", "layers", "globe", "network", "cloud",
            "database", "mail", "phone", "calendar", "flag", "search", "edit", "file",
            "pie", "link", "eye", "heart", "key", "crown", "map", "cpu",
            "package", "award", "briefcase", "users", "code", "gauge", "filter", "refresh", "download",
        };

        var slides = new List<object> { new { type = "cover", title = "图标全量" } };
        for (var i = 0; i < icons.Length; i += 6)
        {
            var page = icons.Skip(i).Take(6)
                .Select(ic => (object)new { icon = ic, title = ic, text = "说明" }).ToArray();
            slides.Add(new { type = "iconRows", title = "图标 " + (i / 6 + 1), items = page });
        }
        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new { title = "图标全量", theme = "forest-eco", slides }));
        Assert.Empty(doc.RootElement.GetProperty("warnings").EnumerateArray());

        using var zip = ZipFile.OpenRead(path);
        var media = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/media/", StringComparison.Ordinal)
                     && e.FullName.EndsWith(".png", StringComparison.Ordinal)).ToList();
        Assert.Equal(icons.Length, media.Count);   // 每个名字都真画了一张图（没回落到文字）
        foreach (var e in media)
        {
            var ink = PngInkRatio(e);
            Assert.True(ink > 0.02, e.FullName + " 几乎是空白（墨迹 " + ink.ToString("P1") + "）");
        }
    }

    // ====================================================================
    // 自动插图：示意图页型 + 程序化题图
    // ====================================================================

    /// <summary>
    /// 五种示意图各自要画出自己的**图形特征**（预设几何），而不是回落成要点页。
    ///
    /// <para>
    /// 断言到 <c>prstGeom</c> 是因为“标题在不在”拦不住静默回落；
    /// 而金字塔/漏斗的斜边靠 <c>trapezoid</c>、循环靠 <c>triangle</c> 旋转、
    /// 题图靠 <c>parallelogram</c>——这些形状名就是“这个页型真的画了图”的证据。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("pyramid", "trapezoid")]
    [InlineData("funnel", "trapezoid")]
    [InlineData("matrix", "rect")]
    [InlineData("cycle", "triangle")]
    [InlineData("stack", "rect")]
    [InlineData("hero", "parallelogram")]
    public void DiagramPages_DrawTheirOwnShapes(string type, string expectedPrst)
    {
        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "示意图检查",
            theme = "pure-tech-blue",
            slides = new object[]
            {
                new { type = "cover", title = "示意图检查" },
                new
                {
                    type, title = "示意图", center = "闭环", xTitle = "难度", yTitle = "价值",
                    items = new object[]
                    {
                        new { title = "甲", text = "说明甲", detail = "细项甲" },
                        new { title = "乙", text = "说明乙", detail = "细项乙" },
                        new { title = "丙", text = "说明丙", detail = "细项丙" },
                    },
                },
            },
        }));

        Assert.Empty(doc.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Equal(0, doc.RootElement.GetProperty("qa").GetProperty("issueCount").GetInt32());
        Assert.True(ShapesOutsideCanvas(path).Count == 0,
            type + " 有形状画出画布：" + string.Join("; ", ShapesOutsideCanvas(path)));

        using var zip = ZipFile.OpenRead(path);
        using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open());
        Assert.Contains($"prst=\"{expectedPrst}\"", sr.ReadToEnd());
    }

    /// <summary>
    /// 示意图装满上限项数（6 项）时仍不得越界。
    /// 实测踩到：题图最初用旋转矩形做斜带、圆也不限位，形状直接画到画布外（自检报 overflow）。
    /// </summary>
    [Theory]
    [InlineData("pyramid")]
    [InlineData("funnel")]
    [InlineData("cycle")]
    [InlineData("stack")]
    [InlineData("hero")]
    public void DiagramPages_MaxItems_StayInsideCanvas(string type)
    {
        var items = Enumerable.Range(1, 6)
            .Select(i => (object)new { title = "第" + i + "层", text = "说明" + i, detail = "细项" + i }).ToArray();
        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "上限检查",
            slides = new object[]
            {
                new { type = "cover", title = "上限检查" },
                new { type, title = "上限检查", center = "闭环", items },
            },
        }));

        Assert.Equal(0, doc.RootElement.GetProperty("qa").GetProperty("issueCount").GetInt32());
        Assert.True(ShapesOutsideCanvas(path).Count == 0,
            type + " 有形状画出画布：" + string.Join("; ", ShapesOutsideCanvas(path)));
    }

    /// <summary>
    /// 没有真图时不再只画一块纯色（或写“图片不存在”），而是**自动生成题图**：
    /// 页面里应该没有 &lt;p:pic&gt;，但有题图的形状；且如实报告用了自动题图。
    /// </summary>
    [Fact]
    public void MissingImage_FallsBackToGeneratedArt()
    {
        var missing = Path.Combine(Path.GetTempPath(), "缺图-" + Guid.NewGuid().ToString("N") + ".png");
        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "缺图检查",
            theme = "forest-eco",
            slides = new object[]
            {
                new { type = "cover", title = "缺图检查" },
                new { type = "image", title = "配图页", variant = "full", path = missing },
                new { type = "cover", title = "背景图封面", variant = "image", path = missing },
            },
        }));

        var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains(warnings, w => w.Contains("自动生成的题图"));

        using var zip = ZipFile.OpenRead(path);
        string Slide(int n)
        {
            using var sr = new StreamReader(zip.Entries.First(e => e.FullName == $"ppt/slides/slide{n}.xml").Open());
            return sr.ReadToEnd();
        }
        foreach (var n in new[] { 2, 3 })
        {
            var xml = Slide(n);
            Assert.DoesNotContain("<p:pic>", xml);            // 没有真图
            Assert.Contains("prst=\"parallelogram\"", xml);  // 但有题图
            Assert.Contains("<a:alpha", xml);                 // 题图靠透明度做层次
        }
    }

    /// <summary>
    /// 题图要**确定性**：同一个标题每次生成的图必须一模一样。
    /// （用 Random 而不是种子的话，用户每次重导出同一份稿子都会拿到不同的封面。）
    /// </summary>
    [Fact]
    public void GeneratedHeroArt_IsDeterministic()
    {
        string HeroOf()
        {
            var (path, _) = RenderDeck(JsonSerializer.Serialize(new
            {
                title = "确定性检查",
                slides = new object[]
                {
                    new { type = "cover", title = "确定性检查" },
                    new { type = "hero", title = "固定标题", subtitle = "固定副标题" },
                },
            }));
            using var zip = ZipFile.OpenRead(path);
            using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open());
            return sr.ReadToEnd();
        }

        var first = HeroOf();
        var second = HeroOf();
        // 形状 id 从 2 开始递增，两次生成完全一致
        Assert.Equal(first, second);
    }

    /// <summary>内容页可用 layout 直接指定示意图子类型（与 timeline/grid 那套别名一致）。</summary>
    [Theory]
    [InlineData("pyramid", "trapezoid")]
    [InlineData("funnel", "trapezoid")]
    [InlineData("matrix", "rect")]
    [InlineData("cycle", "triangle")]
    [InlineData("stack", "rect")]
    public void ContentLayoutAlias_ReachesDiagramTypes(string layout, string expectedPrst)
    {
        var (path, doc) = RenderDeck(JsonSerializer.Serialize(new
        {
            title = "别名检查",
            slides = new object[]
            {
                new { type = "cover", title = "别名检查" },
                new
                {
                    type = "content", title = "别名页", layout,
                    items = new object[]
                    {
                        new { title = "甲", text = "说明甲" },
                        new { title = "乙", text = "说明乙" },
                        new { title = "丙", text = "说明丙" },
                    },
                },
            },
        }));
        Assert.Equal(0, doc.RootElement.GetProperty("qa").GetProperty("issueCount").GetInt32());
        using var zip = ZipFile.OpenRead(path);
        using var sr = new StreamReader(zip.Entries.First(e => e.FullName == "ppt/slides/slide2.xml").Open());
        Assert.Contains($"prst=\"{expectedPrst}\"", sr.ReadToEnd());
    }

    /// <summary>
    /// 量一张 PNG 的墨迹（不透明像素）占比。只处理我们自己生成的 8bit RGBA / 非隔行 PNG。
    /// 用途：把“画了个空白图标/空心环”这类几何错误变成可断言的数字。
    /// </summary>
    private static double PngInkRatio(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var png = ms.ToArray();
        Assert.Equal(0x89, png[0]);

        int pos = 8, width = 0, height = 0, colorType = 0;
        var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            var len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = pos + 8;
            if (type == "IHDR")
            {
                width = (png[data] << 24) | (png[data + 1] << 16) | (png[data + 2] << 8) | png[data + 3];
                height = (png[data + 4] << 24) | (png[data + 5] << 16) | (png[data + 6] << 8) | png[data + 7];
                colorType = png[data + 9];
                Assert.Equal(0, png[data + 12]); // 非隔行
            }
            else if (type == "IDAT") idat.Write(png, data, len);
            else if (type == "IEND") break;
            pos = data + len + 4;
        }
        Assert.Equal(6, colorType); // 我们生成的都是 RGBA8

        idat.Position = 0;
        using var inflated = new ZLibStream(idat, CompressionMode.Decompress);
        using var rawMs = new MemoryStream();
        inflated.CopyTo(rawMs);
        var raw = rawMs.ToArray();

        var channels = 4;
        var stride = width * channels;
        var prev = new byte[stride];
        var cur = new byte[stride];
        var p = 0;
        var opaque = 0;
        for (var y = 0; y < height; y++)
        {
            var filter = raw[p++];
            Array.Copy(raw, p, cur, 0, stride);
            p += stride;
            for (var i = 0; i < stride; i++)
            {
                int a = i >= channels ? cur[i - channels] : 0;
                int b = prev[i];
                int c = i >= channels ? prev[i - channels] : 0;
                cur[i] = filter switch
                {
                    0 => cur[i],
                    1 => (byte)(cur[i] + a),
                    2 => (byte)(cur[i] + b),
                    3 => (byte)(cur[i] + (a + b) / 2),
                    _ => (byte)(cur[i] + PaethFilter(a, b, c)),
                };
            }
            for (var x = 0; x < width; x++)
                if (cur[x * channels + 3] > 32) opaque++;
            Array.Copy(cur, prev, stride);
        }
        return (double)opaque / (width * height);
    }

    private static int PaethFilter(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ================= 联网配图（Wikimedia Commons） =================

    /// <summary>
    /// 一张真实可解码的小 JPEG（565 字节）。
    ///
    /// <para>
    /// 为何不现场用 ImageSharp 生成：本测试项目没有直接引 ImageSharp（只引了 SixLabors.Fonts），
    /// 而“能够解码的 JPEG 字节”才是这里要验的东西（技能要按魔数判定类型并原样存回 JPEG）。
    /// </para>
    /// </summary>
    private const string TinyJpegHex =
        "ffd8ffe000104a46494600010100000100010000ffdb004300"
        + "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        + "c00011080018002003012200021101031101ffc4001f0000010501010101010100000000000000000102030405060708090a0b"
        + "ffc400b5100002010303020403050504040000017d01020300041105122131410613516107227114328191a1082342b1c11552d1f0"
        + "2433627282090a161718191a25262728292a3435363738393a434445464748494a535455565758595a636465666768696a737475767778"
        + "797a838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9da"
        + "e1e2e3e4e5e6e7e8e9eaf1f2f3f4f5f6f7f8f9fa"
        + "ffc4001f0100030101010101010101010000000000000102030405060708090a0b"
        + "ffc400b51100020102040403040705040400010277000102031104052131061241510761711322328108144291a1b1c109233352f0156272d1"
        + "0a162434e125f11718191a262728292a35363738393a434445464748494a535455565758595a636465666768696a737475767778797a828384"
        + "85868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae2e3e4e5e6e7"
        + "e8e9eaf2f3f4f5f6f7f8f9fa"
        + "ffda000c03010002110311003f00f7fa28a2803fffd9";

    private static byte[] TinyJpegBytes() => Convert.FromHexString(TinyJpegHex);

    /// <summary>
    /// 桩图库：模拟 Wikimedia Commons 的 <c>action=query&amp;generator=search</c> 响应。
    ///
    /// <para>
    /// 为何一定要用本地桩而不是真外网：① 测试不能依赖“这台机器能访问 Wikimedia”——
    /// 实测本环境能访问 example.com / api.nuget.org，但 en.wikipedia.org 与 upload.wikimedia.org
    /// 一律连不上（被网络策略拦）；② 真外网会让用例变慢且不稳定。
    /// </para>
    ///
    /// <para>
    /// 桩数据刻意混入两个“不该被选中”的条目：太小的图（100px）、非自由许可（Non-free fair use），
    /// 用来验证筛选真的生效（只取 1920px 的 CC BY-SA 那张）。
    /// </para>
    /// </summary>
    private sealed class StubPhotoLibrary : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string ApiUrl { get; set; } = "";
        /// <summary>置 true 后桩返回 HTTP 500（验证降级链路）。</summary>
        public bool Fail { get; set; }
        /// <summary>置 n 后，搜索端点前 n 次返回 429（验证限流退避重试）。</summary>
        public int Fail429Times;
        /// <summary>检索次数：验证熔断（失败后不再逐页重试）与缓存（同一关键词只查一次）。</summary>
        public int Searches;
        /// <summary>每个请求故意挂多久（毫秒）：验“取图超时要被剩余预算夹住”，桩必须比预算慢。</summary>
        public int DelayMs;
        /// <summary>收到的请求数（含被客户端超时掐断的）。</summary>
        public int Requests;

        private StubPhotoLibrary(WebApplication app) { _app = app; }

        public static async Task<StubPhotoLibrary> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var stub = new StubPhotoLibrary(app);

            app.Run(async ctx =>
            {
                Interlocked.Increment(ref stub.Requests);
                if (Volatile.Read(ref stub.DelayMs) > 0)
                    await Task.Delay(stub.DelayMs, ctx.RequestAborted).ContinueWith(_ => { });
                var path = ctx.Request.Path.Value ?? "";
                if (path.StartsWith("/photo", StringComparison.Ordinal))
                {
                    ctx.Response.ContentType = "image/jpeg";
                    await ctx.Response.Body.WriteAsync(TinyJpegBytes());
                    return;
                }
                if (path != "/api.php") { ctx.Response.StatusCode = 404; return; }
                Interlocked.Increment(ref stub.Searches);
                if (stub.Fail) { ctx.Response.StatusCode = 500; return; }
                // 限流：前 N 次返回 429（技能应当退避 2 秒重试一次）
                while (true)
                {
                    var left = Volatile.Read(ref stub.Fail429Times);
                    if (left <= 0) break;
                    if (Interlocked.CompareExchange(ref stub.Fail429Times, left - 1, left) == left)
                    {
                        ctx.Response.StatusCode = 429;
                        return;
                    }
                }

                var q = ctx.Request.Query["gsrsearch"].ToString().Replace(" filetype:bitmap", "").Trim();
                var baseUrl = "http://127.0.0.1:" + ctx.Connection.LocalPort;
                var name = "Photo of " + q + ".jpg";
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    query = new
                    {
                        // 顺序很要紧：**坏候选排在前面**（index 1~5），用来证明筛选链真的在干活——
                        // 只要有一道筛选漏了，选中的就会是下面这几张，而不是最后那张正常照片。
                        pages = new Dictionary<string, object>
                        {
                            // ① 书刊扫描件：实测搜 “business people working together” 排第一的就是这类
                            ["11"] = Hit(baseUrl, 1, "File:Book scan 1920 illustration.jpg", "image/jpeg", 2288, 1716,
                                "CC0", "Jane Doe",
                                "Photos uploaded from Flickr by Fæ using a script|Files from Internet Archive Book Images Flickr stream"),
                            // ② 全景图：定框裁切后只剩中间一条（ar=2.5）
                            ["12"] = Hit(baseUrl, 2, "File:City panorama 9000.jpg", "image/jpeg", 9000, 3600,
                                "CC BY-SA 4.0", "Jane Doe", "Panoramas"),
                            // ③ 图标 / Logo：实测 “teamwork” 的头两条就是这类
                            ["13"] = Hit(baseUrl, 3, "File:Teamwork-icon.jpg", "image/jpeg", 1280, 1280,
                                "CC BY-SA 4.0", "Jane Doe", "Icons"),
                            // ④ 尺寸过小：铺满一页会发虚
                            ["14"] = Hit(baseUrl, 4, "File:Too small.png", "image/png", 100, 80,
                                "CC0", "Jane Doe", ""),
                            // ⑤ 非自由许可：尺寸合格也不该选
                            ["15"] = Hit(baseUrl, 5, "File:Not free.jpg", "image/jpeg", 1920, 1080,
                                "Non-free fair use", "Jane Doe", ""),
                            // ⑥ 历史档案照：实测搜 “meeting room” 抳到过 1968 年的白宫会议新闻照
                            ["17"] = Hit(baseUrl, 7, "File:Cabinet Room meeting February 1968.jpg", "image/jpeg", 3000, 2000,
                                "Public domain", "Jane Doe", "1968 in Washington, D.C.|Cabinet meetings"),
                            // ⑦ 合格的那张（写进响应的 title 就是它，所以断言能直接认出“选对了”）
                            ["16"] = Hit(baseUrl, 6, "File:" + name, "image/jpeg", 1920, 1280,
                                "CC BY-SA 4.0", "Jane Doe", "Office buildings"),
                        },
                    },
                }));
            });

            await app.StartAsync();
            stub.ApiUrl = app.Urls.First().TrimEnd('/') + "/api.php";
            return stub;
        }

        /// <summary>造一条与 Wikimedia Commons <c>imageinfo</c> 同构的命中（字段名刻意保持一致）。</summary>
        private static object Hit(string baseUrl, int index, string title, string mime, int w, int h,
            string license, string artist, string categories)
            => new
            {
                pageid = 100 + index,
                index,
                title,
                imageinfo = new object[]
                {
                    new
                    {
                        mime,
                        width = w,
                        height = h,
                        thumburl = baseUrl + "/photo.jpg",
                        url = baseUrl + "/photo.jpg",
                        descriptionurl = "https://commons.wikimedia.org/wiki/File:"
                                         + Uri.EscapeDataString(title.Substring(5).Replace(' ', '_')),
                        extmetadata = new Dictionary<string, object>
                        {
                            ["LicenseShortName"] = new { value = license },
                            // 作者字段刻意带 HTML：署名时要剥成纯文本，不能把 <a href> 写进幻灯片
                            ["Artist"] = new { value = "<a href=\"//commons.wikimedia.org/wiki/User:Jane\">" + artist + "</a>" },
                            ["Categories"] = new { value = categories },
                        },
                    },
                },
            };

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    private static string PhotoDeckJson(string apiUrl, int contentPages = 1)
    {
        var slides = new List<object>
        {
            new { type = "cover", variant = "split", title = "配图验证",
                  subtitle = "每页一张符合内容的照片", imageQuery = "team meeting" },
            new { type = "section", variant = "full", title = "一、现状", imageQuery = "team meeting" },
        };
        for (var i = 0; i < contentPages; i++)
        {
            slides.Add(new
            {
                type = "content",
                title = contentPages == 1 ? "协作方式" : "主题 " + (i + 1).ToString("00"),
                bullets = new[] { "把单聊式 AI 升级为多角色协作空间", "每个岗位有自己的记忆特征" },
                imageQuery = "topic number " + (i + 1).ToString("00") + " landscape",
            });
        }
        slides.Add(new { type = "content", title = "纯文字页（无配图）", bullets = new[] { "这一页不该有图", "用来对照" } });
        slides.Add(new { type = "end", title = "谢谢" });
        return JsonSerializer.Serialize(new
        {
            title = "配图验证",
            theme = "tech",
            imageSearchApi = apiUrl,
            slides,
        });
    }

    private static List<string> SlideXmls(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
                     && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(e => int.Parse(new string(e.FullName.Where(char.IsDigit).ToArray())))
            .Select(e =>
            {
                using var r = new StreamReader(e.Open());
                return r.ReadToEnd();
            }).ToList();
    }

    /// <summary>
    /// 用 imageQuery 把 Commons 照片嵌进稿子：图真的进了 pptx、每张都有署名、
    /// content 页自动变成“文左图右”、没写 imageQuery 的页不受影响。
    /// </summary>
    [Fact]
    public async Task PhotoQuery_EmbedsPhotoAndCreditsPage()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        await using var lib = await StubPhotoLibrary.StartAsync();
        try
        {
            var result = NewHost().Run(SkillSource(), PhotoDeckJson(lib.ApiUrl), CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var root = doc.RootElement;
            var path = root.GetProperty("produce_file").GetProperty("path").GetString()!;

            // images[]：署名依据必须回传（调用方要靠它判断“这稿用了外图”）。
            // 2 张：封面与分隔页共用 “team meeting”，要点页自己一个关键词 —— 正好也验证了“逐页匹配”。
            var images = root.GetProperty("images");
            Assert.Equal(2, images.GetArrayLength());
            Assert.All(images.EnumerateArray(), im =>
            {
                Assert.Equal("Jane Doe", im.GetProperty("author").GetString());
                Assert.Equal("CC BY-SA 4.0", im.GetProperty("license").GetString());
                Assert.Contains("commons.wikimedia.org", im.GetProperty("page").GetString());
            });
            var queries = images.EnumerateArray().Select(im => im.GetProperty("query").GetString()).ToList();
            Assert.Contains("team meeting", queries);
            Assert.Contains("topic number 01 landscape", queries);
            Assert.Empty(root.GetProperty("warnings").EnumerateArray());

            // 页数 = 封面 + 分隔 + 1 要点页 + 1 纯文字页 + 结束页 + 图片来源页
            var slides = SlideXmls(path);
            Assert.Equal(6, slides.Count);
            var credit = slides[^1];
            Assert.Contains("图片来源", credit);
            Assert.Contains("Jane Doe", credit);            // 作者（且 HTML 标签已剥掉）
            Assert.DoesNotContain("<a href", credit);
            Assert.Contains("CC BY-SA 4.0", credit);
            Assert.Contains("commons.wikimedia.org", credit);
            Assert.Contains("Photo of team meeting.jpg", credit);
            Assert.Contains("Photo of topic number 01 landscape.jpg", credit);
            // 筛选链真的在干活：桩数据把 5 张“不该选”的排在前面（index 1~5），
            // 任何一道筛选漏了，下面这些就会被写进署名页
            foreach (var bad in new[] { "Book scan 1920 illustration", "City panorama 9000",
                                        "Teamwork-icon", "Too small.png", "Not free.jpg",
                                        "Cabinet Room meeting February 1968" })
                Assert.DoesNotContain(bad, credit);

            // 照片真的嵌进去了：媒体部件是 JPEG，且内容类型表里声明了 jpeg
            using (var zip = ZipFile.OpenRead(path))
            {
                Assert.Contains(zip.Entries, e => e.FullName == "ppt/media/image.jpg");
                var ct = zip.Entries.First(e => e.FullName == "[Content_Types].xml");
                using var r = new StreamReader(ct.Open());
                Assert.Contains("jpeg", r.ReadToEnd(), StringComparison.OrdinalIgnoreCase);
            }

            // 封面 / 分隔 / 带 imageQuery 的要点页：各一张图
            Assert.Equal(1, CountBlips(slides[0]));
            Assert.Equal(1, CountBlips(slides[1]));
            Assert.Equal(1, CountBlips(slides[2]));
            Assert.Contains("协作方式", slides[2]);         // 图有了，文字也没丢（文左图右）
            // 没写 imageQuery 的页不受影响
            Assert.Equal(0, CountBlips(slides[3]));
            Assert.Contains("这一页不该有图", slides[3]);
            Assert.Equal(0, CountBlips(slides[4]));

            // 同一关键词只查一次（封面与分隔页共用）
            Assert.Equal(2, lib.Searches);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>图库不可用（HTTP 500）时：降级为题图 + warnings，不崩且不追加署名页。</summary>
    [Fact]
    public async Task PhotoQuery_WhenLibraryFails_DegradesWithoutBreaking()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        await using var lib = await StubPhotoLibrary.StartAsync();
        lib.Fail = true;
        try
        {
            var result = NewHost().Run(SkillSource(), PhotoDeckJson(lib.ApiUrl), CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var root = doc.RootElement;
            var path = root.GetProperty("produce_file").GetProperty("path").GetString()!;

            Assert.Empty(root.GetProperty("images").EnumerateArray());
            var warnings = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
            Assert.Contains(warnings, w => w.Contains("配图检索未成功"));
            // 降级也要说清楚为什么，别让用户对着题图猜
            Assert.Contains(warnings, w => w.Contains("HTTP 500"));
            // 没拿到图 → 不追加「图片来源」页（不能凭空署名）
            Assert.Equal(5, SlideXmls(path).Count);
            // 熔断/缓存：3 处 imageQuery（两个关键词）→ 只查 2 次，不逐页重试
            Assert.Equal(2, lib.Searches);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>AGUI_PHOTO_API 环境变量（离线部署把端点指向镜像/代理的入口）。</summary>
    [Fact]
    public async Task PhotoQuery_UsesEnvEndpointWhenInputOmitsIt()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        await using var lib = await StubPhotoLibrary.StartAsync();
        Environment.SetEnvironmentVariable("AGUI_PHOTO_API", lib.ApiUrl);
        try
        {
            var deck = JsonSerializer.Serialize(new
            {
                title = "环境变量端点",
                slides = new object[]
                {
                    new { type = "cover", variant = "split", title = "环境变量端点", imageQuery = "office teamwork" },
                    new { type = "end", title = "谢谢" },
                },
            });
            var result = NewHost().Run(SkillSource(), deck, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            Assert.Equal(1, doc.RootElement.GetProperty("images").GetArrayLength());
            Assert.Equal(1, lib.Searches);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGUI_PHOTO_API", null);
            Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null);
        }
    }

    /// <summary>
    /// 署名页的自动分页：照片多到一页放不下时必须拆页，而且**一条都不能漏**
    /// （漏掉就等于没有署名）。这里刻意用 12 张・12 个不同关键词。
    /// </summary>
    [Fact]
    public async Task PhotoQuery_ManyPhotos_CreditsPaginateWithoutLosingEntries()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        await using var lib = await StubPhotoLibrary.StartAsync();
        try
        {
            var result = NewHost().Run(SkillSource(), PhotoDeckJson(lib.ApiUrl, contentPages: 12), CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
            // 12 个要点页各一个关键词 + 封面/分隔页共用的 “team meeting”
            Assert.Equal(13, doc.RootElement.GetProperty("images").GetArrayLength());

            var slides = SlideXmls(path);
            var credits = slides.Where(s => s.Contains("图片来源", StringComparison.Ordinal)).ToList();
            Assert.True(credits.Count >= 2, "12 张照片的署名页应当拆页，实际 " + credits.Count + " 页");
            var all = string.Join("\n", slides);
            Assert.Contains("Photo of team meeting.jpg", all);
            for (var i = 1; i <= 12; i++)
                Assert.Contains("Photo of topic number " + i.ToString("00") + " landscape.jpg", all);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>
    /// 限流（HTTP 429）退避重试：Commons 对密集请求会限流（探测真实 API 时实测触发过），
    /// 一次退避重试就能救回来，不应该把这一页白白降级成题图。
    /// </summary>
    [Fact]
    public async Task PhotoQuery_RetriesOnceOnRateLimit()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        await using var lib = await StubPhotoLibrary.StartAsync();
        lib.Fail429Times = 1;   // 第一次搜索返回 429
        try
        {
            var result = NewHost().Run(SkillSource(), PhotoDeckJson(lib.ApiUrl), CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            Assert.Equal(2, doc.RootElement.GetProperty("images").GetArrayLength());
            Assert.Empty(doc.RootElement.GetProperty("warnings").EnumerateArray());
            // 2 个关键词：其中一个被限流过一次 → 共 3 次搜索请求（首次 429 + 重试 1 次）
            Assert.Equal(3, lib.Searches);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    private static int CountBlips(string slideXml)
        => slideXml.Split("<a:blip", StringSplitOptions.None).Length - 1;

    // ===== 图库兜底：本页文字要切短片段 =====

    /// <summary>
    /// 桩：模拟平台内部图库检索（技能经回环自令牌调 <c>/ag-ui/images/search</c>）。
    /// **只对完全等于配置片段的查询给命中** —— 于是“整段页文字”必然落空，
    /// 只有把本页文字切成短片段才能配上图（这正是真实故障的形态）。
    /// 其余路径（Wikimedia 那条）一律空结果，避免测试出网。
    /// </summary>
    private sealed class StubImageLibrary : IAsyncDisposable
    {
        private readonly WebApplication _app;

        /// <summary>命中片段（如人名）：查询必须与它**完全相等**才命中。</summary>
        public string HitFragment { get; set; } = "";
        /// <summary>收到的查询（按顺序），用来断言“到底试了哪些片段”。</summary>
        public List<string> Queries { get; } = new();
        public string BaseUrl { get; private set; } = "";
        public string HitPath { get; }

        private StubImageLibrary(WebApplication app, string hitPath) { _app = app; HitPath = hitPath; }

        public static async Task<StubImageLibrary> StartAsync(string hitPath)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var stub = new StubImageLibrary(app, hitPath);
            app.Run(async ctx =>
            {
                if ((ctx.Request.Path.Value ?? "") == "/ag-ui/images/search")
                {
                    using var reader = new StreamReader(ctx.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    var query = "";
                    try
                    {
                        using var d = JsonDocument.Parse(body);
                        query = d.RootElement.GetProperty("query").GetString() ?? "";
                    }
                    catch { /* 解析不了就当空查询 */ }
                    lock (stub.Queries) stub.Queries.Add(query);
                    ctx.Response.ContentType = "application/json";
                    if (string.Equals(query, stub.HitFragment, StringComparison.Ordinal))
                        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                        {
                            query,
                            count = 1,
                            images = new object[]
                            {
                                new
                                {
                                    assetId = "asset_stub", libId = "img_stub", libName = "公司人员生活照片",
                                    fileName = Path.GetFileName(stub.HitPath), caption = stub.HitFragment,
                                    contentType = "image/jpeg", score = 0.76, width = 800, height = 600,
                                    path = stub.HitPath, url = "/ag-ui/image-libs/img_stub/assets/asset_stub/raw",
                                },
                            },
                        }));
                    else
                        await ctx.Response.WriteAsync(
                            JsonSerializer.Serialize(new { query, count = 0, images = Array.Empty<object>() }));
                    return;
                }
                // Wikimedia 那条路：空结果（正常降级），保证用例不出网
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"query\":{}}");
            });
            await app.StartAsync();
            stub.BaseUrl = app.Urls.First();
            return stub;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    /// <summary>
    /// <b>本页文字兜底必须切成短片段逐个试</b>，不能把整段（上限 120 字）当成一个查询。
    ///
    /// <para>
    /// 钉住一个真实故障：图库里明明有本人照，ppt 却没配上（用户报“配图不正确”）。
    /// 实测根源是<b>整段被摊薄</b>：关键词“刘佳俊”单查命中本人照 0.76，
    /// 而含该名字的 47 字整段只有 0.5862 —— 低于“能不能用”的 0.60，于是落空、降级成网图/题图。
    /// </para>
    ///
    /// <para>
    /// 用例设计：桩<b>只对与人名完全相等的查询</b>给命中，所以“拿整段去查”必然空手 ——
    /// 修复前这条会失败，修复后才过。
    /// </para>
    /// </summary>
    [Fact]
    public async Task PageTextFallback_TriesShortFragments_SoTheLibraryPhotoIsFound()
    {
        var outDir = TempDir();
        var imagePath = Path.Combine(outDir, "1刘佳俊.jpg");
        File.WriteAllBytes(imagePath, TinyJpegBytes());
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        await using var lib = await StubImageLibrary.StartAsync(imagePath);
        lib.HitFragment = "刘佳俊";
        // 技能调图库走回环自令牌（AGUI_SELF_BASE/TOKEN）；网络那条用 imageSearchApi 指向同一个桩，不出网
        Environment.SetEnvironmentVariable("AGUI_SELF_BASE", lib.BaseUrl);
        Environment.SetEnvironmentVariable("AGUI_SELF_TOKEN", "stub-token");
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "配图兜底",
                imageScopeId = "handle-stub",
                imageSearchApi = lib.BaseUrl + "/api.php",
                slides = new object[]
                {
                    new
                    {
                        type = "image", title = "高效习惯优秀进步奖",
                        bullets = new[] { "刘佳俊：把快而稳做成可复制的日常习惯，带动了整个团队的节奏" },
                        imageQuery = "员工 颁奖 舞台",   // 桩对这个关键词不命中
                    },
                },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);

            // 关键断言：配上了图，而且是**图库**那张本人照
            var images = doc.RootElement.GetProperty("images");
            Assert.Equal(1, images.GetArrayLength());
            var img = images[0];
            Assert.Equal("library", img.GetProperty("source").GetString());
            Assert.Equal("1刘佳俊.jpg", img.GetProperty("title").GetString());

            // 并且确实是用**短片段**去查的，而且短片段排在整段之前（整段只当最后的兜底候选，
            // 所以整段查询在桩上必然空手 —— 配上图只能是碎片的功劳）
            List<string> queries;
            lock (lib.Queries) queries = lib.Queries.ToList();
            var fragmentAt = queries.IndexOf("刘佳俊");
            var blobAt = queries.FindIndex(q => q.Contains('：', StringComparison.Ordinal));
            Assert.True(fragmentAt >= 0, "应当用短片段“刘佳俊”查过图库，实际：" + string.Join(" / ", queries));
            Assert.True(blobAt < 0 || fragmentAt < blobAt, "短片段应排在整段之前，实际：" + string.Join(" / ", queries));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null);
            Environment.SetEnvironmentVariable("AGUI_SELF_BASE", null);
            Environment.SetEnvironmentVariable("AGUI_SELF_TOKEN", null);
        }
    }

    // ===== 取图预算：每次网络调用都要夹到剩余预算内 =====

    /// <summary>
    /// <b>取图不能把整份稿子的执行预算吃光</b>。
    ///
    /// <para>
    /// 实测（容器里跑真流程）：<b>4 张配图 45 秒、8 张正好撞在一次执行 60 秒的硬预算上</b> ——
    /// 而超时的后果是<b>整份稿子都没了</b>，用户看到的就是“生成超时了，我把页数收敛后重出一版”。
    /// 根因：预算只在“每次取图前”查一次，而单次取图内部可能包含库检索 10 秒 + 检索 8 秒 +
    /// 逐个候选下载 12 秒 × N，合计能越过预算好几倍。
    /// </para>
    ///
    /// <para>
    /// 用例：桩故意每次都挂 30 秒，预算压到 2 秒 —— 整个技能必须几秒内收手（并如实报预算用尽），
    /// 而不是一直等到 30 秒。</para>
    /// </summary>
    [Fact]
    public async Task PhotoBudget_CapsEachNetworkCall_SoTheDeckStillCompletes()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        Environment.SetEnvironmentVariable("AGUI_PHOTO_BUDGET_SEC", "2");
        await using var lib = await StubPhotoLibrary.StartAsync();
        lib.DelayMs = 30_000;
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "预算夹紧",
                imageSearchApi = lib.ApiUrl,
                slides = new object[]
                {
                    new { type = "image", title = "场景一", imageQuery = "slow one" },
                    new { type = "image", title = "场景二", imageQuery = "slow two" },
                    new { type = "image", title = "场景三", imageQuery = "slow three" },
                },
            });
            var host = NewHost();
            // 先热一下：Roslyn 编译 + NuGet 还原也在 Run 里，头一次会花十几秒，
            // 不预热的话计时被编译时间污染（实测第一次跑就因此失败）。热身稿不带 imageQuery，不会联网。
            host.Run(SkillSource(), JsonSerializer.Serialize(new
            {
                title = "warmup",
                slides = new object[] { new { type = "content", title = "热身", bullets = new[] { "x" } } },
            }), CancellationToken.None);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = host.Run(SkillSource(), json, CancellationToken.None);
            sw.Stop();
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
                $"取图预算 2 秒时不该跑这么久（实际 {sw.Elapsed.TotalSeconds:0.0}s，桩每条挂 30s）");
            Assert.Contains("时间预算", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null);
            Environment.SetEnvironmentVariable("AGUI_PHOTO_BUDGET_SEC", null);
        }
    }

    // ===== 动画（AnimationML）与页间切换 =====

    /// <summary>一份带动画的稿子：飞入 / 逐段 / 退出，并带顶层与页级切换。</summary>
    private static string AnimatedDeckJson() => JsonSerializer.Serialize(new
    {
        title = "动画能力",
        transition = new { preset = "fade", duration = 0.4 },
        slides = new object[]
        {
            new
            {
                type = "cover", title = "封面飞入", subtitle = "副标题",
                animate = new { preset = "flyIn", direction = "bottom", duration = 0.75 },
            },
            new
            {
                type = "content", title = "逐条出现", bullets = new[] { "第一点", "第二点", "第三点" },
                animate = new { preset = "fade", byParagraph = true },
            },
            new
            {
                type = "end", title = "结束页", subtitle = "谢谢",
                transition = new { preset = "push", direction = "left" },
                animate = "fadeOut",
            },
        },
    });

    private static List<SlidePart> OrderedSlideParts(PresentationDocument doc)
        => doc.PresentationPart!.Presentation.SlideIdList!.Elements<SlideId>()
            .Select(id => (SlidePart)doc.PresentationPart.GetPartById(id.RelationshipId!.Value!))
            .ToList();

    private static List<string> SchemaErrors(string path)
    {
        using var pres = PresentationDocument.Open(path, false);
        return new OpenXmlValidator().Validate(pres)
            .Where(e => e.ErrorType == ValidationErrorType.Schema)
            .Select(e => $"{e.Description} @ {e.Path?.XPath}")
            .Take(20)
            .ToList();
    }

    /// <summary>
    /// 动画必须是<b>真写进 XML 的、且指向真实存在的形状</b>：
    /// 悬挂的 spid 会被 PowerPoint 判「需要修复」——这正是本能力最大的风险点。
    /// </summary>
    [Fact]
    public void Animations_TargetOnlyExistingShapes_AndPassSchemaValidation()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var result = NewHost().Run(SkillSource(), AnimatedDeckJson(), CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

            // 回显：3 页有动画、3 页有切换
            var anim = doc.RootElement.GetProperty("animations");
            Assert.Equal(3, anim.GetProperty("pages").GetInt32());
            Assert.Equal(3, anim.GetProperty("transitions").GetInt32());
            Assert.True(anim.GetProperty("effects").GetInt32() >= 3, result);

            Assert.Empty(SchemaErrors(path));

            using var pres = PresentationDocument.Open(path, false);
            var slides = OrderedSlideParts(pres);
            Assert.Equal(3, slides.Count);
            foreach (var sp in slides)
            {
                var timing = sp.Slide.Descendants<Timing>().FirstOrDefault();
                if (timing is null) continue;
                var shapeIds = sp.Slide.Descendants<NonVisualDrawingProperties>()
                    .Select(x => x.Id?.Value).Where(v => v.HasValue).Select(v => v!.Value).ToHashSet();
                foreach (var tgt in timing.Descendants<ShapeTarget>())
                    Assert.True(uint.TryParse(tgt.ShapeId?.Value, out var sid) && shapeIds.Contains(sid),
                        $"动画指向不存在的形状 id={tgt.ShapeId?.Value}");
            }

            // 第 1 页：飞入（presetID=2 + 位移动画）；换页淡入
            var s1 = slides[0].Slide;
            Assert.Contains(s1.Descendants<CommonTimeNode>(), t => t.PresetId?.Value == 2 && t.PresetClass?.Value == TimeNodePresetClassValues.Entrance);
            Assert.Contains(s1.Descendants<Animate>(), a => a.Descendants<AttributeName>().Any(n => n.Text == "ppt_y"));
            Assert.NotNull(s1.Descendants<FadeTransition>().FirstOrDefault());

            // 第 2 页：逐段——标题 1 个效果 + 正文 3 段各一个（共 4 个），并声明 build="p"
            var s2 = slides[1].Slide;
            var effects = s2.Descendants<CommonTimeNode>().Count(t => t.NodeType is not null && t.PresetClass is not null);
            Assert.Equal(4, effects);
            Assert.Contains(s2.Descendants<BuildParagraph>(), b => b.Build?.Value == ParagraphBuildValues.Paragraph);

            // 第 3 页：退出（presetClass=exit）+ 置隐藏；页级切换覆盖顶层
            var s3Xml = slides[2].Slide.OuterXml;
            Assert.Contains("presetClass=\"exit\"", s3Xml);
            Assert.Contains("val=\"hidden\"", s3Xml);
            Assert.NotNull(slides[2].Slide.Descendants<PushTransition>().FirstOrDefault());
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>
    /// <b>不写动画就一个都不加</b>：这是兼容性承诺（既有稿子/既有测试依赖“输出与从前一致”），
    /// 也是“不要自作主张给用户加动效”的产品判断。
    /// </summary>
    [Fact]
    public void WithoutAnimationRequest_NoTimingAndNoTransitionIsWritten()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var result = NewHost().Run(SkillSource(), FullDeckJson(), CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

            var anim = doc.RootElement.GetProperty("animations");
            Assert.Equal(0, anim.GetProperty("pages").GetInt32());
            Assert.Equal(0, anim.GetProperty("effects").GetInt32());
            Assert.Equal(0, anim.GetProperty("transitions").GetInt32());

            using var pres = PresentationDocument.Open(path, false);
            foreach (var sp in OrderedSlideParts(pres))
            {
                Assert.Null(sp.Slide.Descendants<Timing>().FirstOrDefault());
                Assert.Null(sp.Slide.Descendants<Transition>().FirstOrDefault());
            }
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>
    /// 预设名/切换名写错必须<b>报错</b>，不能静默回落成“没有动画”：
    /// 静默回落会变成“用户要了动画、文件里没有，而返回值还写着成功”。
    /// </summary>
    [Fact]
    public void UnknownAnimationOrTransitionPreset_FailsLoudly()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var bad = JsonSerializer.Serialize(new
            {
                title = "预设写错",
                slides = new object[]
                {
                    new { type = "content", title = "要点", bullets = new[] { "甲" }, animate = "fadeAwayPlease" },
                },
            });
            var r1 = NewHost().Run(SkillSource(), bad, CancellationToken.None);
            Assert.Contains("不支持的动画预设", r1);

            var badTrans = JsonSerializer.Serialize(new
            {
                title = "切换写错",
                slides = new object[]
                {
                    new { type = "content", title = "要点", bullets = new[] { "甲" }, transition = "swirl" },
                },
            });
            var r2 = NewHost().Run(SkillSource(), badTrans, CancellationToken.None);
            Assert.Contains("不支持的页间切换", r2);
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>页级 <c>animate:false</c> 关掉顶层默认——顶层开了全稿动画时，个别页要能保持静态。</summary>
    [Fact]
    public void PageLevelFalse_OptsOutOfGlobalAnimationDefault()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                title = "页级关闭",
                animate = "fade",
                slides = new object[]
                {
                    new { type = "content", title = "要动画", bullets = new[] { "甲" } },
                    new { type = "content", title = "不要动画", bullets = new[] { "乙" }, animate = false },
                },
            });
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            using var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
            Assert.Equal(1, doc.RootElement.GetProperty("animations").GetProperty("pages").GetInt32());

            using var pres = PresentationDocument.Open(path, false);
            var slides = OrderedSlideParts(pres);
            Assert.NotNull(slides[0].Slide.Descendants<Timing>().FirstOrDefault());
            Assert.Null(slides[1].Slide.Descendants<Timing>().FirstOrDefault());
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }

    /// <summary>
    /// 既有稿也能加动画（action:edit + op:animate）：这是用户手里已经出稿的那些稿子的主要通道。
    /// 只动指定页，且重复执行不叠加（每次都先清掉旧的 p:timing）。
    /// </summary>
    [Fact]
    public void Edit_AddsAnimationToExistingDeck_OnSelectedSlidesOnly()
    {
        var outDir = TempDir();
        Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", outDir);
        try
        {
            var host = NewHost();
            var built = host.Run(SkillSource(), JsonSerializer.Serialize(new
            {
                title = "既有稿",
                slides = new object[]
                {
                    new { type = "cover", title = "封面", subtitle = "副标题" },
                    new { type = "content", title = "要点", bullets = new[] { "甲", "乙" } },
                },
            }), CancellationToken.None);
            using var bd = JsonDocument.Parse(built);
            Assert.True(bd.RootElement.GetProperty("ok").GetBoolean(), built);
            var src = bd.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
            var outPath = Path.Combine(outDir, "既有稿_带动画.pptx");

            var edited = host.Run(SkillSource(), JsonSerializer.Serialize(new
            {
                action = "edit",
                path = src,
                outputPath = outPath,
                ops = new object[]
                {
                    new { op = "animate", slides = new[] { 1 }, animate = new { preset = "flyIn", direction = "bottom" } },
                    new { op = "transition", slides = new[] { 1, 2 }, transition = new { preset = "wipe", direction = "right" } },
                },
            }), CancellationToken.None);
            using var ed = JsonDocument.Parse(edited);
            Assert.True(ed.RootElement.GetProperty("ok").GetBoolean(), edited);
            Assert.Equal(1, ed.RootElement.GetProperty("animations").GetProperty("pages").GetInt32());
            Assert.Equal(2, ed.RootElement.GetProperty("animations").GetProperty("transitions").GetInt32());
            Assert.Empty(SchemaErrors(outPath));

            using var pres = PresentationDocument.Open(outPath, false);
            var slides = OrderedSlideParts(pres);
            Assert.NotNull(slides[0].Slide.Descendants<Timing>().FirstOrDefault());
            Assert.Null(slides[1].Slide.Descendants<Timing>().FirstOrDefault());
            Assert.NotNull(slides[1].Slide.Descendants<WipeTransition>().FirstOrDefault());

            // 原件未被改动（edit 的铁律）
            using var srcPres = PresentationDocument.Open(src, false);
            Assert.Null(OrderedSlideParts(srcPres)[0].Slide.Descendants<Timing>().FirstOrDefault());

            // 再跑一次同一条编辑：不叠加（仍然只有一个 p:timing）
            var again = host.Run(SkillSource(), JsonSerializer.Serialize(new
            {
                action = "edit",
                path = outPath,
                outputPath = Path.Combine(outDir, "既有稿_带动画2.pptx"),
                ops = new object[]
                {
                    new { op = "animate", slides = new[] { 1 }, animate = new { preset = "flyIn", direction = "bottom" } },
                },
            }), CancellationToken.None);
            using var ad = JsonDocument.Parse(again);
            var path2 = ad.RootElement.GetProperty("path").GetString()!;
            using var pres2 = PresentationDocument.Open(path2, false);
            Assert.Single(OrderedSlideParts(pres2)[0].Slide.Descendants<Timing>());
        }
        finally { Environment.SetEnvironmentVariable("AGUI_PPTX_OUT", null); }
    }
}
