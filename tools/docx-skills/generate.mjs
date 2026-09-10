/**
 * docx 场景技能生成器
 * ---------------------------------------------------------------------------
 * 背景：本平台的技能（AgentSkillDefinition.Body）是**单个编译单元**——
 *   - 不能 include 共享源码；
 *   - #r 只认 NuGet 包，不认本地文件。
 * 因此「按场景拆分」若各写一份完整 OpenXML 管线，会产生 3 份重复代码、
 * 3 倍维护成本与 3 倍缺陷面。
 *
 * 做法：把 OpenXML 管线（styles/段落/表格/落盘/JSON 解析）作为**共享内核**只写一次，
 * 再由「场景配置」（字体、字号、行距、页边距、标题格式、可用块类型）参数化，
 * 生成三份**各自自包含**的技能源码。
 *
 * 用法：
 *   node tools/docx-skills/generate.mjs
 * 产物：
 *   tools/docx-skills/out/docx_gongwen.cs   公文（GB/T 9704 风格）
 *   tools/docx-skills/out/docx_notice.cs    通知 / 公告（简洁，单页优先）
 *   tools/docx-skills/out/docx_report.cs    工作报告 / 总结（通用学术报告风格）
 */

import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const outDir = join(here, "out");

// ---------------------------------------------------------------------------
// 共享内核：所有场景技能共用的 OpenXML 管线。
// 仅通过 `{{占位符}}` 接受场景差异，其余逻辑完全一致（保证只验证一次）。
// ---------------------------------------------------------------------------
const CORE = String.raw`
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
    private const string SceneName = "{{sceneName}}";
    private const string FontTitle = "{{fontTitle}}";
    private const string FontHeading = "{{fontHeading}}";
    private const string FontBody = "{{fontBody}}";
    private const int SizeTitle = {{sizeTitle}};
    private const int SizeH1 = {{sizeH1}};
    private const int SizeH2 = {{sizeH2}};
    private const int SizeH3 = {{sizeH3}};
    private const int SizeBody = {{sizeBody}};
    private const int SizeSmall = {{sizeSmall}};
    private const int LineBody = {{lineBody}};
    private const bool TitleBold = {{titleBold}};
    private const bool HeadingBold = {{headingBold}};
    private const bool BodyIndent = {{bodyIndent}};
    private const bool UsePageNumbers = {{usePageNumbers}};
    private const int MarginTop = {{marginTop}};
    private const int MarginBottom = {{marginBottom}};
    private const int MarginLeft = {{marginLeft}};
    private const int MarginRight = {{marginRight}};
    private const int PageWidth = {{pageWidth}};
    private const int PageHeight = {{pageHeight}};

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
            {{headerFooter}}
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

            {{closing}}
            body.AppendChild(SectionProps({{footerArg}}));
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
                new FontSize { Val = size.ToString() }{{headingStyleBold}}))
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
                new Color { Val = "808080" },
                new Italic()),
            new Text("（目录将在 Word 中更新域后生成：全选后按 F9）") { Space = SpaceProcessingModeValues.Preserve });
        var r5 = new Run(new FieldChar { FieldCharType = FieldCharValues.End });

        p.AppendChild(r1); p.AppendChild(r2); p.AppendChild(r3); p.AppendChild(r4); p.AppendChild(r5);
        return p;
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

        var drawing = new Drawing(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = cx, Cy = cy },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = NextDrawingId(), Name = "Picture " + relId },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.NonVisualGraphicFrameDrawingProperties(
                    new DocumentFormat.OpenXml.Drawing.GraphicFrameLocks { NoChangeAspect = true }),
                new DocumentFormat.OpenXml.Drawing.Graphic(
                    new DocumentFormat.OpenXml.Drawing.GraphicData(
                        new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                            new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties(
                                new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties { Id = 0U, Name = Path.GetFileName(full) },
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

        var caption = Str(im, "caption");
        var ppr = new ParagraphProperties(new Justification { Val = JustificationValues.Center });
        var p = new Paragraph(ppr);
        p.AppendChild(new Run(drawing));
        if (!string.IsNullOrWhiteSpace(caption))
        {
            p.AppendChild(new Run(new Break()));
            p.AppendChild(new Run(new RunProperties(
                    new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman", EastAsia = FontBody },
                    new FontSize { Val = SizeSmall.ToString() }),
                new Text(Safe(caption)) { Space = SpaceProcessingModeValues.Preserve }));
        }
        if (Str(im, "alt") is { } alt && !string.IsNullOrWhiteSpace(alt))
        {
            if (p.Elements<Run>().FirstOrDefault()?.GetFirstChild<Drawing>() is { } dr
                && dr.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline>() is { } inl
                && inl.GetFirstChild<DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties>() is { } dp)
                dp.Description = Safe(alt); // 可访问性：图片替代文本
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
    private static SectionProperties SectionProps({{footerParam}})
    {
        var sect = new SectionProperties();
        {{footerRef}}
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
`;

