#r "nuget: DocumentFormat.OpenXml, 3.2.0"

// ============================================================================
// docx_report —— 生成规范排版的 Word 文档（.docx）
// 入口：public static string Run(string input)
//   input = JSON，形如：
//     {
//       "title": "关于XX工作的报告",
//       "subtitle": "2026 年度",              // 可选
//       "author": "综合办公室",                // 可选
//       "outputPath": "D:\\out\\report.docx",  // 可选；留空则写入临时目录
//       "layout": "gongwen",                   // 可选：gongwen | report | plain
//       "sections": [
//         { "heading": "一、工作背景", "level": 1 },
//         { "paragraph": "今年以来……" },
//         { "bullets": ["要点一", "要点二"] },
//         { "table": { "headers": ["项目","金额"], "rows": [["A","100"]] } },
//         { "pageBreak": true }
//       ]
//     }
// 返回：JSON（{ ok, path, message, blocks }）供模型据实回答。
// 说明：
//   1) 所有排版参数内联在本文件常量区（本平台技能暂不支持携带伴生资产文件）。
//   2) 【重要】平台预置的公共 using 不含 System.IO（见 DotnetSkillHost.Preamble）——
//      用到 Path / Directory / File 必须自行写 using System.IO; 否则报 CS0103。
//   3) 【重要】#r 里的版本号是「提示」而非强制：实测写 3.2.0 实际还原到 3.5.1（解析器取最新可用）。
// ============================================================================

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

public class Skill
{
    // ---- 排版常量（可调；对应 GB/T 9704 公文与通用报告两套）----
    private const string FontCnGongwen = "方正小标宋简体"; // 公文大标题
    private const string FontCnBody = "仿宋_GB2312";      // 公文正文
    private const string FontCnHei = "黑体";              // 公文一级标题
    private const string FontDbBody = "宋体";             // 报告正文
    private const string FontDbHeading = "黑体";          // 报告标题
    private const int SizeTitle = 44;    // 22pt = 22*2
    private const int SizeH1 = 32;       // 16pt
    private const int SizeBody = 32;     // 16pt（公文三号）
    private const int SizeReportBody = 24; // 12pt
    private const int LineBodyGongwen = 560; // 28pt 固定行距（DXA 二十分之一磅）
    private const int LineBodyReport = 360;  // 18pt

    public static string Run(string input)
    {
        try
        {
            var built = Build(input ?? "");
            return "{\"ok\":true,\"path\":" + Js(built.Path) + ",\"message\":" + Js("已生成 Word 文档：" + built.Path) + ",\"blocks\":" + built.Blocks + "}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"message\":" + Js("生成失败：" + ex.GetType().Name + "：" + ex.Message) + "}";
        }
    }

    // ---- 主流程：解析请求 → 建文档 → 落盘，返回（块数, 路径）----
    private static (int Blocks, string Path) Build(string input)
    {
        using var reqDoc = JsonDocument.Parse(ExtractJson(input));
        var root = reqDoc.RootElement;

        var title = Str(root, "title") ?? "未命名文档";
        var subtitle = Str(root, "subtitle");
        var author = Str(root, "author");
        var layout = (Str(root, "layout") ?? "report").ToLowerInvariant();
        bool gongwen = layout == "gongwen";
        bool plain = layout == "plain";

        var path = Str(root, "outputPath");
        if (string.IsNullOrWhiteSpace(path))
        {
            var dir = Path.Combine(Path.GetTempPath(), "agui-docx");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "docx_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".docx");
        }
        else
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        }

