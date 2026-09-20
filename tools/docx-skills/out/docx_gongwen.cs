#r "nuget: DocumentFormat.OpenXml, 3.2.0"
#r "nuget: SixLabors.ImageSharp, 2.1.5"
#r "nuget: SixLabors.ImageSharp.Drawing, 1.0.0"
// 【必须显式钉到 1.0.1，不要删】图表文字用 ImageSharp 渲染，而 ImageSharp 2.1.5 对
// SixLabors.Fonts 的依赖是 “>= 1.0.0”，NuGet 解析器取最低满足版 → 会落到 1.0.0。
// 而 1.0.0 的 shaping 会对 CJK 字体错误地套用竖排(vert)字形替换：破折号“—”被画成竖线、
// “（）「」【】《》”被旋转 90°（实测：同一字体用 FreeType/PIL 渲染是横排正确的，
// 所以不是字体问题）。升到 1.0.1 即修复（仍为 Apache-2.0）。
#r "nuget: SixLabors.Fonts, 1.0.1"

// ============================================================================
// docx_gongwen —— 公文（党政机关公文格式，参照 GB/T 9704-2012）
//
// 【何时使用】生成通知、通报、请示、批复、报告等公文正文时使用。三号仿宋正文、黑体层次标题、22pt 小标宋大标题、固定行距。
// 【可用内容块】heading / paragraph / numbered / bullets / table / image(path|imageQuery) / chart / toc / pageBreak
//
// 入口：public static string Run(string input) -> JSON
//   input = {
//     "title": "标题", "subtitle": "副标题(可选)", "author": "单位/作者(可选)",
//     "date": "日期(可选)", "outputPath": "D:\\out\\x.docx(可选)",
//     "sections": [
//       { "heading": "一、小节", "level": 1 },
//       { "paragraph": "正文段落" },
//       { "numbered": ["其一", "其二"] },
//       { "bullets": ["要点一", "要点二"] },
//       { "quote": "引用/强调文字" },
//       { "table": { "headers": ["列1","列2"], "rows": [["a","b"]] } },
//       { "image": { "path": "/data/a.png", "widthCm": 14, "caption": "图 1" } },
//       { "image": { "imageQuery": "现代化机房 服务器机柜", "caption": "图 2" } },
//       { "pageBreak": true }
//     ]
//   }
//   返回 = { ok, scene, path, blocks, chartFont, chartFontCjk, warnings?, message }
//
// 【配图（image）】
//   给 path = 直接用服务器上的本地图片文件（不存在会报错）。
//   给 imageQuery = 按关键词从平台「图库」语义检索一张图（用户自己上传的图片，不依赖外网）。
//     平台调用本技能时会注入检索范围句柄（root.imageScopeId），技能拿它回调平台换取
//     服务器本地路径；库内没有匹配、或当前没有可用图库时，**该图被跳过**并在返回的
//     warnings 里说明原因 —— 配图失败不会让整篇稿子出不来。
//
// 【注意】
//   1) 平台预置 using 不含 System.IO —— 用到 Path/Directory/File 必须自行 using System.IO;（已含）。
//   2) #r 的版本号是「提示」非强制：实测写 3.2.0 会还原到 3.5.x。
//   3) 本文件由 tools/docx-skills/generate.mjs 生成，请勿直接手改；改共享内核后重新生成。
// ============================================================================

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
// 图表库与 OpenXML 存在同名类型（Color / PointF 等）：显式起别名，避开 CS0104 二义性
using ImgColor = SixLabors.ImageSharp.Color;
using ImgPointF = SixLabors.ImageSharp.PointF;
using ImgRect = SixLabors.ImageSharp.Drawing.RectangularPolygon;
using ImgEllipse = SixLabors.ImageSharp.Drawing.EllipsePolygon;

public class Skill
{
    // ===== 场景排版参数（由生成器注入）=====
    private const string SceneName = "gongwen";
    private const string FontTitle = "方正小标宋简体";
    private const string FontHeading = "黑体";
    private const string FontBody = "仿宋_GB2312";
    private const int SizeTitle = 44;
    private const int SizeH1 = 32;
    private const int SizeH2 = 32;
    private const int SizeH3 = 32;
    private const int SizeBody = 32;
    private const int SizeSmall = 28;
    private const int LineBody = 560;
    private const bool TitleBold = false;
    private const bool HeadingBold = false;
    private const bool BodyIndent = true;
    private const bool UsePageNumbers = true;
    private const int MarginTop = 2098;
    private const int MarginBottom = 1984;
    private const int MarginLeft = 1588;
    private const int MarginRight = 1474;
    private const int PageWidth = 11906;
    private const int PageHeight = 16838;

    // ===== 入口 =====
    public static string Run(string input)
    {
        try
        {
            var built = Build(input ?? "");
            // produce_file 标记：告诉平台“这个文件可以挂到对话里供下载”。
            // 网关扫到这个标记后会用 AttachmentStore 挂号并挂到当前消息（att_xxx）。
            // 用显式标记而非直接猜路径：避免把正文里偶然出现的任意路径误当产物。
            var produce = "";
            try
            {
                if (System.IO.File.Exists(built.Path))
                {
                    var fi = new System.IO.FileInfo(built.Path);
                    produce = ",\"produce_file\":{\"path\":" + Js(built.Path)
                        + ",\"name\":" + Js(fi.Name)
                        + ",\"bytes\":" + fi.Length + "}";
                }
            }
            catch { /* 标记失败不影响主返回 */ }
            return "{\"ok\":true,\"path\":" + Js(built.Path) + ",\"scene\":" + Js(SceneName)
                + ",\"blocks\":" + built.Blocks + produce
                // 图表字体报出来：若环境没有中文字体，图表中文会缺字（乱码），有地儿排障
                + ",\"chartFont\":" + Js(ChartFontName) + ",\"chartFontCjk\":" + (ChartFontHasCjk ? "true" : "false")
                + ImageWarningsJson()
                + ImageUsedJson()
                + ",\"message\":" + Js("已生成 Word 文档：" + built.Path
                    + (ChartFontHasCjk ? "" : "（提示：当前环境未找到含中文字形的字体，图表中文可能缺字/乱码；"
                        + "可在容器里安装 fonts-noto-cjk / fonts-droid-fallback 后重启）")) + "}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"scene\":" + Js(SceneName)
                + ",\"message\":" + Js("生成失败：" + ex.GetType().Name + "：" + ex.Message) + "}";
        }
    }