// ---------------------------------------------------------------------------
// 场景定义：只描述差异，不重复管线。
// ---------------------------------------------------------------------------
const SCENES = [
  {
    file: "docx_gongwen.cs",
    sceneName: "gongwen",
    closing: null, // 公文结尾语差异大（特此通知/特此报告/当否请示），交给调用方在 sections 里写
    title: "公文（党政机关公文格式，参照 GB/T 9704-2012）",
    when: "生成通知、通报、请示、批复、报告等公文正文时使用。三号仿宋正文、黑体层次标题、22pt 小标宋大标题、固定行距。",
    blocks: "heading / paragraph / numbered / bullets / table / image / toc / pageBreak",
    // GB/T 9704：A4，上37mm 下35mm 左28mm 右26mm（约值）；正文三号(16pt)仿宋；固定行距 28pt
    cfg: {
      fontTitle: "方正小标宋简体", fontHeading: "黑体", fontBody: "仿宋_GB2312",
      sizeTitle: 44, sizeH1: 32, sizeH2: 32, sizeH3: 32, sizeBody: 32, sizeSmall: 28,
      lineBody: 560, titleBold: false, headingBold: false, bodyIndent: true,
      usePageNumbers: true,
      marginTop: 2098, marginBottom: 1984, marginLeft: 1588, marginRight: 1474,
      pageWidth: 11906, pageHeight: 16838,
    },
  },
  {
    file: "docx_notice.cs",
    sceneName: "notice",
    title: "通知 / 公告 / 说明（简洁单页优先）",
    when: "生成对外通知、公告、事项说明、操作指引等短文档时使用。标题醒目、正文不缩进、层次精简、优先单页呈现。",
    blocks: "heading / paragraph / bullets / numbered / quote / table / image / toc / pageBreak",
    cfg: {
      fontTitle: "微软雅黑", fontHeading: "微软雅黑", fontBody: "宋体",
      sizeTitle: 40, sizeH1: 30, sizeH2: 26, sizeH3: 24, sizeBody: 24, sizeSmall: 20,
      lineBody: 360, titleBold: true, headingBold: true, bodyIndent: false,
      usePageNumbers: false,
      marginTop: 1440, marginBottom: 1440, marginLeft: 1440, marginRight: 1440,
      pageWidth: 11906, pageHeight: 16838,
    },
  },
  {
    file: "docx_report.cs",
    sceneName: "report",
    title: "工作报告 / 总结 / 方案（通用书面报告）",
    when: "生成工作总结、调研报告、实施方案、情况汇报等需要分章节和数据的文档时使用。宋体正文、黑体标题、首行缩进、支持数据表格。",
    blocks: "heading / paragraph / bullets / numbered / quote / table / image / toc / pageBreak",
    cfg: {
      fontTitle: "黑体", fontHeading: "黑体", fontBody: "宋体",
      sizeTitle: 36, sizeH1: 30, sizeH2: 26, sizeH3: 24, sizeBody: 24, sizeSmall: 20,
      lineBody: 400, titleBold: true, headingBold: true, bodyIndent: true,
      usePageNumbers: true,
      marginTop: 1440, marginBottom: 1440, marginLeft: 1440, marginRight: 1440,
      pageWidth: 11906, pageHeight: 16838,
    },
  },
];

