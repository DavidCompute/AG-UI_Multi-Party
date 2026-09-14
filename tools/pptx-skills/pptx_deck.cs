#r "nuget: DocumentFormat.OpenXml, 3.2.0"
#r "nuget: SixLabors.ImageSharp, 2.1.5"
#r "nuget: SixLabors.ImageSharp.Drawing, 1.0.0"

// ============================================================================
// pptx_deck —— 演示文稿（.pptx）生成
//
// 【何时使用】用户需要一份**幻灯片 / PPT / 演示文稿**时调用：汇报、方案、路演、
//   培训课件、产品介绍、复盘总结等。识别「做个 PPT」「出套幻灯片」「演示文稿」。
// 【设计参考】页面类型体系（封面 / 目录 / 分隔 / 内容 / 汇总）与「主题对象 + 设计系统」
//   的组织方式，参考 MiniMax-AI/skills 的 pptx-generator；但实现为**纯 .NET**
//   （DocumentFormat.OpenXml 的 PresentationML），与平台其它技能同一条执行链路，
//   不依赖 Node / Python / PptxGenJS。
// 【图表】刻意**不发 ChartPart**：ChartPart 的 DrawingML schema 复杂、易产出旧版
//   PowerPoint 打不开的文件；这里与内置 docx 技能同口径，用 ImageSharp 把图表渲成
//   PNG 再按图片嵌入（视觉可控、兼容性最好）。
//
// 入口：public static string Run(string input) -> JSON
//   input = {
//     "title": "演示标题",                      // 必填（封面与默认文件名）
//     "subtitle": "副标题",                     // 可选
//     "author": "作者/团队",                    // 可选
//     "date": "2026-09",                       // 可选
//     "theme": "business|tech|warm|minimal|dark|vivid",   // 可选，默认 business
//     "themeColors": { "primary":"1F3864", "secondary":"2E5C9A", "accent":"C8A24A",
//                      "light":"E8EEF7", "bg":"FFFFFF", "text":"333333" },  // 可选，覆盖预设
//     "fontTitle": "微软雅黑",                  // 可选
//     "fontBody": "微软雅黑",                   // 可选
//     "outputPath": "/app/docs/x.pptx",        // 可选
//     "slides": [ … ]                          // 必填，见下
//   }
//
//   slides 每项是 { "type": "…", … }，type 取值：
//     cover    封面      title, subtitle, author, date
//     toc      目录      title, items:[ "…" ]
//     section  章节分隔  title, subtitle
//     content  要点页    title, bullets:[ "…" ], note
//     twoCol   两栏      title, left:{heading,bullets:[…]}, right:{heading,bullets:[…]}
//     table    表格      title, headers:[…], rows:[[…]]
//     kpi      指标卡    title, items:[ {value,label} ]
//     quote    引言      text, cite
//     image    图片      title, path, caption
//     chart    图表      title, chartType:"bar|line|pie|doughnut", categories:[…],
//                        series:[ {name,values:[…]} ], yLabel, caption
//     summary  小结      title, bullets:[…]
//     end      结束页    title, subtitle
//   任何页都可带 "notes"（备注文字），写入演讲者备注。
//   返回 = { ok, scene, path, slides, produce_file:{path,name,bytes}, message }
// ============================================================================

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
// 命名空间别名：避免与 OpenXML 类型重名（Color / PointF 等）
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using ImgColor = SixLabors.ImageSharp.Color;
using ImgPointF = SixLabors.ImageSharp.PointF;
using ImgRect = SixLabors.ImageSharp.Drawing.RectangularPolygon;
using ImgEllipse = SixLabors.ImageSharp.Drawing.EllipsePolygon;

public class Skill
{
    private const string SceneName = "pptx";

    // ===== 16:9 几何（EMU；1 英寸 = 914400）=====
    private const long W = 12192000;      // 13.333"
    private const long H = 6858000;       // 7.5"
    private const long MX = 838200;       // 左右安全边距 0.916"
    private const long CW = W - 2 * MX;   // 内容宽
    private const long TitleY = 457200;
    private const long TitleH = 762000;
    private const long BodyY = 1524000;
    private const long BodyH = H - BodyY - 838200;
    private const long BadgeW = 457200;
    private const long BadgeX = W - MX - BadgeW;
    private const long BadgeY = H - 685800;

    private const string NS_A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NS_P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string NS_R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    // ===== 主题 =====
    private sealed class Theme
    {
        public string Primary = "1F3864";
        public string Secondary = "2E5C9A";
        public string Accent = "C8A24A";
        public string Light = "E8EEF7";
        public string Bg = "FFFFFF";
        public string Text = "333333";
        public string FontTitle = "微软雅黑";
        public string FontBody = "微软雅黑";
    }

    /// <summary>当前渲染所用的主题（Run 是同步单次调用；用 ThreadStatic 避免并发时串台）。</summary>
    [ThreadStatic] private static Theme? _currentTheme;

    private static Theme ResolveTheme(JsonElement root)
    {
        var t = new Theme();
        var name = (Str(root, "theme") ?? "business").Trim().ToLowerInvariant();
        switch (name)
        {
            case "tech":
                t.Primary = "0B2545"; t.Secondary = "1B6CA8"; t.Accent = "00C2A8";
                t.Light = "E6F4F1"; t.Bg = "FFFFFF"; t.Text = "2B2B2B"; break;
            case "warm":
                t.Primary = "6B2D2D"; t.Secondary = "B4533C"; t.Accent = "E0A458";
                t.Light = "FBF1E6"; t.Bg = "FFFDF9"; t.Text = "3A2E2A"; break;
            case "minimal":
                t.Primary = "222222"; t.Secondary = "555555"; t.Accent = "9A9A9A";
                t.Light = "F2F2F2"; t.Bg = "FFFFFF"; t.Text = "333333"; break;
            case "dark":
                // 深色主题：primary 用亮色（标题在深底上要看得清）
                t.Primary = "FFFFFF"; t.Secondary = "C9D1D9"; t.Accent = "58A6FF";
                t.Light = "21262D"; t.Bg = "0D1117"; t.Text = "C9D1D9"; break;
            case "vivid":
                t.Primary = "2B2D42"; t.Secondary = "8D99AE"; t.Accent = "EF233C";
                t.Light = "EDEDED"; t.Bg = "FFFFFF"; t.Text = "2B2D42"; break;
            default: break; // business = 默认值
        }
        // themeColors 覆盖（逐项）
        if (root.TryGetProperty("themeColors", out var tc) && tc.ValueKind == JsonValueKind.Object)
        {
            t.Primary = Hex(Str(tc, "primary"), t.Primary);
            t.Secondary = Hex(Str(tc, "secondary"), t.Secondary);
            t.Accent = Hex(Str(tc, "accent"), t.Accent);
            t.Light = Hex(Str(tc, "light"), t.Light);
            t.Bg = Hex(Str(tc, "bg"), t.Bg);
            t.Text = Hex(Str(tc, "text"), t.Text);
        }
        t.FontTitle = Str(root, "fontTitle") ?? t.FontTitle;
        t.FontBody = Str(root, "fontBody") ?? t.FontBody;
        return t;
    }

    private static string Hex(string? v, string fallback)
    {
        if (string.IsNullOrWhiteSpace(v)) return fallback;
        var s = v.Trim().TrimStart('#').ToUpperInvariant();
        return s.Length == 6 && s.All(Uri.IsHexDigit) ? s : fallback;
    }

    /// <summary>深色底上正文用亮色；用于判断某主题的 bg 是否为深色。</summary>
    private static bool IsDarkBg(Theme t)
    {
        int r = Convert.ToInt32(t.Bg.Substring(0, 2), 16);
        int g = Convert.ToInt32(t.Bg.Substring(2, 2), 16);
        int b = Convert.ToInt32(t.Bg.Substring(4, 2), 16);
        return (r * 299 + g * 587 + b * 114) / 1000 < 128;
    }

