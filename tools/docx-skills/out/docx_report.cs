#r "nuget: DocumentFormat.OpenXml, 3.2.0"

// ============================================================================
// docx_report —— 工作报告 / 总结 / 方案（通用书面报告）
//
// 【何时使用】生成工作总结、调研报告、实施方案、情况汇报等需要分章节和数据的文档时使用。宋体正文、黑体标题、首行缩进、支持数据表格。
// 【可用内容块】heading / paragraph / bullets / numbered / quote / table / pageBreak
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
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

public class Skill
{
    // ===== 场景排版参数（由生成器注入）=====
    private const string SceneName = "report";
    private const string FontTitle = "黑体";
    private const string FontHeading = "黑体";
    private const string FontBody = "宋体";
    private const int SizeTitle = 36;
    private const int SizeH1 = 30;
    private const int SizeH2 = 26;
    private const int SizeH3 = 24;
    private const int SizeBody = 24;
    private const int SizeSmall = 20;
    private const int LineBody = 400;
    private const bool TitleBold = true;
    private const bool HeadingBold = true;
    private const bool BodyIndent = true;
    private const bool UsePageNumbers = true;
    private const int MarginTop = 1440;
    private const int MarginBottom = 1440;
    private const int MarginLeft = 1440;
    private const int MarginRight = 1440;
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
                    if (AppendBlock(body, s)) blocks++;
                }
            }

            // 本场景不自动添加结尾语
            body.AppendChild(SectionProps(footerRefId));
            main.Document.Save();
        }
        return (blocks, path);
    }

    // ===== 内容块派发（各场景可用块一致，保证行为可预期）=====
    private static bool AppendBlock(Body body, JsonElement s)
    {
        if (s.TryGetProperty("pageBreak", out var pb) && pb.ValueKind == JsonValueKind.True)
        {
            body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
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
                new FontSize { Val = size.ToString() }
                ,
                new Bold()))
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
        var tmp = Path.Combine(Path.GetTempPath(), "agui-docx");
        Directory.CreateDirectory(tmp);
        return Path.Combine(tmp, Slug(title) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".docx");
    }

    private static string Slug(string s)
    {
        var b = new StringBuilder();
        foreach (var c in s ?? "")
        {
            if (char.IsLetterOrDigit(c)) b.Append(c);
            else if (b.Length > 0 && b[b.Length - 1] != '_') b.Append('_');
            if (b.Length >= 32) break;
        }
        var r = b.ToString().Trim('_');
        return r.Length == 0 ? "document" : r;
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