function render(scene) {
  const c = scene.cfg;

  // 页码：创建页脚部件并在节属性里引用（FooterReference 必须是 sectPr 的子元素）
  const headerFooter = c.usePageNumbers
    ? `// 页脚居中页码（PAGE 域；Word 打开即显示）
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
`
    : `// 本场景不启用页码\n            footerRefId = null;\n`;

  // 场景专属结尾（公文常需「特此通知」类收尾；其它场景不加）
  const closing = scene.closing
    ? `body.AppendChild(BodyPara(${JSON.stringify(scene.closing)}));\n            blocks++;`
    : `// 本场景不自动添加结尾语`;

  const footerParam = c.usePageNumbers ? "string? footerRefId" : "string? footerRefId";
  const footerRef = c.usePageNumbers
    ? "if (!string.IsNullOrEmpty(footerRefId)) sect.AppendChild(new FooterReference { Type = HeaderFooterValues.Default, Id = footerRefId });"
    : "if (false && footerRefId != null) { /* 未启用页码 */ }";
  const footerArg = "footerRefId";

  let src = CORE
    .replaceAll("{{sceneName}}", scene.sceneName)
    .replaceAll("{{fontTitle}}", c.fontTitle)
    .replaceAll("{{fontHeading}}", c.fontHeading)
    .replaceAll("{{fontBody}}", c.fontBody)
    .replaceAll("{{sizeTitle}}", String(c.sizeTitle))
    .replaceAll("{{sizeH1}}", String(c.sizeH1))
    .replaceAll("{{sizeH2}}", String(c.sizeH2))
    .replaceAll("{{sizeH3}}", String(c.sizeH3))
    .replaceAll("{{sizeBody}}", String(c.sizeBody))
    .replaceAll("{{sizeSmall}}", String(c.sizeSmall))
    .replaceAll("{{lineBody}}", String(c.lineBody))
    .replaceAll("{{titleBold}}", c.titleBold ? "true" : "false")
    .replaceAll("{{headingBold}}", c.headingBold ? "true" : "false")
    .replaceAll("{{bodyIndent}}", c.bodyIndent ? "true" : "false")
    .replaceAll("{{usePageNumbers}}", c.usePageNumbers ? "true" : "false")
    .replaceAll("{{marginTop}}", String(c.marginTop))
    .replaceAll("{{marginBottom}}", String(c.marginBottom))
    .replaceAll("{{marginLeft}}", String(c.marginLeft))
    .replaceAll("{{marginRight}}", String(c.marginRight))
    .replaceAll("{{pageWidth}}", String(c.pageWidth))
    .replaceAll("{{pageHeight}}", String(c.pageHeight))
    .replaceAll("{{headingStyleBold}}", c.headingBold ? "\n                ,\n                new Bold()" : "")
    .replaceAll("{{headerFooter}}", headerFooter)
    .replaceAll("{{closing}}", closing)
    .replaceAll("{{footerParam}}", footerParam)
    .replaceAll("{{footerRef}}", footerRef)
    .replaceAll("{{footerArg}}", footerArg);

  const banner = `#r "nuget: DocumentFormat.OpenXml, 3.2.0"

// ============================================================================
// ${scene.file.replace(".cs", "")} —— ${scene.title}
//
// 【何时使用】${scene.when}
// 【可用内容块】${scene.blocks}
//
// 入口：public static string Run(string input) -> JSON
//   input = {
//     "title": "标题", "subtitle": "副标题(可选)", "author": "单位/作者(可选)",
//     "date": "日期(可选)", "outputPath": "D:\\\\out\\\\x.docx(可选)",
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

`;

  return banner + src.replace(/^\s*\n/, "");
}

mkdirSync(outDir, { recursive: true });
for (const scene of SCENES) {
  const src = render(scene);
  writeFileSync(join(outDir, scene.file), src, "utf8");
  console.log(`generated ${scene.file} (${src.length} chars)`);
}