    // ===== 入口 =====
    public static string Run(string input)
    {
        try
        {
            var built = Build(input ?? "");
            // produce_file 标记：告诉平台“这个文件可挂到对话里供下载”。
            // 网关扫到后用 AttachmentStore 挂号并挂到当前消息（att_xxx）。
            var produce = "";
            try
            {
                if (File.Exists(built.Path))
                {
                    var fi = new FileInfo(built.Path);
                    produce = ",\"produce_file\":{\"path\":" + Js(built.Path)
                        + ",\"name\":" + Js(fi.Name)
                        + ",\"bytes\":" + fi.Length + "}";
                }
            }
            catch { /* 标记失败不影响主返回 */ }
            return "{\"ok\":true,\"path\":" + Js(built.Path) + ",\"scene\":" + Js(SceneName)
                + ",\"slides\":" + built.Slides + produce
                + ",\"message\":" + Js("已生成演示文稿：" + built.Path) + "}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"scene\":" + Js(SceneName)
                + ",\"message\":" + Js("生成失败：" + ex.GetType().Name + "：" + ex.Message) + "}";
        }
    }

    // ===== 构建 =====
    private static (int Slides, string Path) Build(string input)
    {
        using var reqDoc = JsonDocument.Parse(ExtractJson(input));
        var root = reqDoc.RootElement;

        var theme = ResolveTheme(root);
        _currentTheme = theme;
        var title = Str(root, "title") ?? "演示文稿";
        var subtitle = Str(root, "subtitle");
        var author = Str(root, "author");
        var dateText = Str(root, "date");

        var slides = new List<JsonElement>();
        if (root.TryGetProperty("slides", out var sv) && sv.ValueKind == JsonValueKind.Array)
            foreach (var s in sv.EnumerateArray())
                if (s.ValueKind == JsonValueKind.Object) slides.Add(s);
        // 一份没有任何页的“演示文稿”没有意义，也不是用户要的：直接报错让模型补内容
        if (slides.Count == 0)
            throw new InvalidOperationException("slides 为空：请提供至少一页（如 cover / content / summary）。");

        var path = ResolveOutputPath(root, title);
        var count = 0;

        using (var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            var presPart = doc.AddPresentationPart();

            // 母版 + 版式 + 主题（PPTX 的必备骨架：没有它们 PowerPoint 会判“需要修复”）
            var masterPart = presPart.AddNewPart<SlideMasterPart>();
            var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
            layoutPart.SlideLayout = new P.SlideLayout(LayoutXml());
            // 版式必须回指其母版（ECMA-376：slideLayout 需关联 slideMaster），缺了会被判结构不完整
            layoutPart.AddPart(masterPart);
            masterPart.SlideMaster = new P.SlideMaster(MasterXml(masterPart.GetIdOfPart(layoutPart)));
            // 母版必须挂一份主题：主题颜色/字体由它提供，没有主题的 sldMaster 是不合法的
            AttachTheme(masterPart, theme);
            layoutPart.SlideLayout.Save();

            // 备注母版：只要有任一页带备注（notesSlide）就<b>必须</b>有它，并由 presentation 与每张备注页分别关联。
            // 实测踩到：只建 notesSlide 不建 notesMaster 时，OpenXML 校验器不报错，但 PowerPoint 打开时会要求修复。
            NotesMasterPart? notesMasterPart = null;
            if (slides.Any(s => !string.IsNullOrWhiteSpace(Str(s, "notes"))))
            {
                notesMasterPart = presPart.AddNewPart<NotesMasterPart>();
                notesMasterPart.NotesMaster = new P.NotesMaster(NotesMasterXml());
                AttachTheme(notesMasterPart, theme);
                notesMasterPart.NotesMaster.Save();
            }

            var ids = new List<string>();
            for (var i = 0; i < slides.Count; i++)
            {
                var el = slides[i];
                var sp = presPart.AddNewPart<SlidePart>();
                sp.AddPart(layoutPart); // 每张幻灯片必须挂一个版式
                var ctx = new SlideCtx(sp, theme, i + 1, slides.Count, title, subtitle, author, dateText);
                var xml = RenderSlide(el, ctx);
                sp.Slide = new P.Slide(xml);
                var notes = Str(el, "notes");
                if (!string.IsNullOrWhiteSpace(notes)) AttachNotes(sp, notes!, notesMasterPart);
                sp.Slide.Save();
                ids.Add(presPart.GetIdOfPart(sp));
            }
            count = ids.Count;

            presPart.Presentation = new P.Presentation(
                PresentationXml(presPart.GetIdOfPart(masterPart), ids,
                    notesMasterPart is null ? null : presPart.GetIdOfPart(notesMasterPart)));
            presPart.Presentation.Save();
        }

        return (count, path);
    }

    /// <summary>单张幻灯片的渲染上下文（部件引用 / 主题 / 序号 / 全局标题信息）。</summary>
    private sealed class SlideCtx
    {
        public readonly SlidePart Part;
        public readonly Theme Theme;
        public readonly int Index;
        public readonly int Total;
        public readonly string Title;
        public readonly string? Subtitle;
        public readonly string? Author;
        public readonly string? Date;
        private int _shapeId = 2;

        public SlideCtx(SlidePart part, Theme theme, int index, int total,
            string title, string? subtitle, string? author, string? date)
        {
            Part = part; Theme = theme; Index = index; Total = total;
            Title = title; Subtitle = subtitle; Author = author; Date = date;
        }

        public int NextId() => _shapeId++;

        /// <summary>把 PNG 作为图片挂进本页，返回关系 id（供 &lt;a:blip r:embed&gt; 引用）。</summary>
        public string AddImage(byte[] png)
        {
            var part = Part.AddImagePart(ImagePartType.Png);
            using var ms = new MemoryStream(png);
            part.FeedData(ms);
            return Part.GetIdOfPart(part);
        }
    }

    // ===== 各页面类型 =====
    private static string RenderSlide(JsonElement el, SlideCtx ctx)
    {
        var type = (Str(el, "type") ?? "content").Trim().ToLowerInvariant();
        var t = ctx.Theme;
        var shapes = new List<string>();

        // 除封面外，各页统一：浅色底 + 标题 + 标题下强调线 + 页码徽标
        var bg = t.Bg;

        switch (type)
        {
            case "cover":
                return CoverSlide(el, ctx);
            case "end":
                return EndSlide(el, ctx);
            case "section":
                return SectionSlide(el, ctx);
            case "toc":
                shapes.Add(SlideTitle(Str(el, "title") ?? "目录", ctx));
                shapes.Add(TocBody(el, ctx));
                break;
            case "twoCol":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(TwoColBody(el, ctx));
                break;
            case "table":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(TableBody(el, ctx));
                break;
            case "kpi":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(KpiBody(el, ctx));
                break;
            case "quote":
                return QuoteSlide(el, ctx);
            case "image":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(ImageBody(el, ctx));
                break;
            case "chart":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(ChartBody(el, ctx));
                break;
            case "summary":
                shapes.Add(SlideTitle(Str(el, "title") ?? "小结", ctx));
                shapes.Add(BulletBody(el, ctx, accent: true));
                break;
            default: // content
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(BulletBody(el, ctx, accent: false));
                break;
        }

        var note = Str(el, "note");
        if (!string.IsNullOrWhiteSpace(note))
            shapes.Add(Footnote(note!, ctx));

        shapes.Add(PageBadge(ctx));
        return SlideXml(bg, shapes);
    }

    // ---- 封面 ----
    private static string CoverSlide(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new List<string>();
        // 左侧色块（主色）作为视觉锚点
        shapes.Add(Rect(ctx.NextId(), 0, 0, W / 3, H, t.Primary));
        shapes.Add(Rect(ctx.NextId(), W / 3, 0, 114300, H, t.Accent));

        var title = Str(el, "title") ?? ctx.Title;
        var subtitle = Str(el, "subtitle") ?? ctx.Subtitle;
        var author = Str(el, "author") ?? ctx.Author;
        var date = Str(el, "date") ?? ctx.Date;

        var left = W / 3 + 685800;
        var rightW = W - left - MX;
        var paras = new StringBuilder();
        paras.Append(ParaTitle(title, 4000, t.Primary, lineSpacing: 105));
        if (!string.IsNullOrWhiteSpace(subtitle))
            paras.Append(Para(subtitle!, 1800, t.Secondary, spaceBefore: 12, lineSpacing: 130));
        shapes.Add(TextBox(ctx.NextId(), left, 1900000, rightW, 2600000, paras.ToString(), anchor: "t"));

        if (!string.IsNullOrWhiteSpace(author) || !string.IsNullOrWhiteSpace(date))
        {
            var meta = new StringBuilder();
            meta.Append(Para((author ?? "").Trim(), 1400, t.Text, align: "l"));
            if (!string.IsNullOrWhiteSpace(date))
                meta.Append(Para(date!.Trim(), 1400, t.Secondary, align: "l", spaceBefore: 4));
            shapes.Add(TextBox(ctx.NextId(), left, H - 1371600, rightW, 762000, meta.ToString(), anchor: "b"));
        }
        return SlideXml(t.Bg, shapes);
    }

