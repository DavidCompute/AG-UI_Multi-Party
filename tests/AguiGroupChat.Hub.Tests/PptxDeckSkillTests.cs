using System.IO.Compression;
using System.Text.Json;
using AguiGroupChat.Agents.BuiltinSkills;
using AguiGroupChat.Agents.Tools;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
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

        // 正文 4.5（AA 标准正文），标题/副标题/强调线 3.0（大字号/非文本元素的 AA 阈值）
        Assert.True(Contrast(C("text"), C("bg")) >= 4.5,
            $"{palette}：正文色 {C("text")} 在底色 {C("bg")} 上对比度只有 {Contrast(C("text"), C("bg")):F2}");
        Assert.True(Contrast(C("primary"), C("bg")) >= 4.5,
            $"{palette}：主色 {C("primary")} 在底色 {C("bg")} 上对比度只有 {Contrast(C("primary"), C("bg")):F2}");
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

        // 强调色必须与主色“看得出来不是同一个颜色”：色相拉开 或 明暗拉开。
        // 实测踩到：forest-eco 选出 3A5A40，与主色 344E41 色相差 4°、对比度 1.11，强调线等于白画。
        var apContrast = Contrast(C("accent"), C("primary"));
        var apHue = HueDistance(Hue(C("accent")), Hue(C("primary")));
        Assert.True(apHue >= 30 || apContrast >= 2.0,
            $"{palette}：强调色 {C("accent")} 与主色 {C("primary")} 色相差 {apHue:F0}°、对比度 {apContrast:F2}，基本糊在一起");
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
}
