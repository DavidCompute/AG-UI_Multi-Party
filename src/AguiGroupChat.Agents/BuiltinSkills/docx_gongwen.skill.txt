#r "nuget: DocumentFormat.OpenXml, 3.2.0"
#r "nuget: SixLabors.ImageSharp, 2.1.5"
#r "nuget: SixLabors.ImageSharp.Drawing, 1.0.0"

// ============================================================================
// docx_gongwen —— 公文（党政机关公文格式，参照 GB/T 9704-2012）
//
// 【何时使用】生成通知、通报、请示、批复、报告等公文正文时使用。三号仿宋正文、黑体层次标题、22pt 小标宋大标题、固定行距。
// 【可用内容块】heading / paragraph / numbered / bullets / table / image / chart / toc / pageBreak
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
//       { "pageBreak": true }
//     ]
//   }
//   返回 = { ok, scene, path, blocks, message }
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
using System.Text;
using System.Text.Json;
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
            return "{\"ok\":true,\"path\":" + Js(built.Path) + ",\"scene\":" + Js(SceneName)
                + ",\"blocks\":" + built.Blocks + ",\"message\":" + Js("已生成 Word 文档：" + built.Path) + "}";
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
    private static bool AppendBlock(MainDocumentPart main, Body body, JsonElement s)
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
        { "Microsoft YaHei", "SimHei", "SimSun", "Arial", "DejaVu Sans" };

    private static readonly object _fontLock = new object();
    private static SixLabors.Fonts.FontFamily? _fontFamily;

    /// <summary>取一个可用的无衬线字体（含 CJK 覆盖）；找不到则抛出可读错误。</summary>
    private static SixLabors.Fonts.Font Family(float size)
    {
        lock (_fontLock)
        {
            if (_fontFamily is null)
            {
                foreach (var name in FontCandidates)
                {
                    if (SixLabors.Fonts.SystemFonts.TryGet(name, out var f)) { _fontFamily = f; break; }
                }
                if (_fontFamily is null && SixLabors.Fonts.SystemFonts.Collection.Families.Any())
                    _fontFamily = SixLabors.Fonts.SystemFonts.Collection.Families.First();
                if (_fontFamily is null)
                    throw new InvalidOperationException("图表需要至少一种系统字体，但当前环境未发现可用字体。");
            }
            return _fontFamily.Value.CreateFont(size);
        }
    }

    private static byte[] RenderBar(int w, int h, string? title, string? yLabel, List<string> cats, List<(string Name, double[] Values)> series)
    {
        using var img = new Image<Rgba32>(w, h);
        int padL = 78, padR = 28, padT = title is null ? 36 : 64, padB = 64;
        double maxV = Math.Max(0.0001, series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max());
        img.Mutate(x =>
        {
            x.Fill(ImgColor.White);
            if (title is not null) x.DrawText(title, Family(24f), ImgColor.Black, new ImgPointF(padL, 22));

            // Y 轴刻度 + 网格
            int ticks = 4;
            for (int i = 0; i <= ticks; i++)
            {
                float y = h - padB - (float)((h - padT - padB) * i / (double)ticks);
                x.DrawLine(ImgColor.FromRgba(210, 210, 210, 255), 1f, new ImgPointF(padL, y), new ImgPointF(w - padR, y));
                var label = (maxV * i / ticks).ToString("0.##");
                x.DrawText(label, Family(13f), ImgColor.FromRgba(90, 90, 90, 255), new ImgPointF(6, y - 9));
            }
            x.DrawLine(ImgColor.Black, 1.6f, new ImgPointF(padL, padT), new ImgPointF(padL, h - padB));
            x.DrawLine(ImgColor.Black, 1.6f, new ImgPointF(padL, h - padB), new ImgPointF(w - padR, h - padB));
            if (!string.IsNullOrWhiteSpace(yLabel)) x.DrawText(yLabel!, Family(13f), ImgColor.FromRgba(90, 90, 90, 255), new ImgPointF(6, padT - 20));

            int nc = cats.Count;
            int ns = series.Count;
            double slot = (w - padL - padR) / (double)Math.Max(1, nc);
            double barW = Math.Max(4, slot * 0.72 / ns);

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
                // X 轴类别标签（居中于 slot；过长截断）
                var lab = cats[c].Length > 8 ? cats[c].Substring(0, 8) + "…" : cats[c];
                var tw = SixLabors.Fonts.TextMeasurer.MeasureBounds(lab, new SixLabors.Fonts.TextOptions(Family(13f))).Width;
                x.DrawText(lab, Family(13f), ImgColor.Black, new ImgPointF((float)(padL + slot * c + slot / 2 - tw / 2), h - padB + 8));
            }

            // 图例（多系列才显示）
            if (ns > 1)
            {
                float lx = padL;
                float ly = padT - 24;
                for (int s = 0; s < ns; s++)
                {
                    var nm = string.IsNullOrEmpty(series[s].Name) ? "系列" + (s + 1) : series[s].Name;
                    var color = ImgColor.ParseHex(Palette[s % Palette.Length]);
                    x.Fill(color, new ImgRect(lx, ly, 14, 14));
                    x.DrawText(nm, Family(13f), ImgColor.Black, new ImgPointF(lx + 19, ly - 2));
                    lx += 19 + SixLabors.Fonts.TextMeasurer.MeasureBounds(nm, new SixLabors.Fonts.TextOptions(Family(13f))).Width + 22;
                }
            }
        });
        using var outMs = new MemoryStream();
        img.SaveAsPng(outMs);
        return outMs.ToArray();
    }

    private static byte[] RenderLine(int w, int h, string? title, string? yLabel, List<string> cats, List<(string Name, double[] Values)> series)
    {
        using var img = new Image<Rgba32>(w, h);
        int padL = 78, padR = 28, padT = title is null ? 36 : 64, padB = 64;
        var all = series.SelectMany(s => s.Values).DefaultIfEmpty(0).ToList();
        double minV = Math.Min(0, all.Min());
        double maxV = Math.Max(0.0001, all.Max());
        if (maxV - minV < 0.0001) maxV = minV + 1;

        img.Mutate(x =>
        {
            x.Fill(ImgColor.White);
            if (title is not null) x.DrawText(title, Family(24f), ImgColor.Black, new ImgPointF(padL, 22));

            int ticks = 4;
            for (int i = 0; i <= ticks; i++)
            {
                float y = h - padB - (float)((h - padT - padB) * i / (double)ticks);
                x.DrawLine(ImgColor.FromRgba(210, 210, 210, 255), 1f, new ImgPointF(padL, y), new ImgPointF(w - padR, y));
                var label = (minV + (maxV - minV) * i / ticks).ToString("0.##");
                x.DrawText(label, Family(13f), ImgColor.FromRgba(90, 90, 90, 255), new ImgPointF(6, y - 9));
            }
            x.DrawLine(ImgColor.Black, 1.6f, new ImgPointF(padL, padT), new ImgPointF(padL, h - padB));
            x.DrawLine(ImgColor.Black, 1.6f, new ImgPointF(padL, h - padB), new ImgPointF(w - padR, h - padB));
            if (!string.IsNullOrWhiteSpace(yLabel)) x.DrawText(yLabel!, Family(13f), ImgColor.FromRgba(90, 90, 90, 255), new ImgPointF(6, padT - 20));

            int nc = cats.Count;
            double slot = (w - padL - padR) / (double)Math.Max(1, nc - 1 == 0 ? 1 : nc - 1);
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
                var lab = cats[c].Length > 8 ? cats[c].Substring(0, 8) + "…" : cats[c];
                float px = nc == 1 ? padL + (w - padL - padR) / 2f : (float)(padL + slot * c);
                var tw = SixLabors.Fonts.TextMeasurer.MeasureBounds(lab, new SixLabors.Fonts.TextOptions(Family(13f))).Width;
                x.DrawText(lab, Family(13f), ImgColor.Black, new ImgPointF(px - tw / 2, h - padB + 8));
            }
            if (series.Count > 1)
            {
                float lx = padL; float ly = padT - 24;
                for (int s = 0; s < series.Count; s++)
                {
                    var nm = string.IsNullOrEmpty(series[s].Name) ? "系列" + (s + 1) : series[s].Name;
                    x.Fill(ImgColor.ParseHex(Palette[s % Palette.Length]), new ImgRect(lx, ly, 14, 14));
                    x.DrawText(nm, Family(13f), ImgColor.Black, new ImgPointF(lx + 19, ly - 2));
                    lx += 19 + SixLabors.Fonts.TextMeasurer.MeasureBounds(nm, new SixLabors.Fonts.TextOptions(Family(13f))).Width + 22;
                }
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
            if (title is not null) x.DrawText(title, Family(24f), ImgColor.Black, new ImgPointF(24, 18));

            float cy = h / 2f + 10, cx = w * 0.34f;
            float r = Math.Min(w * 0.30f, h * 0.40f);
            // 角度用度（PathBuilder.AddArc 的 startAngle/sweepAngle 为度）
            float startDeg = -90f;
            for (int i = 0; i < values.Length; i++)
            {
                double v = values[i] <= 0 ? 0 : values[i];
                if (v == 0) continue;
                float sweepDeg = (float)(360.0 * (v / total));
                var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
                pb.AddArc(new ImgPointF(cx, cy), r, r, 0f, startDeg, sweepDeg);
                x.Fill(ImgColor.ParseHex(Palette[i % Palette.Length]), pb.Build());
                startDeg += sweepDeg;
            }
            // 外圈描边
            x.Draw(ImgColor.Black, 1f, new ImgEllipse(cx, cy, r));

            // 图例
            float lx = w * 0.68f, ly = h * 0.30f;
            for (int i = 0; i < cats.Count; i++)
            {
                double v = i < values.Length ? Math.Max(0, values[i]) : 0;
                var pct = (v / total * 100).ToString("0.#") + "%";
                var nm = cats[i] + "  " + pct;
                if (nm.Length > 22) nm = nm.Substring(0, 22) + "…";
                x.Fill(ImgColor.ParseHex(Palette[i % Palette.Length]), new ImgRect(lx, ly, 14, 14));
                x.DrawText(nm, Family(14f), ImgColor.Black, new ImgPointF(lx + 20, ly - 3));
                ly += 24;
            }
        });
        using var outMs = new MemoryStream();
        img.SaveAsPng(outMs);
        return outMs.ToArray();
    }

    // ===== 图片（内联）=====
    // 从本地文件读入并嵌入；支持 widthCm 控制宽度（按原图比例缩放）。
    // 支持的格式：png / jpg / jpeg / gif / bmp / tiff。
    private static Paragraph? BuildImagePara(MainDocumentPart main, JsonElement im)
    {
        var file = Str(im, "path");
        if (string.IsNullOrWhiteSpace(file)) return null;
        var full = Path.GetFullPath(Safe(file));
        if (!File.Exists(full)) throw new FileNotFoundException("图片文件不存在：" + full);

        var ext = Path.GetExtension(full).ToLowerInvariant().TrimStart('.');
        var contentType = ext switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "tif" or "tiff" => "image/tiff",
            _ => throw new NotSupportedException("不支持的图片格式：." + ext + "（支持 png/jpg/jpeg/gif/bmp/tiff）"),
        };

        var bytes = File.ReadAllBytes(full);
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

        var (pxW, pxH) = TryReadPixelSize(bytes, ext);
        double widthCm = 14.0; // 默认宽（A4 正文宽约 15.9cm）
        if (im.TryGetProperty("widthCm", out var wc) && wc.ValueKind == JsonValueKind.Number) widthCm = wc.GetDouble();
        else if (im.TryGetProperty("widthPercent", out var wp) && wp.ValueKind == JsonValueKind.Number)
            widthCm = Math.Max(1, Math.Min(100, wp.GetDouble())) / 100.0 * 15.9;
        widthCm = Math.Max(1.0, Math.Min(24.0, widthCm));

        // EMU: 1 cm = 360000 EMU
        long cx = (long)Math.Round(widthCm * 360000.0);
        long cy = pxW > 0 && pxH > 0 ? (long)Math.Round(cx * (double)pxH / pxW) : (long)Math.Round(cx * 0.75);

        return ImageDrawingParagraph(relId, cx, cy, Str(im, "caption"), Str(im, "alt"));
    }

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