    // ---- 结束页 ----
    private static string EndSlide(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new List<string> { Rect(ctx.NextId(), 0, 0, W, H, t.Primary) };
        var paras = new StringBuilder();
        paras.Append(ParaTitle(Str(el, "title") ?? "谢谢", 4000, t.Bg, align: "ctr"));
        var sub = Str(el, "subtitle");
        if (!string.IsNullOrWhiteSpace(sub))
            paras.Append(Para(sub!, 1600, t.Accent, align: "ctr", spaceBefore: 16));
        shapes.Add(TextBox(ctx.NextId(), MX, 2286000, CW, 2286000, paras.ToString(), anchor: "ctr"));
        return SlideXml(t.Primary, shapes);
    }

    // ---- 章节分隔 ----
    private static string SectionSlide(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new List<string> { Rect(ctx.NextId(), 0, 0, W, H, t.Primary) };
        // 大号序号
        shapes.Add(TextBox(ctx.NextId(), MX, 1371600, 1524000, 1524000,
            Para(ctx.Index.ToString("00"), 7200, t.Accent, bold: true, align: "l", font: t.FontTitle)));
        var paras = new StringBuilder();
        paras.Append(ParaTitle(Str(el, "title") ?? "", 3600, t.Bg, lineSpacing: 110));
        var sub = Str(el, "subtitle");
        if (!string.IsNullOrWhiteSpace(sub))
            paras.Append(Para(sub!, 1600, t.Light, align: "l", spaceBefore: 12));
        shapes.Add(TextBox(ctx.NextId(), MX + 1524000, 1600200, CW - 1524000, 2286000, paras.ToString(), anchor: "t"));
        shapes.Add(PageBadge(ctx));
        return SlideXml(t.Primary, shapes);
    }

    // ---- 引言 ----
    private static string QuoteSlide(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new List<string>();
        shapes.Add(Rect(ctx.NextId(), MX, 1143000, 57150, 2743200, t.Accent));
        var paras = new StringBuilder();
        paras.Append(Para(Str(el, "text") ?? "", 2600, t.Primary, align: "l", lineSpacing: 130, font: t.FontTitle));
        var cite = Str(el, "cite");
        var box = TextBox(ctx.NextId(), MX + 457200, 1143000, CW - 457200, 2743200, paras.ToString(), anchor: "ctr");
        shapes.Add(box);
        if (!string.IsNullOrWhiteSpace(cite))
            shapes.Add(TextBox(ctx.NextId(), MX + 457200, 4000500, CW - 457200, 457200,
                Para("— " + cite!.Trim(), 1400, t.Secondary, align: "l")));
        shapes.Add(PageBadge(ctx));
        return SlideXml(t.Bg, shapes);
    }

    // ---- 通用标题 ----
    private static string SlideTitle(string text, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new StringBuilder();
        shapes.Append(TextBox(ctx.NextId(), MX, TitleY, CW, TitleH,
            ParaTitle(text, 2800, t.Primary, lineSpacing: 100)));
        // 标题下强调线
        shapes.Append(Rect(ctx.NextId(), MX, TitleY + TitleH + 57150, 838200, 45720, t.Accent));
        return shapes.ToString();
    }

    private static string PageBadge(SlideCtx ctx)
    {
        var t = ctx.Theme;
        var sb = new StringBuilder();
        sb.Append(Rect(ctx.NextId(), BadgeX, BadgeY, BadgeW, BadgeW, t.Accent, radius: true));
        sb.Append(TextBox(ctx.NextId(), BadgeX, BadgeY, BadgeW, BadgeW,
            Para(ctx.Index.ToString("00"), 1100, t.Bg, bold: true, align: "ctr"), anchor: "ctr"));
        return sb.ToString();
    }

    private static string Footnote(string text, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var color = IsDarkBg(t) ? t.Light : "7A7A7A";
        return TextBox(ctx.NextId(), MX, H - 1097280, CW - BadgeW - 228600, 365760,
            Para(text, 1100, color, align: "l"));
    }

    // ---- 正文：项目符号 ----
    private static string BulletBody(JsonElement el, SlideCtx ctx, bool accent)
    {
        var t = ctx.Theme;
        var paras = new StringBuilder();
        var items = StringList(el, "bullets");
        if (items.Count == 0)
        {
            var p = Str(el, "text");
            if (!string.IsNullOrWhiteSpace(p)) items.Add(p!);
        }
        if (items.Count == 0) items.Add("（本页暂无要点）");

        foreach (var raw in items)
        {
            // 支持「小标题：说明」的两行结构，让内容页更有层次
            var parts = SplitLabel(raw);
            var color = accent ? t.Primary : t.Text;
            if (parts is null)
            {
                paras.Append(Para(raw, 1700, color, align: "l", bullet: "•", spaceBefore: 10, lineSpacing: 125, marL: 342900));
            }
            else
            {
                paras.Append(Para(parts.Value.Label, 1800, t.Primary, bold: true, align: "l",
                    bullet: "•", spaceBefore: 12, lineSpacing: 120, marL: 342900));
                paras.Append(Para(parts.Value.Detail, 1500, t.Secondary, align: "l",
                    lineSpacing: 125, marL: 342900));
            }
        }
        return TextBox(ctx.NextId(), MX, BodyY, CW, BodyH, paras.ToString(), anchor: "t");
    }

    private static (string Label, string Detail)? SplitLabel(string raw)
    {
        var idx = raw.IndexOf('：');
        if (idx <= 0) idx = raw.IndexOf(':');
        if (idx <= 0 || idx >= raw.Length - 1) return null;
        var label = raw.Substring(0, idx).Trim();
        var detail = raw.Substring(idx + 1).Trim();
        if (label.Length == 0 || detail.Length < 4 || label.Length > 24) return null;
        return (label, detail);
    }

    // ---- 目录 ----
    private static string TocBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var items = StringList(el, "items");
        if (items.Count == 0) return BulletBody(el, ctx, accent: false);