    private static (int Blocks, string Path) Build(string input)
    {
        using var reqDoc = JsonDocument.Parse(ExtractJson(input));
        var root = reqDoc.RootElement;

        var title = Str(root, "title") ?? "未命名文档";
        var subtitle = Str(root, "subtitle");
        var author = Str(root, "author");
        var dateText = Str(root, "date");
        var path = ResolveOutputPath(Str(root, "outputPath"), title);
        // 平台注入的图库检索范围句柄 + 配图来源策略（详见 ResolveQueryImage）
        _imgScopeId = Str(root, "imageScopeId") ?? Str(root, "image_scope_id");
        _imgSource = (Str(root, "imageSource") ?? Str(root, "image_source")
            ?? Environment.GetEnvironmentVariable("AGUI_IMAGE_SOURCE") ?? "").Trim();
        _imgWarnings = new System.Collections.Generic.List<string>();
        _imgRecentText = null;   // 配图上下文候选：每份稿子从零开始
        _imgLastHeading = null;
        _imgOwnText = null;
        _imgUsed = new System.Collections.Generic.List<ImgHit>();

        int blocks = 0;
        using (var wd = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = wd.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);

            InstallStyles(main);
            string? footerRefId;
            // 页脚居中页码（PAGE 域；Word 打开即显示）
            var footerPart = main.AddNewPart<FooterPart>();
            var footerPara = new Paragraph(new ParagraphProperties(
                new Justification { Val = JustificationValues.Center }));
            footerPara.AppendChild(new Run(new RunProperties(
                new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = FontBody },
                new FontSize { Val = SizeSmall.ToString() }),
                new FieldChar { FieldCharType = FieldCharValues.Begin },
                new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve },
                new FieldChar { FieldCharType = FieldCharValues.End }));
            footerPart.Footer = new Footer(footerPara);
            footerPart.Footer.Save();
            footerRefId = main.GetIdOfPart(footerPart);

            body.AppendChild(TitlePara(title));
            blocks++;
            if (!string.IsNullOrWhiteSpace(subtitle)) { body.AppendChild(SubTitlePara(Safe(subtitle))); blocks++; }
            if (!string.IsNullOrWhiteSpace(author)) { body.AppendChild(AuthorPara(Safe(author))); blocks++; }
            if (!string.IsNullOrWhiteSpace(dateText)) { body.AppendChild(AuthorPara(Safe(dateText))); blocks++; }

            if (root.TryGetProperty("sections", out var secs) && secs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in secs.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    if (AppendBlock(main, body, s)) blocks++;
                }
            }

            // 本场景不自动添加结尾语
            body.AppendChild(SectionProps(footerRefId));
            main.Document.Save();
        }
        return (blocks, path);
    }

    // ===== 内容块派发（各场景可用块一致，保证行为可预期）=====
    // 外层包一层：块处理完后把它上面的文字滚入“配图上下文”（图片块要拿前面最近的文字块去查图库）。
    private static bool AppendBlock(MainDocumentPart main, Body body, JsonElement s)
    {
        var appended = DispatchBlock(main, body, s);
        var t = BlockTextOf(s);
        if (t.Length > 0) PushImgContext(t);
        var h = Str(s, "heading");
        if (!string.IsNullOrWhiteSpace(h)) _imgLastHeading = Cap(h, ImgContextMaxChars);
        return appended;
    }

    /// <summary>本图自己的文字（caption + alt）：当上下文候选的第一位。</summary>
    private static string ImgOwnTextOf(JsonElement im)
        => Cap(string.Join(" ", new[] { Str(im, "caption"), Str(im, "alt") }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim())), ImgContextMaxChars);

    /// <summary>本块自带的文字（用于“后面那张图的上下文”）；图片的 caption 归图片自己用，不算在滚动上下文里。</summary>
    private static string BlockTextOf(JsonElement s)
    {
        var parts = new System.Collections.Generic.List<string>();
        var h = Str(s, "heading");
        if (!string.IsNullOrWhiteSpace(h)) parts.Add(h!.Trim());
        var p = Str(s, "paragraph");
        if (!string.IsNullOrWhiteSpace(p)) parts.Add(p!.Trim());
        var q = Str(s, "quote");
        if (!string.IsNullOrWhiteSpace(q)) parts.Add(q!.Trim());
        foreach (var name in new[] { "bullets", "numbered" })
        {
            if (!s.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var it in arr.EnumerateArray())
            {
                var v = it.GetString();
                if (!string.IsNullOrWhiteSpace(v)) parts.Add(v!.Trim());
            }
        }
        return string.Join(" ", parts);
    }

    private static bool DispatchBlock(MainDocumentPart main, Body body, JsonElement s)
    {
        if (s.TryGetProperty("pageBreak", out var pb) && pb.ValueKind == JsonValueKind.True)
        {
            body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
            return true;
        }
        // 目录（TOC 域）：依赖 Heading1/2/3 的 outlineLvl；打开后需 F9 更新域（Word 会提示）
        if (s.TryGetProperty("toc", out var toc) && (toc.ValueKind == JsonValueKind.True || toc.ValueKind == JsonValueKind.Object))
        {
            int depth = 3;
            string? tocTitle = null;
            if (toc.ValueKind == JsonValueKind.Object)
            {
                if (toc.TryGetProperty("depth", out var dp) && dp.ValueKind == JsonValueKind.Number) depth = Math.Max(1, Math.Min(3, dp.GetInt32()));
                tocTitle = Str(toc, "title");
            }
            if (!string.IsNullOrWhiteSpace(tocTitle)) body.AppendChild(HeadingPara(Safe(tocTitle), 1));
            body.AppendChild(TocPara(depth));
            body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page }))); // 目录后另起页
            return true;
        }
        var heading = Str(s, "heading");
        if (!string.IsNullOrWhiteSpace(heading))
        {
            int lvl = 1;
            if (s.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number) lvl = lv.GetInt32();
            body.AppendChild(HeadingPara(Safe(heading), lvl));
            return true;
        }
        var para = Str(s, "paragraph");
        if (!string.IsNullOrWhiteSpace(para)) { body.AppendChild(BodyPara(Safe(para))); return true; }

        if (s.TryGetProperty("bullets", out var bl) && bl.ValueKind == JsonValueKind.Array)
        {
            int n = 0;
            foreach (var b in bl.EnumerateArray())
            {
                var txt = b.GetString();
                if (!string.IsNullOrWhiteSpace(txt)) { body.AppendChild(BulletPara(Safe(txt))); n++; }
            }
            return n > 0;
        }
        if (s.TryGetProperty("numbered", out var nu) && nu.ValueKind == JsonValueKind.Array)
        {
            int n = 0, i = 1;
            foreach (var b in nu.EnumerateArray())
            {
                var txt = b.GetString();
                if (!string.IsNullOrWhiteSpace(txt)) { body.AppendChild(NumberedPara(Safe(txt), i++)); n++; }
            }
            return n > 0;
        }
        if (s.TryGetProperty("quote", out var q) && q.ValueKind == JsonValueKind.String)
        {
            var txt = q.GetString();
            if (!string.IsNullOrWhiteSpace(txt)) { body.AppendChild(QuotePara(Safe(txt))); return true; }
        }
        if (s.TryGetProperty("table", out var tb) && tb.ValueKind == JsonValueKind.Object)
        {
            var t = BuildTable(tb);
            if (t != null) { body.AppendChild(t); return true; }
        }
        if (s.TryGetProperty("image", out var im) && im.ValueKind == JsonValueKind.Object)
        {
            // 本图自己的文字（caption/alt）当上下文候选之一（见 ResolveQueryImage）
            _imgOwnText = ImgOwnTextOf(im);
            var p = BuildImagePara(main, im);
            if (p != null) { body.AppendChild(p); return true; }
        }
        // 图表：用 ImageSharp 渲成 PNG 再按图片嵌入（避开 DrawingML ChartPart 的 schema 风险）
        if (s.TryGetProperty("chart", out var ch) && ch.ValueKind == JsonValueKind.Object)
        {
            var p = BuildChartPara(main, ch);
            if (p != null) { body.AppendChild(p); return true; }
        }
        return false;
    }

    // ===== styles.xml：docDefaults + Heading（带 OutlineLevel，TOC/导航才认得出）=====
    private static void InstallStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        styles.AppendChild(new DocDefaults(
            new RunPropertiesDefault(
                new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = FontBody },
                    new FontSize { Val = SizeBody.ToString() })),
            new ParagraphPropertiesDefault(
                new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { Line = LineBody.ToString(), LineRule = LineSpacingRuleValues.Exact }))));

        styles.AppendChild(new Style(
            new StyleName { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { Line = LineBody.ToString(), LineRule = LineSpacingRuleValues.Exact }),
            new StyleRunProperties(
                new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = FontBody },
                new FontSize { Val = SizeBody.ToString() }))
        { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });

        AddHeadingStyle(styles, "Heading1", "heading 1", SizeH1, 0);
        AddHeadingStyle(styles, "Heading2", "heading 2", SizeH2, 1);
        AddHeadingStyle(styles, "Heading3", "heading 3", SizeH3, 2);

        part.Styles = styles;
        part.Styles.Save();
    }

    private static void AddHeadingStyle(Styles styles, string id, string name, int size, int outline)
    {
        styles.AppendChild(new Style(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleParagraphProperties(
                new KeepNext(),
                new OutlineLevel { Val = outline },
                new SpacingBetweenLines { Before = "240", After = "120", Line = LineBody.ToString(), LineRule = LineSpacingRuleValues.Exact },
                new Justification { Val = JustificationValues.Left }),
            new StyleRunProperties(
                new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = FontHeading },
                new FontSize { Val = size.ToString() }))
        { Type = StyleValues.Paragraph, StyleId = id, Default = false });
    }

    // ===== 段落构件 =====
    private static Paragraph TitlePara(string text)
    {
        var p = new Paragraph(new ParagraphProperties(
            new Justification { Val = JustificationValues.Center },
            new KeepNext(),
            new SpacingBetweenLines { After = "240", Line = "600", LineRule = LineSpacingRuleValues.Exact }));
        p.AppendChild(RunWith(text, FontTitle, SizeTitle, TitleBold));
        return p;
    }

    private static Paragraph SubTitlePara(string text)
    {
        var p = new Paragraph(new ParagraphProperties(
            new Justification { Val = JustificationValues.Center },
            new KeepNext(),
            new SpacingBetweenLines { After = "120" }));
        p.AppendChild(RunWith(text, FontHeading, SizeH2, false));
        return p;
    }

    private static Paragraph AuthorPara(string text)
    {
        var p = new Paragraph(new ParagraphProperties(
            new Justification { Val = JustificationValues.Center },
            new SpacingBetweenLines { After = "300" }));
        p.AppendChild(RunWith(text, FontBody, SizeSmall, false));
        return p;
    }

    private static Paragraph HeadingPara(string text, int level)
    {
        var lv = Math.Max(1, Math.Min(3, level));
        var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading" + lv }));
        p.AppendChild(RunWith(text, FontHeading, lv == 1 ? SizeH1 : lv == 2 ? SizeH2 : SizeH3, HeadingBold));
        return p;
    }

    private static Paragraph BodyPara(string text)
    {
        var ppr = new ParagraphProperties();
        if (BodyIndent) ppr.AppendChild(new Indentation { FirstLineChars = 200 }); // 首行缩进 2 字符（东亚版式）
        ppr.AppendChild(new SpacingBetweenLines { Line = LineBody.ToString(), LineRule = LineSpacingRuleValues.Exact });
        ppr.AppendChild(new Justification { Val = JustificationValues.Both });
        var p = new Paragraph(ppr);
        p.AppendChild(RunWith(text, FontBody, SizeBody, false));
        return p;
    }

    private static Paragraph BulletPara(string text)
    {
        var p = new Paragraph(new ParagraphProperties(
            new Indentation { LeftChars = 200, HangingChars = 100 },
            new SpacingBetweenLines { Line = LineBody.ToString(), LineRule = LineSpacingRuleValues.Exact }));
        p.AppendChild(RunWith("• " + text, FontBody, SizeBody, false));
        return p;
    }

    private static Paragraph NumberedPara(string text, int index)
    {
        var p = new Paragraph(new ParagraphProperties(
            new Indentation { LeftChars = 200, HangingChars = 100 },
            new SpacingBetweenLines { Line = LineBody.ToString(), LineRule = LineSpacingRuleValues.Exact }));
        p.AppendChild(RunWith(index + ". " + text, FontBody, SizeBody, false));
        return p;
    }

    private static Paragraph QuotePara(string text)
    {
        var ppr = new ParagraphProperties(
            new Indentation { LeftChars = 200, RightChars = 200 },
            new SpacingBetweenLines { Line = LineBody.ToString(), LineRule = LineSpacingRuleValues.Exact });
        ppr.AppendChild(new ParagraphBorders(new LeftBorder { Val = BorderValues.Single, Size = 12, Color = "808080" }));
        var p = new Paragraph(ppr);
        p.AppendChild(RunWith(text, FontBody, SizeSmall, false));
        return p;
    }

    // ===== 目录（TOC 域）=====
    // 用复杂域：begin → instrText → separate → 占位提示 → end。
    // 打开文档时 Word 可能提示“更新域”；也可 Ctrl+A → F9 刷新。
    private static Paragraph TocPara(int depth)
    {
        var p = new Paragraph(new ParagraphProperties(
            new SpacingBetweenLines { Line = LineBody.ToString(), LineRule = LineSpacingRuleValues.Exact }));

        var r1 = new Run(new FieldChar { FieldCharType = FieldCharValues.Begin });
        var r2 = new Run(new FieldCode(" TOC \\o \"1-" + depth + "\" \\h \\z \\u ") { Space = SpaceProcessingModeValues.Preserve });
        var r3 = new Run(new FieldChar { FieldCharType = FieldCharValues.Separate });
        var r4 = new Run(new RunProperties(
                new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = FontBody },
                new FontSize { Val = SizeSmall.ToString() },
                new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "808080" },
                new Italic()),
            new Text("（目录将在 Word 中更新域后生成：全选后按 F9）") { Space = SpaceProcessingModeValues.Preserve });
        var r5 = new Run(new FieldChar { FieldCharType = FieldCharValues.End });

        p.AppendChild(r1); p.AppendChild(r2); p.AppendChild(r3); p.AppendChild(r4); p.AppendChild(r5);
        return p;
    }

    // ===== 图表（柱状 / 折线 / 饼图）=====
    // 用 ImageSharp 渲成 PNG，再沿用图片嵌入路径 —— 不发 ChartPart（避开 DrawingML 图表 schema 风险，
    // 也不会在旧版 Word 里显示为“不可读内容”）。图表缩放/居中/图题与图片一致。
    private static Paragraph? BuildChartPara(MainDocumentPart main, JsonElement ch)
    {
        var kind = (Str(ch, "type") ?? "bar").ToLowerInvariant();
        var categories = new List<string>();
        if (ch.TryGetProperty("categories", out var cat) && cat.ValueKind == JsonValueKind.Array)
            foreach (var c in cat.EnumerateArray()) categories.Add(c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ToString());

        // series: [{ name, values: [...] }]，也容忍 values 直接写在顶层
        var series = new List<(string Name, double[] Values)>();
        if (ch.TryGetProperty("series", out var sr) && sr.ValueKind == JsonValueKind.Array)
        {
            foreach (var one in sr.EnumerateArray())
            {
                if (one.ValueKind != JsonValueKind.Object) continue;
                var nm = Str(one, "name") ?? "";
                series.Add((nm, ReadNumbers(one, "values")));
            }
        }
        if (series.Count == 0) series.Add(("", ReadNumbers(ch, "values")));
        if (series.All(s => s.Values.Length == 0)) throw new InvalidOperationException("图表缺少数据：请提供 series[].values 或 values");
        if (categories.Count == 0)
        {
            int max = series.Max(s => s.Values.Length);
            for (int i = 0; i < max; i++) categories.Add((i + 1).ToString());
        }

        var title = Str(ch, "title");         // 图内标题（可选）
        var yLabel = Str(ch, "yLabel");        // Y 轴单位（可选）
        var caption = Str(ch, "caption");      // 图题（可选）

        int pxW = 900, pxH = 480;
        if (ch.TryGetProperty("pixelWidth", out var pw) && pw.ValueKind == JsonValueKind.Number) pxW = Math.Max(320, Math.Min(2400, pw.GetInt32()));
        if (ch.TryGetProperty("pixelHeight", out var ph) && ph.ValueKind == JsonValueKind.Number) pxH = Math.Max(200, Math.Min(1600, ph.GetInt32()));

        var png = kind switch
        {
            "pie" => RenderPie(pxW, pxH, title, categories, series[0].Values),
            "line" => RenderLine(pxW, pxH, title, yLabel, categories, series),
            _ => RenderBar(pxW, pxH, title, yLabel, categories, series),
        };

        var imagePart = main.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream(png)) imagePart.FeedData(ms);
        var relId = main.GetIdOfPart(imagePart);

        double widthCm = 14.0;
        if (ch.TryGetProperty("widthCm", out var wc) && wc.ValueKind == JsonValueKind.Number) widthCm = wc.GetDouble();
        widthCm = Math.Max(3.0, Math.Min(24.0, widthCm));
        long cx = (long)Math.Round(widthCm * 360000.0);
        long cy = (long)Math.Round(cx * (double)pxH / pxW);

        return ImageDrawingParagraph(relId, cx, cy, caption, Str(ch, "alt") ?? title);
    }

    private static double[] ReadNumbers(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<double>();
        var list = new List<double>();
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.Number) list.Add(x.GetDouble());
            else if (x.ValueKind == JsonValueKind.String && double.TryParse(x.GetString(), out var d)) list.Add(d);
        }
        return list.ToArray();
    }

    // 配色（低饱和，打印友好）
    private static readonly string[] Palette =
        { "#4F81BD", "#C0504D", "#9BBB59", "#8064A2", "#4BACC6", "#F79646", "#2C4D75", "#772C2A" };

    private static readonly string[] FontCandidates =
    {
        // 中文字体优先（Windows / macOS / Linux 各发行版的常见族名与中文名）
        "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "SimSun", "宋体", "DengXian", "等线", "KaiTi", "楷体",
        "Noto Sans CJK SC", "Noto Sans CJK TC", "Noto Sans CJK JP", "Noto Sans SC", "Noto Sans CJK",
        "Source Han Sans SC", "Source Han Sans CN", "WenQuanYi Micro Hei", "WenQuanYi Zen Hei",
        "Droid Sans Fallback", "AR PL UMing CN", "AR PL UKai CN", "文泉驿微米黑",
        "PingFang SC", "Hiragino Sans GB", "STHeiti", "Heiti SC",
        // 纯拉丁字体（仅当上面都没命中时）：能出图，但中文会缺字
        "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans",
    };

    private static readonly object _fontLock = new object();
    private static SixLabors.Fonts.FontFamily? _fontFamily;
    private static string _fontName = "";
    private static bool _fontCjk;

    /// <summary>所选图表字体族名与是否含中文字形（供返回信息与排障用）。</summary>
    internal static string ChartFontName => _fontName;
    internal static bool ChartFontHasCjk => _fontCjk;

    /// <summary>
    /// 取一个可用的无衬线字体（优先含 CJK 覆盖）；找不到则抛出可读错误。
    ///
    /// <para>
    /// <b>关键：不能只按“族名命中”就选定</b>。实测踩到——容器里（Linux）前几候选
    /// （Microsoft YaHei / SimHei / SimSun / Arial）都不存在，名单里第一个存在的是
    /// <c>DejaVu Sans</c>（纯拉丁字体），于是图表里的中文标题、分类标签、系列名全部缺字，
    /// 表现为乱码/空白，而英文坐标数字正常。因此命中后必须验字形覆盖。
    /// </para>
    /// </summary>
    private static SixLabors.Fonts.Font Family(float size)
    {
        lock (_fontLock)
        {
            if (_fontFamily is null) PickFamily();
            return _fontFamily!.Value.CreateFont(size);
        }
    }

    private static void PickFamily()
    {
        // 1) 候选名单里第一个真的含中文字形的
        foreach (var name in FontCandidates)
            if (SixLabors.Fonts.SystemFonts.TryGet(name, out var f) && CanRenderCjk(f))
            {
                _fontFamily = f; _fontName = name; _fontCjk = true; return;
            }
        // 2) 名单没命中，但系统里有中文字体（族名未知，如容器的 Noto Sans CJK SC）
        foreach (var f in SixLabors.Fonts.SystemFonts.Collection.Families)
            if (CanRenderCjk(f))
            {
                _fontFamily = f; _fontName = f.Name; _fontCjk = true; return;
            }
        // 3) 没有中文字体：退回第一个可用字体（能出图，中文会缺字）
        foreach (var name in FontCandidates)
            if (SixLabors.Fonts.SystemFonts.TryGet(name, out var f))
            {
                _fontFamily = f; _fontName = name; _fontCjk = false; return;
            }
        if (SixLabors.Fonts.SystemFonts.Collection.Families.Any())
        {
            var f = SixLabors.Fonts.SystemFonts.Collection.Families.First();
            _fontFamily = f; _fontName = f.Name; _fontCjk = false; return;
        }
        throw new InvalidOperationException("图表需要至少一种系统字体，但当前环境未发现可用字体。");
    }

    /// <summary>该字体族是否真的含中文字形（取样常用汉字，避免“只有标点/符号”的假阳性）。</summary>
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

    // ===== 文本适配：内容太多时不能越界 =====

    /// <summary>按可用宽度裁剪文本（超宽则逐字回退并加省略号）。</summary>
    private static string FitText(string? text, float size, double maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth < 10) return "";
        if (MeasureText(text!, size) <= maxWidth) return text!;
        for (var n = text!.Length - 1; n > 0; n--)
        {
            var cand = text.Substring(0, n) + "…";
            if (MeasureText(cand, size) <= maxWidth) return cand;
        }
        return "";
    }

    /// <summary>先缩小字号直到放得下（不低于 minSize），仍放不下再裁剪。返回裁剪后的文本与实际字号。</summary>
    private static (string Text, float Size) FitTextShrink(string? text, float size, double maxWidth, float minSize)
    {
        if (string.IsNullOrEmpty(text)) return ("", size);
        var s = size;
        while (s > minSize && MeasureText(text!, s) > maxWidth) s -= 1f;
        return (FitText(text, s, maxWidth), s);
    }

    private static double MeasureText(string text, float size)
        => SixLabors.Fonts.TextMeasurer.MeasureBounds(text, new SixLabors.Fonts.TextOptions(Family(size))).Width;

    /// <summary>把 x 夹在 [min,max]，保证绘制起点不越界。</summary>
    private static float ClampX(float v, float min, float max)
        => max < min ? min : (v < min ? min : (v > max ? max : v));

    private sealed class LegendItem
    {
        public string Text = "";
        public float X;
        public float Y;
        public int ColorIndex;
    }

    private sealed class LegendLayout
    {
        public List<LegendItem> Items = new List<LegendItem>();
        public int Rows;
        public int Hidden;
    }

    /// <summary>
    /// 图例排版：按可用宽度换行，最多 maxRows 行；放不下的计入 Hidden。
    ///
    /// <para>
    /// 原实现是一行横向累加 x，系列一多就直接画出画布右边（典型“内容太多越界”）。
    /// </para>
    /// </summary>
    private static LegendLayout LayoutLegend(List<(string Name, double[] Values)> series, double availW, float size, int maxRows)
    {
        var layout = new LegendLayout();
        if (availW < 60) { layout.Hidden = series.Count; return layout; }
        float x = 0;
        int row = 0;
        for (int s = 0; s < series.Count; s++)
        {
            var nm = string.IsNullOrEmpty(series[s].Name) ? "系列" + (s + 1) : series[s].Name;
            var text = FitText(nm, size, Math.Min(220, availW - 42));
            if (text.Length == 0) text = "系列" + (s + 1);
            float itemW = 19 + (float)MeasureText(text, size) + 20;
            if (x > 0 && x + itemW > availW) { row++; x = 0; }
            if (row >= maxRows) { layout.Hidden = series.Count - s; break; }
            layout.Items.Add(new LegendItem { Text = text, X = x, Y = row * 22f, ColorIndex = s });
            x += itemW;
        }
        layout.Rows = layout.Items.Count == 0 ? 0 : row + 1;
        return layout;
    }

    /// <summary>画图例，并在被截断时补一个“…”提示（不谎报系列数）。</summary>
    private static void DrawLegend(IImageProcessingContext x, LegendLayout legend, float left, float top,
        double availW, SixLabors.Fonts.Font font, ImgColor fg, ImgColor muted)
    {
        foreach (var it in legend.Items)
        {
            var col = ImgColor.ParseHex(Palette[it.ColorIndex % Palette.Length]);
            x.Fill(col, new ImgRect(left + it.X, top + it.Y, 14, 14));
            x.DrawText(it.Text, font, fg, new ImgPointF(left + it.X + 19, top + it.Y - 3));
        }
        if (legend.Hidden > 0 && legend.Rows > 0)
        {
            var last = legend.Items[legend.Items.Count - 1];
            var hint = "…等 " + (legend.Items.Count + legend.Hidden) + " 项";
            var hx = left + last.X + 19 + (float)MeasureText(last.Text, 13f) + 20;
            var hw = (float)MeasureText(hint, 13f);
            if (hx + hw <= left + availW) x.DrawText(hint, Family(13f), muted, new ImgPointF(hx, top + last.Y));
        }
    }

    /// <summary>图表标题：先缩字号（不低于 16）再裁剪，长标题不越出画布右边。</summary>
    private static void DrawChartTitle(IImageProcessingContext x, string? title, float left, float top, double availW)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var fit = FitTextShrink(title, 24f, availW, 16f);
        if (fit.Text.Length == 0) return;
        x.DrawText(fit.Text, Family(fit.Size), ImgColor.Black, new ImgPointF(left, top));
    }

    private static byte[] RenderBar(int w, int h, string? title, string? yLabel, List<string> cats, List<(string Name, double[] Values)> series)
    {
        using var img = new Image<Rgba32>(w, h);
        var fg = ImgColor.Black;
        var grid = ImgColor.FromRgba(210, 210, 210, 255);
        var muted = ImgColor.FromRgba(90, 90, 90, 255);
        double maxV = Math.Max(0.0001, series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max());
        const int padR = 28, padB = 64;

        // 刻度文案先算出来：左边距按“最宽的刻度”留，否则大数字会压到绘图区（越界）
        const int ticks = 4;
        var tickTexts = new string[ticks + 1];
        for (int i = 0; i <= ticks; i++) tickTexts[i] = (maxV * i / ticks).ToString("0.##");
        double tickW = tickTexts.Max(s => MeasureText(s, 13f));
        int padL = (int)Math.Min(170, Math.Max(66, tickW + 26));

        // 图例先排版（含换行），据此预留顶部高度：既不把绘图区挤没，也不会自身越界
        var legendSeries = series.Count > 1 ? series : new List<(string Name, double[] Values)>();
        var legend = LayoutLegend(legendSeries, w - padL - padR, 13f, 3);
        int padT = (title is null ? 36 : 64) + legend.Rows * 22;

        img.Mutate(x =>
        {
            x.Fill(ImgColor.White);
            DrawChartTitle(x, title, padL, 22, w - padL - padR);

            // Y 轴刻度 + 网格
            for (int i = 0; i <= ticks; i++)
            {
                float y = h - padB - (float)((h - padT - padB) * i / (double)ticks);
                x.DrawLine(grid, 1f, new ImgPointF(padL, y), new ImgPointF(w - padR, y));
                // 右对齐到轴线左侧，不再固定 x=6
                x.DrawText(tickTexts[i], Family(13f), muted,
                    new ImgPointF((float)(padL - 8 - MeasureText(tickTexts[i], 13f)), y - 9));
            }
            x.DrawLine(fg, 1.6f, new ImgPointF(padL, padT), new ImgPointF(padL, h - padB));
            x.DrawLine(fg, 1.6f, new ImgPointF(padL, h - padB), new ImgPointF(w - padR, h - padB));
            if (!string.IsNullOrWhiteSpace(yLabel))
                x.DrawText(FitText(yLabel, 13f, w - padL - padR), Family(13f), muted,
                    new ImgPointF(padL, Math.Max(6, padT - 22)));
            if (legend.Rows > 0)
                DrawLegend(x, legend, padL, padT - legend.Rows * 22f, w - padL - padR, Family(13f), fg, muted);

            int nc = cats.Count;
            int ns = series.Count;
            double slot = (w - padL - padR) / (double)Math.Max(1, nc);
            double barW = Math.Max(4, slot * 0.72 / ns);
            // 分类太密时隔位显示，避免标签叠成一团
            int stride = Math.Max(1, (int)Math.Ceiling(52.0 / Math.Max(1.0, slot)));

            for (int c = 0; c < nc; c++)
            {
                for (int s = 0; s < ns; s++)
                {
                    double v = c < series[s].Values.Length ? series[s].Values[c] : 0;
                    if (v < 0) v = 0;
                    float bh = (float)((h - padT - padB) * (v / maxV));
                    float bx = (float)(padL + slot * c + slot * 0.14 + barW * s);
                    float by = h - padB - bh;
                    var color = ImgColor.ParseHex(Palette[(ns > 1 ? s : c) % Palette.Length]);
                    if (bh > 0.5f) x.Fill(color, new ImgRect(bx, by, (float)barW, bh));
                }
                // X 轴类别标签：按槽宽裁剪（而不是按字符数），起点夹在绘图区内
                if (c % stride != 0) continue;
                var lab = FitText(cats[c], 13f, slot * stride * 0.96);
                if (lab.Length == 0) continue;
                float tw = (float)MeasureText(lab, 13f);
                float lx = ClampX((float)(padL + slot * c + slot / 2 - tw / 2), padL, (float)(w - padR - tw));
                x.DrawText(lab, Family(13f), fg, new ImgPointF(lx, h - padB + 8));
            }
        });
        using var outMs = new MemoryStream();
        img.SaveAsPng(outMs);
        return outMs.ToArray();
    }

    private static byte[] RenderLine(int w, int h, string? title, string? yLabel, List<string> cats, List<(string Name, double[] Values)> series)
    {
        using var img = new Image<Rgba32>(w, h);
        var fg = ImgColor.Black;
        var grid = ImgColor.FromRgba(210, 210, 210, 255);
        var muted = ImgColor.FromRgba(90, 90, 90, 255);
        var all = series.SelectMany(s => s.Values).DefaultIfEmpty(0).ToList();
        double minV = Math.Min(0, all.Min());
        double maxV = Math.Max(0.0001, all.Max());
        if (maxV - minV < 0.0001) maxV = minV + 1;
        const int padR = 28, padB = 64;

        // 刻度文案先算出来：左边距按“最宽的刻度”留，否则大数字会压到绘图区（越界）
        const int ticks = 4;
        var tickTexts = new string[ticks + 1];
        for (int i = 0; i <= ticks; i++) tickTexts[i] = (minV + (maxV - minV) * i / ticks).ToString("0.##");
        double tickW = tickTexts.Max(s => MeasureText(s, 13f));
        int padL = (int)Math.Min(170, Math.Max(66, tickW + 26));
        var legendSeries = series.Count > 1 ? series : new List<(string Name, double[] Values)>();
        var legend = LayoutLegend(legendSeries, w - padL - padR, 13f, 3);
        int padT = (title is null ? 36 : 64) + legend.Rows * 22;

        img.Mutate(x =>
        {
            x.Fill(ImgColor.White);
            DrawChartTitle(x, title, padL, 22, w - padL - padR);

            for (int i = 0; i <= ticks; i++)
            {
                float y = h - padB - (float)((h - padT - padB) * i / (double)ticks);
                x.DrawLine(grid, 1f, new ImgPointF(padL, y), new ImgPointF(w - padR, y));
                x.DrawText(tickTexts[i], Family(13f), muted,
                    new ImgPointF((float)(padL - 8 - MeasureText(tickTexts[i], 13f)), y - 9));
            }
            x.DrawLine(fg, 1.6f, new ImgPointF(padL, padT), new ImgPointF(padL, h - padB));
            x.DrawLine(fg, 1.6f, new ImgPointF(padL, h - padB), new ImgPointF(w - padR, h - padB));
            if (!string.IsNullOrWhiteSpace(yLabel))
                x.DrawText(FitText(yLabel, 13f, w - padL - padR), Family(13f), muted,
                    new ImgPointF(padL, Math.Max(6, padT - 22)));
            if (legend.Rows > 0)
                DrawLegend(x, legend, padL, padT - legend.Rows * 22f, w - padL - padR, Family(13f), fg, muted);

            int nc = cats.Count;
            double slot = nc <= 1 ? (w - padL - padR) : (w - padL - padR) / (double)(nc - 1);
            int stride = Math.Max(1, (int)Math.Ceiling(52.0 / Math.Max(1.0, slot)));
            for (int s = 0; s < series.Count; s++)
            {
                var color = ImgColor.ParseHex(Palette[s % Palette.Length]);
                var pts = new List<PointF>();
                for (int c = 0; c < cats.Count; c++)
                {
                    double v = c < series[s].Values.Length ? series[s].Values[c] : minV;
                    float px = nc == 1 ? padL + (w - padL - padR) / 2f : (float)(padL + slot * c);
                    float py = (float)(h - padB - (h - padT - padB) * ((v - minV) / (maxV - minV)));
                    pts.Add(new ImgPointF(px, py));
                }
                for (int i = 1; i < pts.Count; i++) x.DrawLine(color, 2.4f, pts[i - 1], pts[i]);
                for (int i = 0; i < pts.Count; i++) x.Fill(color, new ImgEllipse(pts[i].X, pts[i].Y, 4f));
            }
            for (int c = 0; c < cats.Count; c++)
            {
                // 按槽宽裁剪 + 隔位显示 + 起点夹在绘图区内
                if (c % stride != 0) continue;
                var lab = FitText(cats[c], 13f, slot * stride * 0.96);
                if (lab.Length == 0) continue;
                float px = nc == 1 ? padL + (w - padL - padR) / 2f : (float)(padL + slot * c);
                float tw = (float)MeasureText(lab, 13f);
                float lx = ClampX(px - tw / 2, padL, (float)(w - padR - tw));
                x.DrawText(lab, Family(13f), fg, new ImgPointF(lx, h - padB + 8));
            }
        });
        using var outMs = new MemoryStream();
        img.SaveAsPng(outMs);
        return outMs.ToArray();
    }

    private static byte[] RenderPie(int w, int h, string? title, List<string> cats, double[] values)
    {
        using var img = new Image<Rgba32>(w, h);
        double total = values.Where(v => v > 0).Sum();
        if (total <= 0) throw new InvalidOperationException("饼图数据全部为 0，无法绘制。");

        img.Mutate(x =>
        {
            x.Fill(ImgColor.White);
            DrawChartTitle(x, title, 24, 18, w - 48);

            float cy = h / 2f + 10, cx = w * 0.34f;
            float r = Math.Min(w * 0.30f, h * 0.40f);
            // 角度用度
            float startDeg = -90f;
            for (int i = 0; i < values.Length; i++)
            {
                double v = values[i] <= 0 ? 0 : values[i];
                if (v == 0) continue;
                float sweepDeg = (float)(360.0 * (v / total));
                // 扇形必须由「圆心 → 沿弧走一圈 → 回圆心」围成。
                // 【几何易错，勿改】PathBuilder.AddArc 只是往当前图形里追加一段弧：它既不先移到圆心，
                // 也不会自动补上两条半径。只写 AddArc（哪怕再补 CloseFigure）得到的是「弧 + 弦」
                // 围成的弓形，面积几乎为 0——实测 40 项饼图只剩贴外缘的一圈发丝线，圆盘内部整片留白。
                // 这里用多边形逼近（每 2° 一段，弦高远小于 1px）显式围出扇形。
                int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweepDeg) / 2f));
                var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
                pb.MoveTo(new ImgPointF(cx, cy));
                for (int s = 0; s <= steps; s++)
                {
                    float ang = (startDeg + sweepDeg * s / steps) * (float)Math.PI / 180f;
                    pb.LineTo(new ImgPointF(cx + r * (float)Math.Cos(ang), cy + r * (float)Math.Sin(ang)));
                }
                pb.CloseFigure();
                x.Fill(ImgColor.ParseHex(Palette[i % Palette.Length]), pb.Build());
                startDeg += sweepDeg;
            }
            // 外圈描边
            x.Draw(ImgColor.Black, 1f, new ImgEllipse(cx, cy, r));

            // 图例：每项按可用宽度裁剪、按可用高度限制行数；放不下的补“…等 N 项”
            // （原实现 lx 固定、ly 逐行 +24 递增，分类一多就画出画布底部/右边）
            float lx = w * 0.68f;
            double availW = w - lx - 20;
            const float rowH = 24f;
            float top = Math.Max(56f, h * 0.16f);
            int n = Math.Min(cats.Count, values.Length);
            int maxRows = Math.Max(1, (int)Math.Floor((h - top - 34) / rowH));
            int shown = Math.Min(n, maxRows);
            for (int i = 0; i < shown; i++)
            {
                double v = Math.Max(0, values[i]);
                var pct = (v / total * 100).ToString("0.#") + "%";
                var nm = cats[i] + "  " + pct;
                float ly = top + i * rowH;
                x.Fill(ImgColor.ParseHex(Palette[i % Palette.Length]), new ImgRect(lx, ly, 14, 14));
                x.DrawText(FitText(nm, 14f, availW - 22), Family(14f), ImgColor.Black, new ImgPointF(lx + 20, ly - 3));
            }
            if (shown < n)
            {
                var more = "…等 " + n + " 项";
                float ly = top + shown * rowH;
                x.DrawText(FitText(more, 13f, availW - 22), Family(13f), ImgColor.FromRgba(90, 90, 90, 255), new ImgPointF(lx + 20, ly));
            }
        });
        using var outMs = new MemoryStream();
        img.SaveAsPng(outMs);
        return outMs.ToArray();
    }

    // ===== 图片（内联）=====
    // 从本地文件读入并嵌入；支持 widthCm 控制宽度（按原图比例缩放）。
    // 支持的格式：png / jpg / jpeg / gif / bmp / tiff；其它能解码的（如 WebP）自动重编成 PNG。
    private static Paragraph? BuildImagePara(MainDocumentPart main, JsonElement im)
    {
        var file = Str(im, "path");
        if (string.IsNullOrWhiteSpace(file))
        {
            // 没给本地 path 但有 imageQuery：按关键词查团队图库拿一张（拿不到就跳过这张图 + 记 warning）。
            var q = ImageQueryOf(im);
            if (q.Length == 0) return null;
            var hit = ResolveQueryImage(q, out var why);
            if (hit is null)
            {
                WarnImage("配图检索未成功，已跳过该图：" + (why ?? "未知原因"));
                return null;
            }
            file = hit;
        }
        var full = Path.GetFullPath(Safe(file));
        if (!File.Exists(full)) throw new FileNotFoundException("图片文件不存在：" + full);

        var (bytes, contentType) = NormalizeImage(File.ReadAllBytes(full));
        var imagePart = main.AddImagePart(contentType switch
        {
            "image/png" => ImagePartType.Png,
            "image/jpeg" => ImagePartType.Jpeg,
            "image/gif" => ImagePartType.Gif,
            "image/bmp" => ImagePartType.Bmp,
            _ => ImagePartType.Tiff,
        });
        using (var ms = new MemoryStream(bytes)) imagePart.FeedData(ms);
        var relId = main.GetIdOfPart(imagePart);

        var (pxW, pxH) = TryReadPixelSize(bytes, ExtForContentType(contentType));
        double widthCm = 14.0; // 默认宽（A4 正文宽约 15.9cm）
        if (im.TryGetProperty("widthCm", out var wc) && wc.ValueKind == JsonValueKind.Number) widthCm = wc.GetDouble();
        else if (im.TryGetProperty("widthPercent", out var wp) && wp.ValueKind == JsonValueKind.Number)
            widthCm = Math.Max(1, Math.Min(100, wp.GetDouble())) / 100.0 * 15.9;
        widthCm = Math.Max(1.0, Math.Min(24.0, widthCm));

        // EMU: 1 cm = 360000 EMU
        long cx = (long)Math.Round(widthCm * 360000.0);
        long cy = pxW > 0 && pxH > 0 ? (long)Math.Round(cx * (double)pxH / pxW) : (long)Math.Round(cx * 0.75);

        // 等比缩到**正文区内**：图不能宽过正文宽，也不能高过正文高。
        // 实测踩到：上面只把 widthCm 卡在 ≤24cm，而 A4 正文宽只有约 15.9cm ——
        // 传 widthCm:20/24（或传一张很宽的图 + widthPercent）就会画到页边距外。
        // 上限从**当前页面的实际尺寸与页边距**算，不写死 15.9。
        var fitK = Math.Min(1.0, Math.Min((double)TextWidthEmu / cx, (double)TextHeightEmu / cy));
        if (fitK < 1.0)
        {
            cx = Math.Max(1, (long)Math.Round(cx * fitK));
            cy = Math.Max(1, (long)Math.Round(cy * fitK));
        }

        return ImageDrawingParagraph(relId, cx, cy, Str(im, "caption"), Str(im, "alt"));
    }

    /// <summary>
    /// 把图片字节规范成 Word 认得的格式（png/jpeg/gif/bmp/tiff）；其余能解码的（如 WebP）
    /// 用 ImageSharp 重编成 PNG —— 本技能已因图表引用 ImageSharp，不增加依赖。
    ///
    /// <para>以<b>文件头</b>判定而不是扩展名：图库里的文件可能是改过名的（.webp 里装的是 JPEG）。</para>
    /// </summary>
    private static (byte[] Bytes, string ContentType) NormalizeImage(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return (b, "image/png");
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return (b, "image/jpeg");
        if (b.Length > 3 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return (b, "image/gif");
        if (b.Length > 2 && b[0] == 0x42 && b[1] == 0x4D) return (b, "image/bmp");
        if (b.Length > 4 && ((b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A) || (b[0] == 0x4D && b[1] == 0x4D && b[2] == 0x00)))
            return (b, "image/tiff");
        using var img = Image.Load(b);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return (ms.ToArray(), "image/png");
    }

    /// <summary>内容类型 → 扩展名（供像素尺寸解析器按格式分支）。</summary>
    private static string ExtForContentType(string contentType) => contentType switch
    {
        "image/jpeg" => "jpeg",
        "image/gif" => "gif",
        "image/bmp" => "bmp",
        "image/tiff" => "tiff",
        _ => "png",
    };

    /// <summary>把已嵌入的图片部件包成一个居中段落（可带图题与替代文本）。</summary>
    private static Paragraph ImageDrawingParagraph(string relId, long cx, long cy, string? caption, string? alt)
    {
        var drawing = new Drawing(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = cx, Cy = cy },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = NextDrawingId(), Name = "Picture " + relId, Description = string.IsNullOrWhiteSpace(alt) ? null : Safe(alt!) },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.NonVisualGraphicFrameDrawingProperties(
                    new DocumentFormat.OpenXml.Drawing.GraphicFrameLocks { NoChangeAspect = true }),
                new DocumentFormat.OpenXml.Drawing.Graphic(
                    new DocumentFormat.OpenXml.Drawing.GraphicData(
                        new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                            new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties(
                                new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties { Id = 0U, Name = "image" },
                                new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureDrawingProperties()),
                            new DocumentFormat.OpenXml.Drawing.BlipFill(
                                new DocumentFormat.OpenXml.Drawing.Blip { Embed = relId },
                                new DocumentFormat.OpenXml.Drawing.Stretch(new DocumentFormat.OpenXml.Drawing.FillRectangle())),
                            new DocumentFormat.OpenXml.Drawing.Pictures.ShapeProperties(
                                new DocumentFormat.OpenXml.Drawing.Transform2D(
                                    new DocumentFormat.OpenXml.Drawing.Offset { X = 0L, Y = 0L },
                                    new DocumentFormat.OpenXml.Drawing.Extents { Cx = cx, Cy = cy }),
                                new DocumentFormat.OpenXml.Drawing.PresetGeometry(new DocumentFormat.OpenXml.Drawing.AdjustValueList()) { Preset = DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });

        var p = new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }));
        p.AppendChild(new Run(drawing));
        if (!string.IsNullOrWhiteSpace(caption))
        {
            p.AppendChild(new Run(new Break()));
            p.AppendChild(new Run(new RunProperties(
                    new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = FontBody },
                    new FontSize { Val = SizeSmall.ToString() }),
                new Text(Safe(caption!)) { Space = SpaceProcessingModeValues.Preserve }));
        }
        return p;
    }

    private static int _drawingId;
    private static uint NextDrawingId() => (uint)System.Threading.Interlocked.Increment(ref _drawingId);

    // ===== 配图（imageQuery → 团队图库）=====
    // 平台在调用文档技能前，会往入参里注入一个「图库检索范围句柄」（root.imageScopeId），
    // 回调地址走进程环境变量 AGUI_SELF_BASE / AGUI_SELF_TOKEN（技能与平台同进程，读得到）。
    // 技能只带句柄、不带图库 ID —— 入参由模型生成，若能自报图库 ID 就能越权读别人的图。
    // 没有句柄 = 当前没有可用图库，此时跳过配图并记 warning，而不是让整篇稿子生成失败。
    [ThreadStatic] private static string? _imgScopeId;
    [ThreadStatic] private static string? _imgSource;
    [ThreadStatic] private static System.Collections.Generic.Dictionary<string, ImgHit?>? _imgCache;
    [ThreadStatic] private static System.Collections.Generic.List<string>? _imgWarnings;
    /// <summary>前面最近的文字块（滚动累积），作为配图的“上下文候选”之一。</summary>
    [ThreadStatic] private static string? _imgRecentText;
    /// <summary>最近一个标题（滚动）：配图常用它当检索词 —— 人名 / 产品名往往就写在标题里。</summary>
    [ThreadStatic] private static string? _imgLastHeading;
    /// <summary>当前那张图自己的文字（caption + alt），由调用方在检索前设好。</summary>
    [ThreadStatic] private static string? _imgOwnText;
    /// <summary>本稿实际用到的图（回显用：哪条检索词胜出、分数多少）。</summary>
    [ThreadStatic] private static System.Collections.Generic.List<ImgHit>? _imgUsed;

    private const string SelfBaseEnv = "AGUI_SELF_BASE";
    private const string SelfTokenEnv = "AGUI_SELF_TOKEN";
    private const int ImgSearchTimeoutSec = 10;

    /// <summary>
    /// 图库检索的分数门槛（传给平台 <c>/ag-ui/images/search</c> 的 <c>minScore</c>）。
    ///
    /// <para>
    /// <b>必须显式传</b>：平台在不传时按 <b>0.25</b> 兜底，而实测（真实图库 + bge-m3）无意义关键词能到
    /// <b>0.44~0.55</b>，真实命中才是 0.62~0.88。用默认值等于不筛 —— 会把不相干的人物照当“配图”嵌进稿子，
    /// 比不配图差得多（PPT 侧踩过这个坑，这里同口径修正）。
    /// </para>
    /// </summary>
    private const double ImgMinScore = 0.60;

    /// <summary>
    /// 图库命中的“可信分”。低于它时，再用<b>上下文文字</b>查一次图库，谁分高用谁（见 <see cref="ResolveQueryImage"/>）。
    ///
    /// <para>
    /// 为何不贴着 <see cref="ImgMinScore"/>：那是“能不能用”的底线，这条是“够不够确定”。
    /// 0.6~0.78 之间实测有“蹭词命中”（图库自动生成的描述很长，通用词靠“舞台/宴会厅”这类共同词也能过线）。
    /// </para>
    /// </summary>
    private const double ImgConfidentScore = 0.78;

    /// <summary>上下文最多再查几次（每次都是本机回环检索，但也要给时间预算留余地）。</summary>
    private const int ImgMaxContextTries = 3;

    /// <summary>上下文文字的字符上限（BM25 会把得分按查询词项数摊薄，太长反而糊掉关键的那个名字）。</summary>
    private const int ImgContextMaxChars = 120;

    /// <summary>一次图库检索的结果（拿不到时为 null）。<see cref="Score"/> 用于多个候选间择优。</summary>
    private sealed class ImgHit
    {
        public string Path = "";
        public string FileName = "";
        public double Score;
        public string Query = "";
    }

    /// <summary>把一段文字滚入“前面最近的文字块”（保留末尾，最近的最相关）。</summary>
    private static void PushImgContext(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return;
        var s = (_imgRecentText ?? "").Trim();
        var merged = s.Length > 0 ? s + " " + t : t;
        _imgRecentText = merged.Length <= ImgContextMaxChars
            ? merged
            : merged.Substring(merged.Length - ImgContextMaxChars);
    }

    /// <summary>
    /// 组装当前那张图的<b>上下文候选</b>（除模型关键词外）：① 图片自己的 caption/alt；② 最近的标题；③ 前面最近的文字块。
    /// 去重、去空、每个都截到 <see cref="ImgContextMaxChars"/>，并按该顺序返回。
    ///
    /// <para>
    /// 为何要<b>分开</b>当候选、而不是拼成一长串：检索是按整串算分的 —— 实测把“获奖同事 + 标题 + 正文”
    /// 拼成一句去查，那个关键名字就被旁边的词稀释了（人名直查 0.88 → 拼串只有 0.63）。
    /// </para>
    /// </summary>
    private static System.Collections.Generic.List<string> ImageContextCandidates(string keyword)
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (var raw in new[] { _imgOwnText, _imgLastHeading, _imgRecentText })
        {
            var s = Cap(raw, ImgContextMaxChars);
            if (s.Length == 0) continue;
            if (string.Equals(s, keyword, StringComparison.Ordinal)) continue;
            if (list.Contains(s)) continue;
            list.Add(s);
        }
        return list;
    }

    /// <summary>截断到 cap 个字符（去空白）。</summary>
    private static string Cap(string? v, int cap)
    {
        var s = (v ?? "").Trim();
        return s.Length <= cap ? s : s.Substring(0, cap);
    }

    /// <summary>本图块的检索关键词（没有则空串）。</summary>
    private static string ImageQueryOf(JsonElement im)
        => (Str(im, "imageQuery") ?? Str(im, "image_query"))?.Trim() ?? "";

    private static void WarnImage(string message)
        => (_imgWarnings ??= new System.Collections.Generic.List<string>()).Add(message);

    private static string ImageWarningsJson()
        => _imgWarnings is { Count: > 0 }
            ? ",\"warnings\":[" + string.Join(",", _imgWarnings.Select(Js)) + "]"
            : "";

    /// <summary>
    /// 回显本稿实际用到的图（哪条检索词胜出、分数多少、哪张文件）。
    ///
    /// <para>
    /// 为何要回显：配图一旦配错，光看稿子无法判断“为什么是这张” —— PPT 侧早就这么做了，
    /// 这里同口径补上（“分数择优 + 上下文兜底”的效果能直接看见）。
    /// </para>
    /// </summary>
    private static string ImageUsedJson()
        => _imgUsed is { Count: > 0 }
            ? ",\"images\":[" + string.Join(",", _imgUsed.Select(h =>
                "{\"query\":" + Js(h.Query) + ",\"fileName\":" + Js(h.FileName)
                + ",\"score\":" + h.Score.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "}")) + "]"
            : "";

    private static void MarkImageUsed(ImgHit hit)
    {
        _imgUsed ??= new System.Collections.Generic.List<ImgHit>();
        if (!_imgUsed.Any(h => string.Equals(h.Path, hit.Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(h.Query, hit.Query, StringComparison.Ordinal)))
            _imgUsed.Add(hit);
    }

    /// <summary>给失败原因套一层：把“用的是哪条关键词”写进去（用户能据此改词重试）。</summary>
    private static string ImgWhy(string keyword, string reason)
        => string.IsNullOrEmpty(keyword) ? reason : "关键词“" + keyword + "”（" + reason + "）";

    /// <summary>
    /// 给一个图块配图：先拿模型给的<b>关键词</b>查图库；命中不够确定（低于 <see cref="ImgConfidentScore"/>）或没命中时，
    /// 再依次拿<b>上下文候选</b>（<see cref="ImageContextCandidates"/>）查，<b>谁分高用谁</b>；已够确定就不再查。
    ///
    /// <para>
    /// 为何要二次尝试：模型看不到图库里有什么（描述常常就是人名 / 产品名），而正文里往往写着那个名字 ——
    /// 实测用名字直查能到 0.88，用“员工 颁奖 舞台”只能蹭到 0.6 且是别的图。两次都没配上则两条原因合并报出。
    /// </para>
    ///
    /// <para>失败一律返回 null 并给出原因：配图失败不该让整份稿子出不来。</para>
    /// </summary>
    private static string? ResolveQueryImage(string query, out string? why)
    {
        why = null;
        var key = (query ?? "").Trim();
        if (key.Length == 0) { why = "关键词为空"; return null; }
        if (string.Equals(_imgSource, "network", StringComparison.OrdinalIgnoreCase))
        {
            why = ImgWhy(key, "已配置为不使用图库（imageSource=network），而本技能只从图库配图");
            return null;
        }
        if (string.IsNullOrWhiteSpace(_imgScopeId))
        {
            why = ImgWhy(key, "当前没有可用的图库（平台未注入检索范围）");
            return null;
        }

        var first = LibraryLookup(key, out var why1);
        var best = first;
        var ctxTried = new System.Collections.Generic.List<string>();
        string? whyCtx = null;
        if (first is null || first.Score < ImgConfidentScore)
        {
            foreach (var cand in ImageContextCandidates(key))
            {
                if (best is not null && best.Score >= ImgConfidentScore) break;   // 已经够确定：不必再查
                if (ctxTried.Count >= ImgMaxContextTries) break;                 // 时间预算：上下文最多再查几次
                ctxTried.Add(cand);
                var hit = LibraryLookup(cand, out var candWhy);
                if (hit is not null) { if (BetterHit(best, hit)) best = hit; }
                else if (whyCtx is null) whyCtx = candWhy;
            }
        }
        if (best is null)
        {
            // 两次都没配上：两条原因都写出来（只写关键词那条，会让人以为根本没试过上下文）
            var parts = new System.Collections.Generic.List<string>();
            if (why1 is not null) parts.Add("关键词“" + key + "”（" + why1 + "）");
            if (ctxTried.Count > 0) parts.Add("上下文“" + ctxTried[0] + "”（" + (whyCtx ?? "未找到") + "）");
            why = parts.Count > 0 ? string.Join("；", parts) : null;
        }
        else MarkImageUsed(best);
        return best?.Path;
    }

    /// <summary>两个候选谁更该用：分高者胜（<paramref name="alt"/> 为空表示不换）。</summary>
    private static bool BetterHit(ImgHit? cur, ImgHit? alt)
        => alt is not null && (cur is null || alt.Score > cur.Score);

    /// <summary>
    /// 查一次<b>团队图库</b>：回环 HTTP 回调 <c>/ag-ui/images/search</c>，拿回**服务器本地路径**与分数。
    ///
    /// <para>
    /// 技能与平台同进程，但拿不到平台的 DI 容器，所以走回环 HTTP：带上平台注入的句柄 + 自令牌
    /// （平台在该接口里已按句柄做过图库可读性鉴权）。带 <c>minScore</c> 与 <c>topK</c> 见常量注释。
    /// </para>
    /// </summary>
    private static ImgHit? LibraryLookup(string query, out string? why)
    {
        why = null;
        var key = (query ?? "").Trim();
        if (key.Length == 0) { why = "关键词为空"; return null; }
        _imgCache ??= new System.Collections.Generic.Dictionary<string, ImgHit?>(StringComparer.Ordinal);
        if (_imgCache.TryGetValue(key, out var cached)) return cached;

        var baseUrl = Environment.GetEnvironmentVariable(SelfBaseEnv);
        var token = Environment.GetEnvironmentVariable(SelfTokenEnv);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            why = ImgWhy(key, "未拿到平台回调地址（AGUI_SELF_BASE / AGUI_SELF_TOKEN 未注入）");
            return null;
        }

        ImgHit? hit = null;
        var body = "{\"query\":" + Js(key) + ",\"scopeHandle\":" + Js(_imgScopeId!) + ",\"topK\":3,\"minScore\":"
                 + ImgMinScore.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        var json = HttpPostJson(baseUrl!.TrimEnd('/') + "/ag-ui/images/search", body, token!, out var httpWhy);
        if (json is null) why = httpWhy ?? "平台未返回内容";
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("images", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var img in arr.EnumerateArray())
                    {
                        var p = Str(img, "path");
                        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
                        hit = new ImgHit
                        {
                            Path = p!,
                            // 回显用图库里的**原始文件名**（服务器存储名是 asset_xxx.png，对人没意义）
                            FileName = Str(img, "fileName") ?? System.IO.Path.GetFileName(p!),
                            Query = key,
                            Score = ScoreOf(img),
                        };
                        break;
                    }
                    if (hit is null) why = "图库里没有匹配的图片（门槛 " + ImgMinScore.ToString(System.Globalization.CultureInfo.InvariantCulture) + "）";
                }
                else why = "平台返回格式异常";
            }
            catch (Exception ex) { why = "平台返回解析失败：" + ex.Message; }
        }
        _imgCache[key] = hit;
        return hit;
    }

    /// <summary>读平台返回的相似度（缺失 / 非数字记 0：宁可当成“不确定”，就会走二次比较）。</summary>
    private static double ScoreOf(JsonElement img)
    {
        if (!img.TryGetProperty("score", out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
    }

    /// <summary>POST JSON 并读回响应体（技能入口是同步签名，故这里同步等待）。失败返回 null 并给出原因。</summary>
    private static string? HttpPostJson(string url, string body, string token, out string? why)
    {
        why = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(ImgSearchTimeoutSec) };
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            // 头部名就是常量本身：服务端 SelfApi.IsSelf 读的正是同名的请求头
            req.Headers.TryAddWithoutValidation(SelfTokenEnv, token);
            using var resp = Task.Run(() => http.SendAsync(req, HttpCompletionOption.ResponseContentRead))
                .GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) { why = "平台返回 HTTP " + (int)resp.StatusCode; return null; }
            return Task.Run(() => resp.Content.ReadAsStringAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex) { why = ex.GetType().Name + "：" + ex.Message; return null; }
    }

    // 从文件头读像素尺寸（仅 png / gif / bmp / jpeg）；读不到返回 (0,0)，由调用方按 4:3 估算。
    private static (int W, int H) TryReadPixelSize(byte[] b, string ext)
    {
        try
        {
            if (ext == "png" && b.Length > 24 && b[0] == 0x89 && b[1] == 0x50)
            {
                int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                int h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                return (w, h);
            }
            if (ext == "gif" && b.Length > 10 && b[0] == 0x47 && b[1] == 0x49)
                return (b[6] | (b[7] << 8), b[8] | (b[9] << 8));
            if (ext == "bmp" && b.Length > 26 && b[0] == 0x42 && b[1] == 0x4D)
            {
                int w = BitConverter.ToInt32(b, 18);
                int h = Math.Abs(BitConverter.ToInt32(b, 22));
                return (w, h);
            }
            if ((ext == "jpg" || ext == "jpeg") && b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
                return ReadJpegSize(b);
        }
        catch { /* 读不出就交给调用方估算 */ }
        return (0, 0);
    }

    private static (int W, int H) ReadJpegSize(byte[] b)
    {
        int i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            int marker = b[i + 1];
            if (marker == 0xD8 || marker == 0xD9 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            int len = (b[i + 2] << 8) | b[i + 3];
            // SOF0..SOF15（除 DHT=C4 / JPG=C8 / DAC=CC）
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                int h = (b[i + 5] << 8) | b[i + 6];
                int w = (b[i + 7] << 8) | b[i + 8];
                return (w, h);
            }
            i += 2 + len;
        }
        return (0, 0);
    }

    // ===== 表格：三线表（表头加粗 + 跨页重复表头）=====
    private static Table? BuildTable(JsonElement tb)
    {
        if (!tb.TryGetProperty("headers", out var hdr) || hdr.ValueKind != JsonValueKind.Array) return null;
        var headers = hdr.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        if (headers.Count == 0) return null;

        var table = new Table(
            new TableProperties(
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 12 },
                    new BottomBorder { Val = BorderValues.Single, Size = 12 },
                    new LeftBorder { Val = BorderValues.None },
                    new RightBorder { Val = BorderValues.None },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideVerticalBorder { Val = BorderValues.None })));

        var grid = new TableGrid();
        foreach (var _ in headers) grid.AppendChild(new GridColumn());
        table.AppendChild(grid);

        var headRow = new TableRow(new TableRowProperties(new TableHeader()));
        foreach (var h in headers) headRow.AppendChild(MakeCell(h, true));
        table.AppendChild(headRow);

        if (tb.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rows.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Array) continue;
                var row = new TableRow();
                foreach (var c in r.EnumerateArray())
                    row.AppendChild(MakeCell(c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ToString(), false));
                table.AppendChild(row);
            }
        }
        return table;
    }

    private static TableCell MakeCell(string text, bool bold)
    {
        var rpr = new RunProperties(
            new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = FontBody },
            new FontSize { Val = SizeSmall.ToString() });
        if (bold) rpr.AppendChild(new Bold());

        var para = new Paragraph(new ParagraphProperties(
            new Justification { Val = bold ? JustificationValues.Center : JustificationValues.Left },
            new SpacingBetweenLines { Before = "40", After = "40" }));
        para.AppendChild(new Run(rpr, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return new TableCell(para); // 每格必须至少一个 <w:p/>
    }

    // ===== 页面 =====

    /// <summary>
    /// 正文可用宽/高（EMU）。由**实际页面尺寸与页边距**算出，不要写死。
    /// 注意单位：PageWidth/页边距是 twip（1/20 pt），图片的 cx/cy 是 EMU，1 twip = 635 EMU。
    /// </summary>
    private static long TextWidthEmu => Math.Max(1, (PageWidth - MarginLeft - MarginRight) * 635L);
    private static long TextHeightEmu => Math.Max(1, (PageHeight - MarginTop - MarginBottom) * 635L);

    private static SectionProperties SectionProps(string? footerRefId)
    {
        var sect = new SectionProperties();
        if (!string.IsNullOrEmpty(footerRefId)) sect.AppendChild(new FooterReference { Type = HeaderFooterValues.Default, Id = footerRefId });
        sect.AppendChild(new PageSize { Width = (uint)PageWidth, Height = (uint)PageHeight, Orient = PageWidth > PageHeight ? PageOrientationValues.Landscape : PageOrientationValues.Portrait });
        sect.AppendChild(new PageMargin { Top = MarginTop, Bottom = MarginBottom, Left = (uint)MarginLeft, Right = (uint)MarginRight });
        return sect;
    }

    private static Run RunWith(string text, string eastAsiaFont, int halfPointSize, bool bold)
    {
        var rpr = new RunProperties(
            new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = eastAsiaFont },
            new FontSize { Val = halfPointSize.ToString() });
        if (bold) rpr.AppendChild(new Bold());
        return new Run(rpr, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    // ===== 工具 =====
    private static string ResolveOutputPath(string? requested, string title)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var full = Path.GetFullPath(Safe(requested));
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            if (!full.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) full += ".docx";
            return full;
        }
        // 缺省落盘：以标题为文件名（中文保留），落到默认输出目录；同名则追加序号避免覆盖
        var outDir = DefaultOutputDir();
        Directory.CreateDirectory(outDir);
        return UniquePath(outDir, SafeFileNameFromTitle(title));
    }

    /// <summary>默认输出目录：AGUI_DOCX_OUT 环境变量 > 用户主目录/agui-docx > 临时目录/agui-docx。</summary>
    private static string DefaultOutputDir()
    {
        var configured = Environment.GetEnvironmentVariable("AGUI_DOCX_OUT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var d = Path.GetFullPath(configured.Trim());
                Directory.CreateDirectory(d);
                return d;
            }
            catch { /* 配置路径不可用 → 回退默认 */ }
        }
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                var d = Path.Combine(home, "agui-docx");
                Directory.CreateDirectory(d);
                return d;
            }
        }
        catch { /* 无主目录 → 回退临时目录 */ }
        var tmp = Path.Combine(Path.GetTempPath(), "agui-docx");
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    /// <summary>
    /// 把标题转成安全的文件名（保留中文）：仅剔除文件系统非法字符与控制字符，
    /// 合并空白，限长 80 字符。与之前不同：不再把中文换成下划线，也不强加时间戳。
    /// </summary>
    private static string SafeFileNameFromTitle(string title)
    {
        var raw = Safe(title).Trim();
        var b = new StringBuilder();
        foreach (var c in raw)
        {
            if (char.IsControl(c)) continue;
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') { b.Append('_'); continue; }
            b.Append(c);
        }
        var name = b.ToString();
        // 合并连续空白为单个空格
        var sb = new StringBuilder();
        var lastSpace = false;
        foreach (var c in name)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace && lastSpace) continue;
            sb.Append(isSpace ? ' ' : c);
            lastSpace = isSpace;
        }
        name = sb.ToString().Trim().TrimEnd('.'); // Windows 不允许结尾点号
        if (name.Length == 0) name = "document";
        if (name.Length > 80) name = name.Substring(0, 80).TrimEnd();
        // 保留设备名兼容
        var upper = name.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL" || upper.StartsWith("COM") || upper.StartsWith("LPT"))
            name = "_" + name;
        return name;
    }

    /// <summary>同名时追加 _2 / _3 … 直到不冲突。</summary>
    private static string UniquePath(string dir, string baseName)
    {
        var candidate = Path.Combine(dir, baseName + ".docx");
        if (!File.Exists(candidate)) return candidate;
        for (int i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(dir, $"{baseName}_{i}.docx");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.docx");
    }

    private static string Safe(string s) => (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

    // 从模型输出里剥出 JSON（容忍代码围栏与前后说明文字）
    private static string ExtractJson(string s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return "{}";
        // 去掉代码围栏（围栏字符用 char code 构造，避免与本文件的模板字符串冲突）
        var fence = new string((char)96, 3);
        if (t.StartsWith(fence, StringComparison.Ordinal))
        {
            int nl = t.IndexOf('\n');
            if (nl > 0) t = t.Substring(nl + 1);
            int close = t.LastIndexOf(fence, StringComparison.Ordinal);
            if (close >= 0) t = t.Substring(0, close);
            t = t.Trim();
        }
        int first = t.IndexOf('{');
        int last = t.LastIndexOf('}');
        if (first >= 0 && last > first) return t.Substring(first, last - first + 1);
        return "{}";
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Js(string s)
    {
        var b = new StringBuilder("\"");
        foreach (var c in s ?? "")
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default:
                    if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                    else b.Append(c);
                    break;
            }
        }
        return b.Append('"').ToString();
    }
}