        int blocks = 0;
        using (var wd = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = wd.AddMainDocumentPart();
            var body = new Body();

            main.Document = new Document(body);
            InstallDefaults(main, gongwen);

            if (!plain)
            {
                body.AppendChild(TitlePara(title, gongwen, plain));
                if (!string.IsNullOrWhiteSpace(subtitle)) body.AppendChild(SubTitlePara(subtitle!, gongwen));
                if (!string.IsNullOrWhiteSpace(author)) body.AppendChild(AuthorPara(author!, gongwen));
                blocks += 3;
            }
            else
            {
                body.AppendChild(TitlePara(title, false, true));
                blocks++;
            }

            if (root.TryGetProperty("sections", out var secs) && secs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in secs.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;

                    if (s.TryGetProperty("pageBreak", out var pb) && pb.ValueKind == JsonValueKind.True)
                    {
                        body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                        blocks++;
                        continue;
                    }
                    var heading = Str(s, "heading");
                    if (!string.IsNullOrWhiteSpace(heading))
                    {
                        int lvl = 1;
                        if (s.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number) lvl = lv.GetInt32();
                        body.AppendChild(HeadingPara(heading!, lvl, gongwen));
                        blocks++;
                        continue;
                    }
                    var para = Str(s, "paragraph");
                    if (!string.IsNullOrWhiteSpace(para))
                    {
                        body.AppendChild(BodyPara(para!, gongwen));
                        blocks++;
                        continue;
                    }
                    if (s.TryGetProperty("bullets", out var bl) && bl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var b in bl.EnumerateArray())
                        {
                            var txt = b.GetString();
                            if (!string.IsNullOrWhiteSpace(txt))
                            {
                                body.AppendChild(BulletPara(txt!, gongwen));
                                blocks++;
                            }
                        }
                        continue;
                    }
                    if (s.TryGetProperty("table", out var tb) && tb.ValueKind == JsonValueKind.Object)
                    {
                        var t = BuildTable(tb, gongwen);
                        if (t != null) { body.AppendChild(t); blocks++; }
                        continue;
                    }
                }
            }

            // 节属性必须是 body 的最后一个子元素
            body.AppendChild(SectionProps(gongwen));
            main.Document.Save();
        }
        return (blocks, path);
    }

    // ---- 文档默认（styles.xml）：docDefaults + 基础样式，段落只引用 pStyle ----
    private static void InstallDefaults(MainDocumentPart main, bool gongwen)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();
        var cnBody = gongwen ? FontCnBody : FontDbBody;
        int bodySize = gongwen ? SizeBody : SizeReportBody;
        int line = gongwen ? LineBodyGongwen : LineBodyReport;

        styles.AppendChild(new DocDefaults(
            new RunPropertiesDefault(
                new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = cnBody },
                    new FontSize { Val = bodySize.ToString() },
                    new FontSizeComplexScript { Val = bodySize.ToString() })),
            new ParagraphPropertiesDefault(
                new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { Line = line.ToString(), LineRule = LineSpacingRuleValues.Exact },
                    new Justification { Val = JustificationValues.Both }))));

        styles.AppendChild(new Style(
            new StyleName { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { Line = line.ToString(), LineRule = LineSpacingRuleValues.Exact }),
            new StyleRunProperties(
                new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = cnBody },
                new FontSize { Val = bodySize.ToString() }))
        { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });

        // 标题样式必须带 OutlineLevel，否则 Word 导航窗格 / TOC 认不出
        AddHeading(styles, "Heading1", "heading 1", gongwen ? FontCnHei : FontDbHeading, SizeH1, 0);
        AddHeading(styles, "Heading2", "heading 2", gongwen ? FontCnHei : FontDbHeading, 28, 1);
        AddHeading(styles, "Heading3", "heading 3", gongwen ? FontCnHei : FontDbHeading, 26, 2);

        part.Styles = styles;
        part.Styles.Save();
    }

    private static void AddHeading(Styles styles, string id, string name, string font, int size, int outline)
    {
        styles.AppendChild(new Style(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleParagraphProperties(
                new KeepNext(),
                new OutlineLevel { Val = outline },
                new SpacingBetweenLines { Before = "240", After = "120", Line = LineBodyReport.ToString(), LineRule = LineSpacingRuleValues.Exact },
                new Justification { Val = JustificationValues.Left }),
            new StyleRunProperties(
                new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = font },
                new Bold(),
                new FontSize { Val = size.ToString() }))
        { Type = StyleValues.Paragraph, StyleId = id, Default = false });
    }

    // ---- 各类段落 ----
    private static Paragraph TitlePara(string text, bool gongwen, bool plain)
    {
        var p = new Paragraph(new ParagraphProperties(
            new Justification { Val = JustificationValues.Center },
            new SpacingBetweenLines { After = "240", Line = "600", LineRule = LineSpacingRuleValues.Exact }));
        p.AppendChild(RunWith(text, gongwen ? FontCnGongwen : FontDbHeading, gongwen ? SizeTitle : 36, true));
        return p;
    }

    private static Paragraph SubTitlePara(string text, bool gongwen)
    {
        var p = new Paragraph(new ParagraphProperties(
            new Justification { Val = JustificationValues.Center },
            new SpacingBetweenLines { After = "120" }));
        p.AppendChild(RunWith(text, gongwen ? FontCnHei : FontDbBody, 28, false));
        return p;
    }

    private static Paragraph AuthorPara(string text, bool gongwen)
    {
        var p = new Paragraph(new ParagraphProperties(
            new Justification { Val = JustificationValues.Center },
            new SpacingBetweenLines { After = "360" }));
        p.AppendChild(RunWith(text, gongwen ? FontCnBody : FontDbBody, 24, false));
        return p;
    }

    private static Paragraph HeadingPara(string text, int level, bool gongwen)
    {
        var lv = Math.Max(1, Math.Min(3, level));
        var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading" + lv }));
        p.AppendChild(RunWith(text, gongwen ? FontCnHei : FontDbHeading, lv == 1 ? SizeH1 : lv == 2 ? 28 : 26, true));
        return p;
    }

    private static Paragraph BodyPara(string text, bool gongwen)
    {
        var p = new Paragraph(new ParagraphProperties(
            new Indentation { FirstLineChars = 200 }, // 首行缩进 2 字符（东亚版式随字号缩放）
            new SpacingBetweenLines { Line = (gongwen ? LineBodyGongwen : LineBodyReport).ToString(), LineRule = LineSpacingRuleValues.Exact },
            new Justification { Val = JustificationValues.Both }));
        p.AppendChild(RunWith(text, gongwen ? FontCnBody : FontDbBody, gongwen ? SizeBody : SizeReportBody, false));
        return p;
    }

    private static Paragraph BulletPara(string text, bool gongwen)
    {
        var p = new Paragraph(new ParagraphProperties(
            new Indentation { LeftChars = 200, HangingChars = 100 },
            new SpacingBetweenLines { Line = (gongwen ? LineBodyGongwen : LineBodyReport).ToString(), LineRule = LineSpacingRuleValues.Exact }));
        p.AppendChild(RunWith("• " + text, gongwen ? FontCnBody : FontDbBody, gongwen ? SizeBody : SizeReportBody, false));
        return p;
    }

    // ---- 表格：三线表风格，表头加粗 + 跨页重复 ----
    private static Table? BuildTable(JsonElement tb, bool gongwen)
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
        foreach (var h in headers) headRow.AppendChild(MakeCell(h, true, gongwen));
        table.AppendChild(headRow);

        if (tb.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rows.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Array) continue;
                var row = new TableRow();
                foreach (var c in r.EnumerateArray())
                    row.AppendChild(MakeCell(c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ToString(), false, gongwen));
                table.AppendChild(row);
            }
        }
        return table;
    }

    private static TableCell MakeCell(string text, bool bold, bool gongwen)
    {
        var font = gongwen ? FontCnBody : FontDbBody;
        int size = gongwen ? SizeBody - 6 : SizeReportBody - 2; // 表格内略小
        var rpr = new RunProperties(
            new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = font },
            new FontSize { Val = size.ToString() });
        if (bold) rpr.AppendChild(new Bold());

        var para = new Paragraph(new ParagraphProperties(
            new Justification { Val = bold ? JustificationValues.Center : JustificationValues.Left },
            new SpacingBetweenLines { Before = "40", After = "40" }));
        para.AppendChild(new Run(rpr, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

        return new TableCell(para); // 每格必须至少一个 <w:p/>
    }

    private static SectionProperties SectionProps(bool gongwen)
    {
        uint lr = gongwen ? 1474u : 1440u; // 公文左右 2.6cm≈1474 DXA
        uint tb = gongwen ? 2098u : 1440u; // 公文上下 3.7cm≈2098 DXA
        return new SectionProperties(
            new PageSize { Width = gongwen ? 11906u : 12240u, Height = gongwen ? 16838u : 15840u }, // A4 / Letter
            new PageMargin { Top = (int)tb, Bottom = (int)tb, Left = lr, Right = lr });
    }

    private static Run RunWith(string text, string eastAsiaFont, int halfPointSize, bool bold)
    {
        var rpr = new RunProperties(
            new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = eastAsiaFont },
            new FontSize { Val = halfPointSize.ToString() },
            new FontSizeComplexScript { Val = halfPointSize.ToString() });
        if (bold) rpr.AppendChild(new Bold());
        return new Run(rpr, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    // ---- 工具：从模型给的文本里剥出 JSON（可能带 ```json 围栏或前后说明）----
    private static string ExtractJson(string s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return "{}";
        int fence = t.IndexOf("```");
        if (fence >= 0)
        {
            int nl = t.IndexOf('\n', fence);
            int close = nl > 0 ? t.IndexOf("```", nl + 1) : -1;
            if (nl > 0 && close > nl) t = t.Substring(nl + 1, close - nl - 1).Trim();
        }
        int first = t.IndexOf('{');
        int last = t.LastIndexOf('}');
        if (first >= 0 && last > first) return t.Substring(first, last - first + 1);
        return "{}";
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // 极简 JSON 字符串转义（仅用于返回值）
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