        var paras = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            paras.Append(Para((i + 1).ToString("00") + "   " + items[i], 1800, t.Text,
                align: "l", spaceBefore: 14, lineSpacing: 120));
        }
        return TextBox(ctx.NextId(), MX, BodyY, CW, BodyH, paras.ToString(), anchor: "t");
    }

    // ---- 两栏 ----
    private static string TwoColBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var gap = 342900;
        var colW = (CW - gap) / 2;
        var shapes = new StringBuilder();

        foreach (var (name, x) in new[] { ("left", MX), ("right", MX + colW + gap) })
        {
            var paras = new StringBuilder();
            string? heading = null;
            var bullets = new List<string>();
            if (el.TryGetProperty(name, out var col) && col.ValueKind == JsonValueKind.Object)
            {
                heading = Str(col, "heading");
                bullets = StringList(col, "bullets");
            }
            // 栏底卡片
            shapes.Append(Rect(ctx.NextId(), x, BodyY, colW, BodyH, t.Light, radius: true));
            var inner = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(heading))
                inner.Append(Para(heading!, 1900, t.Primary, bold: true, align: "l", lineSpacing: 110));
            if (bullets.Count == 0) bullets.Add("（空）");
            foreach (var b in bullets)
                inner.Append(Para(b, 1500, t.Text, align: "l", bullet: "•",
                    spaceBefore: 10, lineSpacing: 125, marL: 285750));
            shapes.Append(TextBox(ctx.NextId(), x + 228600, BodyY + 228600, colW - 457200, BodyH - 457200,
                inner.ToString(), anchor: "t"));
        }
        return shapes.ToString();
    }

    // ---- 表格 ----
    private static string TableBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var headers = StringList(el, "headers");
        var rows = new List<List<string>>();
        if (el.TryGetProperty("rows", out var rv) && rv.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rv.EnumerateArray())
            {
                var row = new List<string>();
                if (r.ValueKind == JsonValueKind.Array)
                    foreach (var c in r.EnumerateArray())
                        row.Add(c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ToString());
                if (row.Count > 0) rows.Add(row);
            }
        }
        var cols = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
        if (cols == 0) return BulletBody(el, ctx, accent: false);

        var rowCount = rows.Count + (headers.Count > 0 ? 1 : 0);
        var colW = CW / cols;
        var headerH = 457200L;
        var rowH = Math.Min(571500L, (BodyH - headerH) / Math.Max(1, rows.Count));
        // 行高不足时压缩字号的阈值：行数太多就给表体留最小高度，超出部分靠查看器滚动
        if (rows.Count > 0 && rowH < 320000L) rowH = 320000L;

        var tbl = new StringBuilder();
        // 斑马纹 + 表头底色：不用内置表格样式（依赖 theme 部件），直接给单元格上色，兼容性最好
        tbl.Append("<a:tbl><a:tblPr firstRow=\"1\" bandRow=\"0\"/><a:tblGrid>");
        for (var c = 0; c < cols; c++) tbl.Append("<a:gridCol w=\"").Append(colW).Append("\"/>");
        tbl.Append("</a:tblGrid>");

        if (headers.Count > 0)
        {
            tbl.Append("<a:tr h=\"").Append(headerH).Append("\">");
            for (var c = 0; c < cols; c++)
            {
                var text = c < headers.Count ? headers[c] : "";
                tbl.Append(TableCell(text, 1500, t.Bg, t.Primary, bold: true, colW));
            }
            tbl.Append("</a:tr>");
        }
        for (var r = 0; r < rows.Count; r++)
        {
            tbl.Append("<a:tr h=\"").Append(rowH).Append("\">");
            var fill = r % 2 == 1 ? t.Light : t.Bg;
            for (var c = 0; c < cols; c++)
            {
                var text = c < rows[r].Count ? rows[r][c] : "";
                tbl.Append(TableCell(text, 1300, t.Text, fill, bold: false, colW));
            }
            tbl.Append("</a:tr>");
        }
        tbl.Append("</a:tbl>");

        var tableH = headerH + rowH * rows.Count;
        var frame = new StringBuilder();
        frame.Append("<p:graphicFrame><p:nvGraphicFramePr>")
             .Append("<p:cNvPr id=\"").Append(ctx.NextId()).Append("\" name=\"Table\"/>")
             .Append("<p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>")
             .Append("<p:xfrm><a:off x=\"").Append(MX).Append("\" y=\"").Append(BodyY)
             .Append("\"/><a:ext cx=\"").Append(CW).Append("\" cy=\"").Append(tableH).Append("\"/></p:xfrm>")
             .Append("<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">")
             .Append(tbl)
             .Append("</a:graphicData></a:graphic></p:graphicFrame>");
        return frame.ToString();
    }

    private static string TableCell(string text, int sz, string color, string fill, bool bold, long w)
    {
        var paras = Para(text, sz, color, bold: bold, align: "l", lineSpacing: 110);
        return "<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>" + paras + "</a:txBody>"
             + "<a:tcPr marL=\"91440\" marR=\"91440\" marT=\"45720\" marB=\"45720\" anchor=\"ctr\">"
             + "<a:solidFill>" + Rgb(fill) + "</a:solidFill></a:tcPr></a:tc>";
    }

    // ---- KPI 指标卡 ----
    private static string KpiBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var items = new List<(string Value, string Label)>();
        if (el.TryGetProperty("items", out var iv) && iv.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in iv.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                var v = Str(it, "value") ?? "";
                var l = Str(it, "label") ?? "";
                if (v.Length == 0 && l.Length == 0) continue;
                items.Add((v, l));
            }
        }
        if (items.Count == 0) return BulletBody(el, ctx, accent: false);

        var n = Math.Min(items.Count, 4);              // 一行最多 4 张卡，超出分两行
        var rowsCount = (items.Count + n - 1) / n;
        var gap = 285750L;
        var cardW = (CW - gap * (n - 1)) / n;
        var cardH = Math.Min(2057400L, (BodyH - gap * (rowsCount - 1)) / rowsCount);
        var shapes = new StringBuilder();

        for (var i = 0; i < items.Count; i++)
        {
            var row = i / n;
            var col = i % n;
            var x = MX + (cardW + gap) * col;
            var y = BodyY + (cardH + gap) * row;
            shapes.Append(Rect(ctx.NextId(), x, y, cardW, cardH, t.Light, radius: true));
            shapes.Append(Rect(ctx.NextId(), x, y, cardW, 45720, t.Accent));
            var inner = new StringBuilder();
            inner.Append(Para(items[i].Value, 3200, t.Primary, bold: true, align: "ctr", lineSpacing: 100, font: t.FontTitle));
            if (items[i].Label.Length > 0)
                inner.Append(Para(items[i].Label, 1300, t.Secondary, align: "ctr", spaceBefore: 6, lineSpacing: 115));
            shapes.Append(TextBox(ctx.NextId(), x + 91440, y + 342900, cardW - 182880, cardH - 457200,
                inner.ToString(), anchor: "ctr"));
        }
        return shapes.ToString();
    }

    // ---- 图片 ----
    private static string ImageBody(JsonElement el, SlideCtx ctx)
    {
        var path = Str(el, "path");
        var caption = Str(el, "caption");
        var availH = BodyH - (string.IsNullOrWhiteSpace(caption) ? 0 : 457200);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            var t = ctx.Theme;
            var msg = string.IsNullOrWhiteSpace(path)
                ? "（未提供 path，无法插入图片）"
                : "（图片不存在：" + path + "）";
            return Rect(ctx.NextId(), MX, BodyY, CW, availH, ctx.Theme.Light, radius: true)
                 + TextBox(ctx.NextId(), MX, BodyY, CW, availH,
                        Para(msg, 1500, t.Secondary, align: "ctr"), anchor: "ctr");
        }

        byte[] bytes;
        int pxW, pxH;
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load(path!);
            pxW = img.Width; pxH = img.Height;
        }
        catch (Exception ex)
        {
            // 非 ImageSharp 能解析的格式（如 svg/emf）→ 交给平台按原文件嵌入，避免整页失败
            throw new InvalidOperationException("图片无法读取：" + path + "（" + ex.Message + "）");
        }

        // 等比缩放并居中
        var scale = Math.Min((double)CW / pxW, (double)availH / pxH);
        var cx = Math.Max(1, (long)(pxW * scale));
        var cy = Math.Max(1, (long)(pxH * scale));
        var x = MX + (CW - cx) / 2;
        var y = BodyY + (availH - cy) / 2;

        var rel = ctx.AddImage(File.ReadAllBytes(path!));
        var shapes = new StringBuilder();
        shapes.Append(Picture(ctx.NextId(), rel, x, y, cx, cy));
        if (!string.IsNullOrWhiteSpace(caption))
            shapes.Append(TextBox(ctx.NextId(), MX, BodyY + availH, CW, 365760,
                Para(caption!, 1200, ctx.Theme.Secondary, align: "ctr")));
        return shapes.ToString();
    }

    // ---- 图表（渲成 PNG 再嵌入）----
    private static string ChartBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var kind = (Str(el, "chartType") ?? Str(el, "type2") ?? "bar").Trim().ToLowerInvariant();
        var categories = StringList(el, "categories");
        var series = new List<(string Name, double[] Values)>();
        if (el.TryGetProperty("series", out var sv) && sv.ValueKind == JsonValueKind.Array)
        {
            foreach (var one in sv.EnumerateArray())
            {
                if (one.ValueKind != JsonValueKind.Object) continue;
                series.Add((Str(one, "name") ?? "", ReadNumbers(one, "values")));
            }
        }
        if (series.Count == 0) series.Add(("", ReadNumbers(el, "values")));
        if (series.All(s => s.Values.Length == 0))
            throw new InvalidOperationException("图表缺少数据：请提供 series[].values 或 values。");
        if (categories.Count == 0)
        {
            var max = series.Max(s => s.Values.Length);
            for (var i = 0; i < max; i++) categories.Add((i + 1).ToString());
        }

        var title = Str(el, "title");
        var yLabel = Str(el, "yLabel");
        var caption = Str(el, "caption");
        var dark = IsDarkBg(t);

        const int pxW = 1400, pxH = 800;
        byte[] png;
        try
        {
            png = kind switch
            {
                "pie" => RenderPie(pxW, pxH, title, categories, series[0].Values, dark, t),
                "doughnut" => RenderPie(pxW, pxH, title, categories, series[0].Values, dark, t, doughnut: true),
                "line" => RenderLine(pxW, pxH, title, yLabel, categories, series, dark, t),
                _ => RenderBar(pxW, pxH, title, yLabel, categories, series, dark, t),
            };
        }
        catch (Exception ex)
        {
            // 字体缺失等环境问题 → 降级为要点页，别让整页失败
            var items = new List<string>();
            for (var i = 0; i < categories.Count; i++)
            {
                var vals = new List<string>();
                foreach (var s in series)
                    if (i < s.Values.Length)
                        vals.Add((s.Name.Length > 0 ? s.Name + " " : "") + s.Values[i].ToString("0.##"));
                items.Add(categories[i] + "：" + string.Join("；", vals));
            }
            if (items.Count == 0) items.Add("（图表数据不可渲染：" + ex.Message + "）");
            return TextBox(ctx.NextId(), MX, BodyY, CW, BodyH,
                BuildBullets(items, 1500, t.Text, t.Primary), anchor: "t");
        }

        var availH = BodyH - (string.IsNullOrWhiteSpace(caption) ? 0 : 457200);
        var scale = Math.Min((double)CW / pxW, (double)availH / pxH);
        var cx = (long)(pxW * scale);
        var cy = (long)(pxH * scale);
        var x = MX + (CW - cx) / 2;
        var y = BodyY + (availH - cy) / 2;

        var rel = ctx.AddImage(png);
        var shapes = new StringBuilder();
        shapes.Append(Picture(ctx.NextId(), rel, x, y, cx, cy));
        if (!string.IsNullOrWhiteSpace(caption))
            shapes.Append(TextBox(ctx.NextId(), MX, BodyY + availH, CW, 365760,
                Para(caption!, 1200, t.Secondary, align: "ctr")));
        return shapes.ToString();
    }

    private static string BuildBullets(List<string> items, int sz, string color, string boldColor)
    {
        var paras = new StringBuilder();
        foreach (var it in items)
        {
            var parts = SplitLabel(it);
            if (parts is null)
                paras.Append(Para(it, sz, color, align: "l", bullet: "•", spaceBefore: 10, lineSpacing: 125, marL: 342900));
            else
            {
                paras.Append(Para(parts.Value.Label, sz + 100, boldColor, bold: true, align: "l",
                    bullet: "•", spaceBefore: 12, lineSpacing: 120, marL: 342900));
                paras.Append(Para(parts.Value.Detail, sz - 100, color, align: "l", lineSpacing: 125, marL: 342900));
            }
        }
        return paras.ToString();
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

    private static List<string> StringList(JsonElement o, string name)
    {
        var list = new List<string>();
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return list;
        foreach (var x in v.EnumerateArray())
        {
            var s = x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!.Trim());
        }
        return list;
    }

    // ===== 演讲者备注 =====
    /// <summary>
    /// 给一页挂上备注。
    ///
    /// <para>
    /// <b>必须同时关联备注母版</b>：notesSlide 是一个独立部件，规范上需关联 notesMaster，
    /// 且 presentation 里要有 notesMasterIdLst。只建 notesSlide 不建 notesMaster 时，
    /// OpenXML 校验器不报错，但 PowerPoint 打开会弹出“需要修复”（实测踩到）。
    /// </para>
    /// </summary>
    private static void AttachNotes(SlidePart slide, string text, NotesMasterPart? notesMaster)
    {
        var notesPart = slide.AddNewPart<NotesSlidePart>();
        if (notesMaster is not null) notesPart.AddPart(notesMaster);
        // 备注页回指它所属的幻灯片（PowerPoint / python-pptx 产物均有此关系，保持一致）
        notesPart.AddPart(slide);
        var xml = new StringBuilder();
        xml.Append("<p:notes xmlns:a=\"").Append(NS_A).Append("\" xmlns:r=\"").Append(NS_R)
           .Append("\" xmlns:p=\"").Append(NS_P).Append("\">")
           .Append("<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>")
           .Append("<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Notes Placeholder\"/>")
           .Append("<p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>")
           .Append("<p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr>")
           .Append("<p:spPr/>")
           .Append("<p:txBody><a:bodyPr/><a:lstStyle/>")
           .Append(Para(text, 1200, "000000", align: "l"))
           .Append("</p:txBody></p:sp></p:spTree></p:cSld>")
           .Append("<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>");
        notesPart.NotesSlide = new P.NotesSlide(xml.ToString());
        notesPart.NotesSlide.Save();
    }

    // ===== XML 片段 =====
    private static string SlideXml(string bg, IEnumerable<string> shapes)
    {
        var sb = new StringBuilder();
        sb.Append("<p:sld xmlns:a=\"").Append(NS_A).Append("\" xmlns:r=\"").Append(NS_R)
          .Append("\" xmlns:p=\"").Append(NS_P).Append("\"><p:cSld>");
        if (!string.IsNullOrWhiteSpace(bg))
            sb.Append("<p:bg><p:bgPr><a:solidFill>").Append(Rgb(bg)).Append("</a:solidFill><a:effectLst/></p:bgPr></p:bg>");
        sb.Append("<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>");
        foreach (var s in shapes) sb.Append(s);
        sb.Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        return sb.ToString();
    }

    private static string Rgb(string hex) => "<a:srgbClr val=\"" + hex + "\"/>";

    private static string TextBox(int id, long x, long y, long cx, long cy, string paras, string anchor = "t")
    {
        var sb = new StringBuilder();
        sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id).Append("\" name=\"TextBox ").Append(id).Append("\"/>")
          .Append("<p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>")
          .Append("<p:spPr><a:xfrm><a:off x=\"").Append(x).Append("\" y=\"").Append(y)
          .Append("\"/><a:ext cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm>")
          .Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>")
          .Append("<p:txBody><a:bodyPr wrap=\"square\" lIns=\"0\" rIns=\"0\" tIns=\"0\" bIns=\"0\" anchor=\"")
          .Append(anchor).Append("\"><a:normAutofit/></a:bodyPr><a:lstStyle/>")
          .Append(paras)
          .Append("</p:txBody></p:sp>");
        return sb.ToString();
    }

    private static string Rect(int id, long x, long y, long cx, long cy, string fill,
        bool radius = false, int alpha = 100)
    {
        var sb = new StringBuilder();
        sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id).Append("\" name=\"Rect ").Append(id).Append("\"/>")
          .Append("<p:cNvSpPr/><p:nvPr/></p:nvSpPr>")
          .Append("<p:spPr><a:xfrm><a:off x=\"").Append(x).Append("\" y=\"").Append(y)
          .Append("\"/><a:ext cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm>");
        if (radius)
            sb.Append("<a:prstGeom prst=\"roundRect\"><a:avLst><a:gd name=\"adj\" fmla=\"val 12000\"/></a:avLst></a:prstGeom>");
        else
            sb.Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>");
        sb.Append("<a:solidFill>");
        if (alpha < 100) sb.Append("<a:srgbClr val=\"").Append(fill).Append("\"><a:alpha val=\"").Append(alpha * 1000).Append("\"/></a:srgbClr>");
        else sb.Append(Rgb(fill));
        sb.Append("</a:solidFill><a:ln><a:noFill/></a:ln></p:spPr>")
          .Append("<p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody></p:sp>");
        return sb.ToString();
    }

    private static string Picture(int id, string relId, long x, long y, long cx, long cy)
    {
        var sb = new StringBuilder();
        sb.Append("<p:pic><p:nvPicPr><p:cNvPr id=\"").Append(id).Append("\" name=\"Picture ").Append(id).Append("\"/>")
          .Append("<p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>")
          .Append("<p:blipFill><a:blip r:embed=\"").Append(relId).Append("\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>")
          .Append("<p:spPr><a:xfrm><a:off x=\"").Append(x).Append("\" y=\"").Append(y)
          .Append("\"/><a:ext cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm>")
          .Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");
        return sb.ToString();
    }

    /// <summary>标题段落：走主题的标题字体（fontTitle）。</summary>
    private static string ParaTitle(string text, int sz, string color, string align = "l", int lineSpacing = 0)
        => Para(text, sz, color, bold: true, align: align, font: _currentTheme?.FontTitle, lineSpacing: lineSpacing);

    private static string Para(string text, int sz, string color, bool bold = false, string align = "l",
        string? bullet = null, string? font = null, int spaceBefore = 0,
        int lineSpacing = 0, int marL = 0)
    {
        var sb = new StringBuilder();
        sb.Append("<a:p><a:pPr algn=\"").Append(align).Append("\"");
        if (marL > 0) sb.Append(" marL=\"").Append(marL).Append("\" indent=\"-").Append(marL / 2).Append("\"");
        sb.Append(">");
        if (lineSpacing > 0) sb.Append("<a:lnSpc><a:spcPct val=\"").Append(lineSpacing * 1000).Append("\"/></a:lnSpc>");
        if (spaceBefore > 0) sb.Append("<a:spcBef><a:spcPts val=\"").Append(spaceBefore * 100).Append("\"/></a:spcBef>");
        if (bullet is not null)
            sb.Append("<a:buFont typeface=\"Arial\"/><a:buChar char=\"").Append(bullet).Append("\"/>");
        else
            sb.Append("<a:buNone/>");
        sb.Append("</a:pPr>");
        sb.Append(Run(text, sz, color, bold, font));
        sb.Append("</a:p>");
        return sb.ToString();
    }

    private static string Run(string text, int sz, string color, bool bold, string? font)
    {
        // font 未显式指定时，落到“当前主题的正文字体”——
        // 这样 json 里的 fontBody 覆盖才会真正生效（实测踩到：解析了主题却没用到）。
        var face = Xml(font ?? _currentTheme?.FontBody ?? "微软雅黑");
        return "<a:r><a:rPr lang=\"zh-CN\" altLang=\"en-US\" sz=\"" + sz + "\" b=\"" + (bold ? 1 : 0) + "\" dirty=\"0\">"
             + "<a:solidFill>" + Rgb(color) + "</a:solidFill>"
             + "<a:latin typeface=\"" + face + "\"/><a:ea typeface=\"" + face + "\"/>"
             + "</a:rPr><a:t>" + Xml(text) + "</a:t></a:r>";
    }

    // ===== 包骨架（母版 / 版式 / 主题 / 备注母版 / 演示文稿）=====

    /// <summary>给母版（幻灯片母版 / 备注母版）挂一份主题部件：主题颜色与字体由它提供，母版必须有关联主题。</summary>
    private static void AttachTheme(OpenXmlPartContainer holder, Theme t)
    {
        var themePart = holder.AddNewPart<ThemePart>();
        themePart.Theme = new A.Theme(ThemeXml(t));
        themePart.Theme.Save();
    }

    /// <summary>
    /// 主题 XML（clrScheme / fontScheme / fmtScheme）。
    /// <para>
    /// 三个子方案都是 schema 要求的，且 fmtScheme 下的 fillStyleLst / lnStyleLst / effectStyleLst /
    /// bgFillStyleLst <b>各需至少 3 项</b>，否则会被校验器（与 PowerPoint）判为非法。
    /// </para>
    /// </summary>
    private static string ThemeXml(Theme t)
    {
        var sb = new StringBuilder();
        sb.Append("<a:theme xmlns:a=\"").Append(NS_A).Append("\" name=\"知聚主题\">")
          .Append("<a:themeElements>")
          .Append("<a:clrScheme name=\"知聚配色\">")
          .Append("<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1>")
          .Append("<a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>")
          .Append("<a:dk2>").Append(Srgb(t.Text)).Append("</a:dk2>")
          .Append("<a:lt2>").Append(Srgb(t.Light)).Append("</a:lt2>")
          .Append("<a:accent1>").Append(Srgb(t.Primary)).Append("</a:accent1>")
          .Append("<a:accent2>").Append(Srgb(t.Secondary)).Append("</a:accent2>")
          .Append("<a:accent3>").Append(Srgb(t.Accent)).Append("</a:accent3>")
          .Append("<a:accent4>").Append(Srgb(t.Light)).Append("</a:accent4>")
          .Append("<a:accent5>").Append(Srgb(t.Secondary)).Append("</a:accent5>")
          .Append("<a:accent6>").Append(Srgb(t.Primary)).Append("</a:accent6>")
          .Append("<a:hlink>").Append(Srgb("0563C1")).Append("</a:hlink>")
          .Append("<a:folHlink>").Append(Srgb("954F72")).Append("</a:folHlink>")
          .Append("</a:clrScheme>")
          .Append("<a:fontScheme name=\"知聚字体\">")
          .Append("<a:majorFont><a:latin typeface=\"").Append(Xml(t.FontTitle)).Append("\"/>")
          .Append("<a:ea typeface=\"").Append(Xml(t.FontTitle)).Append("\"/><a:cs typeface=\"\"/></a:majorFont>")
          .Append("<a:minorFont><a:latin typeface=\"").Append(Xml(t.FontBody)).Append("\"/>")
          .Append("<a:ea typeface=\"").Append(Xml(t.FontBody)).Append("\"/><a:cs typeface=\"\"/></a:minorFont>")
          .Append("</a:fontScheme>")
          .Append("<a:fmtScheme name=\"知聚样式\">")
          .Append("<a:fillStyleLst>")
          .Append(SolidFill("FFFFFF")).Append(SolidFill(t.Light)).Append(SolidFill(t.Primary))
          .Append("</a:fillStyleLst>")
          .Append("<a:lnStyleLst>")
          .Append(Line(t.Primary)).Append(Line(t.Secondary)).Append(Line(t.Accent))
          .Append("</a:lnStyleLst>")
          .Append("<a:effectStyleLst>")
          .Append("<a:effectStyle><a:effectLst/></a:effectStyle>")
          .Append("<a:effectStyle><a:effectLst/></a:effectStyle>")
          .Append("<a:effectStyle><a:effectLst/></a:effectStyle>")
          .Append("</a:effectStyleLst>")
          .Append("<a:bgFillStyleLst>")
          .Append(SolidFill("FFFFFF")).Append(SolidFill(t.Light)).Append(SolidFill(t.Primary))
          .Append("</a:bgFillStyleLst>")
          .Append("</a:fmtScheme>")
          .Append("</a:themeElements>")
          .Append("<a:objectDefaults/><a:extraClrSchemeLst/>")
          .Append("</a:theme>");
        return sb.ToString();
    }

    private static string Srgb(string hex) => "<a:srgbClr val=\"" + Xml(hex) + "\"/>";

    private static string SolidFill(string hex)
        => "<a:solidFill>" + Srgb(hex) + "</a:solidFill>";

    private static string Line(string hex)
        => "<a:ln w=\"6350\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">" + SolidFill(hex) + "<a:prstDash val=\"solid\"/></a:ln>";

    /// <summary>
    /// 备注母版 XML：提供备注页使用的占位符（主体文字），与 notesSlide 里 type="body" idx="1" 对应。
    /// </summary>
    private static string NotesMasterXml()
    {
        var sb = new StringBuilder();
        sb.Append("<p:notesMaster xmlns:a=\"").Append(NS_A).Append("\" xmlns:r=\"").Append(NS_R)
          .Append("\" xmlns:p=\"").Append(NS_P).Append("\">")
          .Append("<p:cSld><p:bg><p:bgPr>").Append(SolidFill("FFFFFF"))
          .Append("<a:effectLst/></p:bgPr></p:bg>")
          .Append("<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>")
          // 备注文字占位符（与 notesSlide 的 ph type=body idx=1 对应）
          .Append("<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Notes Placeholder\"/>")
          .Append("<p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr>")
          .Append("<p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>")
          .Append("<p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody></p:sp>")
          .Append("</p:spTree></p:cSld>")
          .Append("<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" ")
          .Append("accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>")
          .Append("<p:notesStyle/>")
          .Append("</p:notesMaster>");
        return sb.ToString();
    }
    private static string MasterXml(string layoutRelId)
    {
        var sb = new StringBuilder();
        sb.Append("<p:sldMaster xmlns:a=\"").Append(NS_A).Append("\" xmlns:r=\"").Append(NS_R)
          .Append("\" xmlns:p=\"").Append(NS_P).Append("\">")
          .Append("<p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>")
          .Append("<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld>")
          .Append("<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" ")
          .Append("accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>")
          .Append("<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"").Append(layoutRelId).Append("\"/></p:sldLayoutIdLst>")
          .Append("<p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles>")
          .Append("</p:sldMaster>");
        return sb.ToString();
    }

    private static string LayoutXml()
    {
        var sb = new StringBuilder();
        sb.Append("<p:sldLayout xmlns:a=\"").Append(NS_A).Append("\" xmlns:r=\"").Append(NS_R)
          .Append("\" xmlns:p=\"").Append(NS_P).Append("\" type=\"blank\" preserve=\"1\">")
          .Append("<p:cSld name=\"Blank\"><p:spTree>")
          .Append("<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>")
          .Append("</p:spTree></p:cSld>")
          .Append("<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        return sb.ToString();
    }

    private static string PresentationXml(string masterRelId, List<string> slideRelIds, string? notesMasterRelId)
    {
        var sb = new StringBuilder();
        sb.Append("<p:presentation xmlns:a=\"").Append(NS_A).Append("\" xmlns:r=\"").Append(NS_R)
          .Append("\" xmlns:p=\"").Append(NS_P).Append("\">")
          .Append("<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"").Append(masterRelId).Append("\"/></p:sldMasterIdLst>");
        // 元素顺序是 schema 固定的：sldMasterIdLst → notesMasterIdLst → sldIdLst → sldSz → notesSz
        if (!string.IsNullOrWhiteSpace(notesMasterRelId))
            sb.Append("<p:notesMasterIdLst><p:notesMasterId r:id=\"").Append(notesMasterRelId).Append("\"/></p:notesMasterIdLst>");
        sb.Append("<p:sldIdLst>");
        var id = 256;
        foreach (var rid in slideRelIds)
            sb.Append("<p:sldId id=\"").Append(id++).Append("\" r:id=\"").Append(rid).Append("\"/>");
        sb.Append("</p:sldIdLst>")
          // 16:9 宽屏；notesSz 为必备元素
          .Append("<p:sldSz cx=\"").Append(W).Append("\" cy=\"").Append(H).Append("\" type=\"screen16x9\"/>")
          .Append("<p:notesSz cx=\"").Append(H).Append("\" cy=\"").Append(W).Append("\"/>")
          .Append("</p:presentation>");
        return sb.ToString();
    }

    // ===== 图表渲染（ImageSharp → PNG）=====
    private static readonly string[] Palette =
        { "#4F81BD", "#C0504D", "#9BBB59", "#8064A2", "#4BACC6", "#F79646", "#2C4D75", "#772C2A" };
    private static readonly string[] FontCandidates = { "Microsoft YaHei", "SimHei", "SimSun", "Arial", "DejaVu Sans" };
    private static readonly object _fontLock = new object();
    private static SixLabors.Fonts.FontFamily? _fontFamily;

    private static SixLabors.Fonts.Font Family(float size)
    {
        lock (_fontLock)
        {
            if (_fontFamily is null)
            {
                foreach (var name in FontCandidates)
                    if (SixLabors.Fonts.SystemFonts.TryGet(name, out var f)) { _fontFamily = f; break; }
                if (_fontFamily is null && SixLabors.Fonts.SystemFonts.Collection.Families.Any())
                    _fontFamily = SixLabors.Fonts.SystemFonts.Collection.Families.First();
                if (_fontFamily is null)
                    throw new InvalidOperationException("图表需要至少一种系统字体，但当前环境未发现可用字体。");
            }
            return _fontFamily.Value.CreateFont(size);
        }
    }

    private static byte[] RenderBar(int w, int h, string? title, string? yLabel, List<string> cats,
        List<(string Name, double[] Values)> series, bool dark, Theme t)
    {
        using var img = new Image<Rgba32>(w, h);
        var fg = dark ? ImgColor.White : ImgColor.Black;
        var muted = dark ? ImgColor.FromRgba(170, 178, 190, 255) : ImgColor.FromRgba(90, 90, 90, 255);
        int padL = 92, padR = 36, padT = title is null ? 40 : 78, padB = 76;
        var maxV = Math.Max(0.0001, series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max());

        img.Mutate(x =>
        {
            x.Fill(dark ? ImgColor.FromRgba(13, 17, 23, 255) : ImgColor.White);
            if (title is not null) x.DrawText(title, Family(26f), fg, new ImgPointF(padL, 24));

            const int ticks = 4;
            for (var i = 0; i <= ticks; i++)
            {
                var y = h - padB - (float)((h - padT - padB) * i / (double)ticks);
                x.DrawLine(muted, 1f, new ImgPointF(padL, y), new ImgPointF(w - padR, y));
                x.DrawText((maxV * i / ticks).ToString("0.##"), Family(13f), muted, new ImgPointF(8, y - 9));
            }
            if (!string.IsNullOrWhiteSpace(yLabel)) x.DrawText(yLabel!, Family(13f), muted, new ImgPointF(8, padT - 22));

            int nc = cats.Count, ns = series.Count;
            double slot = (w - padL - padR) / (double)Math.Max(1, nc);
            double barW = Math.Max(4, slot * 0.72 / ns);
            for (var c = 0; c < nc; c++)
            {
                for (var s = 0; s < ns; s++)
                {
                    var v = c < series[s].Values.Length ? series[s].Values[c] : 0;
                    if (v < 0) v = 0;
                    var bh = (float)((h - padT - padB) * (v / maxV));
                    var bx = (float)(padL + slot * c + slot * 0.14 + barW * s);
                    var by = h - padB - bh;
                    if (bh > 0.5f) x.Fill(ImgColor.ParseHex(Palette[(ns > 1 ? s : c) % Palette.Length]), new ImgRect(bx, by, (float)barW, bh));
                }
                var lab = cats[c].Length > 8 ? cats[c].Substring(0, 8) + "…" : cats[c];
                var tw = SixLabors.Fonts.TextMeasurer.MeasureBounds(lab, new SixLabors.Fonts.TextOptions(Family(14f))).Width;
                x.DrawText(lab, Family(14f), fg, new ImgPointF((float)(padL + slot * c + slot / 2 - tw / 2), h - padB + 10));
            }
            if (ns > 1)
            {
                float lx = padL, ly = padT - 28;
                for (var s = 0; s < ns; s++)
                {
                    var nm = string.IsNullOrEmpty(series[s].Name) ? "系列" + (s + 1) : series[s].Name;
                    x.Fill(ImgColor.ParseHex(Palette[s % Palette.Length]), new ImgRect(lx, ly, 15, 15));
                    x.DrawText(nm, Family(14f), fg, new ImgPointF(lx + 20, ly - 3));
                    lx += 20 + SixLabors.Fonts.TextMeasurer.MeasureBounds(nm, new SixLabors.Fonts.TextOptions(Family(14f))).Width + 24;
                }
            }
        });
        return ToPng(img);
    }

    private static byte[] RenderLine(int w, int h, string? title, string? yLabel, List<string> cats,
        List<(string Name, double[] Values)> series, bool dark, Theme t)
    {
        using var img = new Image<Rgba32>(w, h);
        var fg = dark ? ImgColor.White : ImgColor.Black;
        var muted = dark ? ImgColor.FromRgba(170, 178, 190, 255) : ImgColor.FromRgba(90, 90, 90, 255);
        int padL = 92, padR = 36, padT = title is null ? 40 : 78, padB = 76;
        var maxV = Math.Max(0.0001, series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max());

        img.Mutate(x =>
        {
            x.Fill(dark ? ImgColor.FromRgba(13, 17, 23, 255) : ImgColor.White);
            if (title is not null) x.DrawText(title, Family(26f), fg, new ImgPointF(padL, 24));

            const int ticks = 4;
            for (var i = 0; i <= ticks; i++)
            {
                var y = h - padB - (float)((h - padT - padB) * i / (double)ticks);
                x.DrawLine(muted, 1f, new ImgPointF(padL, y), new ImgPointF(w - padR, y));
                x.DrawText((maxV * i / ticks).ToString("0.##"), Family(13f), muted, new ImgPointF(8, y - 9));
            }
            if (!string.IsNullOrWhiteSpace(yLabel)) x.DrawText(yLabel!, Family(13f), muted, new ImgPointF(8, padT - 22));
            x.DrawLine(fg, 1.6f, new ImgPointF(padL, padT), new ImgPointF(padL, h - padB));
            x.DrawLine(fg, 1.6f, new ImgPointF(padL, h - padB), new ImgPointF(w - padR, h - padB));

            int nc = cats.Count;
            double slot = (w - padL - padR) / (double)Math.Max(1, nc);
            for (var s = 0; s < series.Count; s++)
            {
                var color = ImgColor.ParseHex(Palette[s % Palette.Length]);
                ImgPointF? prev = null;
                for (var c = 0; c < nc; c++)
                {
                    var v = c < series[s].Values.Length ? series[s].Values[c] : 0;
                    var py = (float)(h - padB - (h - padT - padB) * (v / maxV));
                    var px = (float)(padL + slot * c + slot / 2);
                    var cur = new ImgPointF(px, py);
                    if (prev is not null) x.DrawLine(color, 2.6f, prev.Value, cur);
                    x.Fill(color, new ImgEllipse(new ImgPointF(px, py), 5f));
                    prev = cur;
                }
            }
            for (var c = 0; c < nc; c++)
            {
                var lab = cats[c].Length > 8 ? cats[c].Substring(0, 8) + "…" : cats[c];
                var tw = SixLabors.Fonts.TextMeasurer.MeasureBounds(lab, new SixLabors.Fonts.TextOptions(Family(14f))).Width;
                x.DrawText(lab, Family(14f), fg, new ImgPointF((float)(padL + slot * c + slot / 2 - tw / 2), h - padB + 10));
            }
            if (series.Count > 1)
            {
                float lx = padL, ly = padT - 28;
                for (var s = 0; s < series.Count; s++)
                {
                    var nm = string.IsNullOrEmpty(series[s].Name) ? "系列" + (s + 1) : series[s].Name;
                    x.Fill(ImgColor.ParseHex(Palette[s % Palette.Length]), new ImgRect(lx, ly, 15, 15));
                    x.DrawText(nm, Family(14f), fg, new ImgPointF(lx + 20, ly - 3));
                    lx += 20 + SixLabors.Fonts.TextMeasurer.MeasureBounds(nm, new SixLabors.Fonts.TextOptions(Family(14f))).Width + 24;
                }
            }
        });
        return ToPng(img);
    }

    private static byte[] RenderPie(int w, int h, string? title, List<string> cats, double[] values,
        bool dark, Theme t, bool doughnut = false)
    {
        using var img = new Image<Rgba32>(w, h);
        var fg = dark ? ImgColor.White : ImgColor.Black;
        var bg = dark ? ImgColor.FromRgba(13, 17, 23, 255) : ImgColor.White;
        img.Mutate(x =>
        {
            x.Fill(bg);
            if (title is not null) x.DrawText(title, Family(26f), fg, new ImgPointF(40, 22));

            var positives = values.Select(v => Math.Max(0, v)).ToArray();
            var total = positives.Sum();
            if (total <= 0) { x.DrawText("（无正数数据）", Family(20f), fg, new ImgPointF(60, h / 2f)); return; }

            var size = Math.Min(h - 150, w / 2 - 60);
            var cx = 70 + size / 2f;
            var cy = h / 2f + 22;
            var radius = size / 2f;
            var start = -90f; // 从 12 点开始，顺时针
            for (var i = 0; i < positives.Length; i++)
            {
                if (positives[i] <= 0) continue;
                var sweep = (float)(360.0 * positives[i] / total);
                // 用 PathBuilder 画真正的扇区（旋转椭圆只会得到交叠的圆，不是扇形）
                var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
                pb.AddLine(cx, cy, cx + radius * (float)Math.Cos(start * Math.PI / 180),
                    cy + radius * (float)Math.Sin(start * Math.PI / 180));
                pb.AddArc(cx - radius, cy - radius, radius * 2, radius * 2, start, sweep, 0f);
                pb.CloseFigure();
                x.Fill(ImgColor.ParseHex(Palette[i % Palette.Length]), pb.Build());
                start += sweep;
            }
            if (doughnut)
                x.Fill(bg, new ImgEllipse(new ImgPointF(cx, cy), radius * 0.55f));

            // 图例
            var lx = w / 2f + 40;
            var ly = 100f;
            for (var i = 0; i < cats.Count && i < positives.Length; i++)
            {
                var pct = total > 0 ? positives[i] / total * 100 : 0;
                x.Fill(ImgColor.ParseHex(Palette[i % Palette.Length]), new ImgRect(lx, ly, 16, 16));
                var label = cats[i] + "  " + positives[i].ToString("0.##") + "（" + pct.ToString("0.#") + "%）";
                x.DrawText(label, Family(15f), fg, new ImgPointF(lx + 24, ly - 3));
                ly += 30;
            }
        });
        return ToPng(img);
    }

    private static byte[] ToPng(Image<Rgba32> img)
    {
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    // ===== 落盘 =====
    private static string ResolveOutputPath(JsonElement root, string title)
    {
        var outPath = Str(root, "outputPath");
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            var p = Path.GetFullPath(outPath!.Trim());
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            return p;
        }
        var d = DefaultOutputDir();
        Directory.CreateDirectory(d);
        return UniquePath(d, SafeFileNameFromTitle(title));
    }

    /// <summary>默认输出目录：AGUI_PPTX_OUT &gt; AGUI_DOCX_OUT（复用同一可下载目录）&gt; 主目录/agui-pptx &gt; 临时目录/agui-pptx。</summary>
    private static string DefaultOutputDir()
    {
        foreach (var envName in new[] { "AGUI_PPTX_OUT", "AGUI_DOCX_OUT" })
        {
            var configured = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(configured)) continue;
            try
            {
                var d = Path.GetFullPath(configured.Trim());
                Directory.CreateDirectory(d);
                return d;
            }
            catch { /* 配置路径不可用 → 回退 */ }
        }
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                var d = Path.Combine(home, "agui-pptx");
                Directory.CreateDirectory(d);
                return d;
            }
        }
        catch { /* 无主目录 → 临时目录 */ }
        var tmp = Path.Combine(Path.GetTempPath(), "agui-pptx");
        Directory.CreateDirectory(tmp);
        return tmp;
    }

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
        var sb = new StringBuilder();
        var lastSpace = false;
        foreach (var c in name)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace && lastSpace) continue;
            sb.Append(isSpace ? ' ' : c);
            lastSpace = isSpace;
        }
        name = sb.ToString().Trim().TrimEnd('.');
        if (name.Length == 0) name = "presentation";
        if (name.Length > 80) name = name.Substring(0, 80).TrimEnd();
        var upper = name.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL" || upper.StartsWith("COM") || upper.StartsWith("LPT"))
            name = "_" + name;
        return name;
    }

    private static string UniquePath(string dir, string baseName)
    {
        var candidate = Path.Combine(dir, baseName + ".pptx");
        if (!File.Exists(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(dir, $"{baseName}_{i}.pptx");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.pptx");
    }

    // ===== 通用 =====
    private static string Xml(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s ?? "")
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string Safe(string s) => (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

    private static string ExtractJson(string s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return "{}";
        var fence = new string((char)96, 3);
        if (t.StartsWith(fence, StringComparison.Ordinal))
        {
            var nl = t.IndexOf('\n');
            if (nl > 0) t = t.Substring(nl + 1);
            var close = t.LastIndexOf(fence, StringComparison.Ordinal);
            if (close >= 0) t = t.Substring(0, close);
            t = t.Trim();
        }
        var first = t.IndexOf('{');
        var last = t.LastIndexOf('}');
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
        b.Append("\"");
        return b.ToString();
    }
}
