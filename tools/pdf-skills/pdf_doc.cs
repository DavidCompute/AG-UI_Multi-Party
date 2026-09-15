// ============================================================================
// pdf_doc —— 打印级 PDF 文档生成（设计令牌驱动的排版引擎）
//
// 【何时使用】用户要一份 PDF / 报告 / 方案 / 简历 / 白皮书 / 打印级文档时调用；
//   也用于把已有 Markdown / 纯文本「套上版式重新排版成 PDF」。
//   识别「出个 PDF」「导出成 PDF」「把这段 Markdown 排成 PDF」「做份正式报告」。
// 【设计参考】MiniMax-AI/skills 的 minimax-pdf：由「文档类型」决定配色 / 字体 /
//   留白（设计令牌贯穿每一页），内容以「内容块（block）」而非纯文本组织。
// 【实现】PDFsharp 6.2.4 + 自定义字体解析器。PDFsharp 已是平台依赖（随技能宿主的
//   可信平台程序集 TPA 分发），故**不写 #r "nuget: PdfSharp"**——避免一次多余联网还原。
//   图表用 XGraphics 矢量绘制（不引入 ImageSharp），中文用系统/容器 CJK 字体按族名
//   分发并子集嵌入。
//
// 入口：public static string Run(string input) -> JSON
//   input = {
//     "title": "标题",                        // 可选；缺省取第一个 h1，再缺省「文档」
//     "subtitle": "副标题", "author": "作者", "date": "2026-09",
//     "docType": "report|proposal|resume|academic|minimal|editorial|magazine|terminal",
//     "accent": "1F3864",                    // 可选，覆盖强调色（6 位十六进制，可带 #）
//     "accentRole": "legal|tech|eco|academic|finance|creative|health|luxury",
//     "cover": true,                         // 可选，默认 true（是否画封面）
//     "toc": true | {"title":"目录","items":["…"]},   // 可选，自动插目录（带页码）
//     "pageSize": "A4|Letter", "marginMm": 20,
//     "colors": { "ink":"","bg":"","panel":"","rule":"","muted":"","coverBg":"","coverInk":"" },
//     "fontPath": "/path/to/font.ttf",       // 可选，显式字体（允许 CFF，会警示体积）
//     "outputPath": "/app/docs/x.pdf",       // 可选
//     "blocks": [ … ],                       // 内容块（与 markdown 二选一）
//     "markdown": "# 标题\n\n正文…"           // REFORMAT：把 Markdown/纯文本重排
//   }
//
//   blocks[].type 取值：
//     h1/h2/h3   text
//     p           text（支持 **粗体** / *斜体* / `等宽`）
//     list        items:[…], ordered:false
//     callout     kind:"info|warn|success|danger", title, text
//     quote       text, cite
//     table       headers:[…], rows:[[…]], caption
//     image       path, caption, widthMm
//     chart       chartType:"bar|line|pie|doughnut", title, categories:[…],
//                 series:[{name,values:[…]}], yLabel, heightMm, caption
//     code        code, language
//     divider / caption(text) / pagebreak / spacer(heightMm)
//     toc         title, items:[…]（items 省略时用文档里的 h1/h2 自动生成，带页码）
//
//   返回 = { ok, scene:"pdf", docType, path, pages, blocks, font, warnings?,
//            produce_file:{path,name,bytes}, message }
// ============================================================================

using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

public class Skill
{
    private const string SceneName = "pdf";

    // 字体解析器：PDFsharp 的 GlobalFontSettings.FontResolver 只认第一次赋值，
    // 而技能每次执行都在新的可卸载 ALC 里（类型标识不同）——所以必须把解析器实例与
    // 字体字节放在进程级 AppDomain 数据里复用，否则第二次执行会抛
    // "You must not change font resolver after is was once used."
    private const string RegKey = "agui.pdf.doc.font.resolver";
    private const string FacesKey = "agui.pdf.doc.font.faces";
    private const string FallbackKey = "agui.pdf.doc.font.fallback";

    // 字号与行高（点；1 英寸 = 72 点）
    private const double LhRatio = 1.62;
    private const double BodySize = 10.5;
    private const double SmallSize = 9;
    private const double CaptionSize = 8.5;
    private const double MonoSize = 9.2;
    private const double H1Size = 20;
    private const double H2Size = 15;
    private const double H3Size = 12.5;
    private const double ChartTitleSize = 11;
    private const double CoverTitleSize = 34;

    // ===================== 入口 =====================

    public static string Run(string input)
    {
        var warnings = new List<string>();
        try
        {
            var built = Build(input ?? "", warnings);
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
            var warn = warnings.Count == 0
                ? ""
                : ",\"warnings\":[" + string.Join(",", warnings.Select(Js)) + "]";
            return "{\"ok\":true,\"scene\":" + Js(SceneName)
                + ",\"docType\":" + Js(built.DocType)
                + ",\"path\":" + Js(built.Path)
                + ",\"pages\":" + built.Pages
                + ",\"blocks\":" + built.Blocks
                + ",\"font\":" + Js(built.Font)
                + produce + warn
                + ",\"message\":" + Js("已生成 PDF：" + built.Path + "（共 " + built.Pages + " 页）") + "}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"scene\":" + Js(SceneName)
                + ",\"message\":" + Js("生成失败：" + ex.Message) + "}";
        }
    }

    private sealed class Built
    {
        public string Path = "";
        public int Pages;
        public int Blocks;
        public string DocType = "report";
        public string Font = "";
    }

    private static Built Build(string input, List<string> warnings)
    {
        JsonDocument reqDoc;
        try { reqDoc = JsonDocument.Parse(ExtractJson(input)); }
        catch (JsonException ex) { throw new InvalidOperationException("输入不是合法 JSON：" + ex.Message); }

        using (reqDoc)
        {
            var root = reqDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("输入必须是 JSON 对象（如 {\"title\":\"…\",\"blocks\":[…] }）。");

            var docType = NormalizeDocType(Str(root, "docType") ?? Str(root, "documentType") ?? "report");
            var t = TokensFor(docType, root);

            var blocks = ParseBlocks(root, warnings);
            if (blocks.Count == 0)
                throw new InvalidOperationException(
                    "没有可排版的内容：请提供 blocks 数组（如 [{\"type\":\"h1\",\"text\":\"标题\"},{\"type\":\"p\",\"text\":\"正文\"}]），"
                    + "或提供 markdown / text 字段让技能重排。");

            var title = Str(root, "title");
            if (string.IsNullOrWhiteSpace(title)) title = FirstHeading(blocks) ?? "文档";
            var subtitle = Str(root, "subtitle");
            var author = Str(root, "author");
            var dateText = Str(root, "date");

            var fonts = FindFonts(Str(root, "fontPath"), warnings);
            InstallResolver(fonts, warnings);

            var paper = PaperFor(Str(root, "pageSize"));
            t.PageW = paper.W;
            t.PageH = paper.H;
            var mm = Num(root, "marginMm");
            if (mm.HasValue && mm.Value > 0)
                t.MarginX = t.MarginTop = t.MarginBottom = XUnit.FromMillimeter(mm.Value);

            var cover = Bool(root, "cover") ?? true;
            var outputPath = ResolveOutputPath(root, title!);

            // 目录：显式 toc 块 / toc:true 自动插入（放在封面之后、正文之前）
            var tocReq = ParseTocRequest(root);
            if (tocReq.Enabled && !blocks.Any(x => x.Type == "toc"))
                blocks.Insert(0, new Block { Type = "toc", Title = tocReq.Title, Items = tocReq.Items });
            var tocItems = CollectHeadings(blocks, tocReq.Items);
            var needMeasure = blocks.Any(x => x.Type == "toc");

            var fontsInfo = fonts.Describe;

            // 第一趟（仅当有目录）：只测量页面分布，记录标题页码；产物丢弃
            PassOptions pass1 = new PassOptions
            {
                Cover = cover,
                Save = false,
                OutputPath = null,
                Record = new Dictionary<string, int>(StringComparer.Ordinal),
                TocItems = tocItems,
            };
            if (needMeasure) RenderPass(t, fonts, blocks, title!, subtitle, author, dateText, pass1);

            // 第二趟：正式渲染，目录带页码
            var pass2 = new PassOptions
            {
                Cover = cover,
                Save = true,
                OutputPath = outputPath,
                Record = null,
                TocPages = pass1.Record ?? new Dictionary<string, int>(StringComparer.Ordinal),
                TocItems = tocItems,
            };
            var pages = RenderPass(t, fonts, blocks, title!, subtitle, author, dateText, pass2);

            return new Built
            {
                Path = outputPath,
                Pages = pages,
                Blocks = blocks.Count,
                DocType = docType,
                Font = fontsInfo,
            };
        }
    }

    // ===================== 渲染上下文 =====================

    private sealed class PassOptions
    {
        public bool Cover = true;
        public bool Save;
        public string? OutputPath;
        public Dictionary<string, int>? Record;
        public Dictionary<string, int> TocPages = new(StringComparer.Ordinal);
        public List<string> TocItems = new();
    }

    private sealed class FontTriple
    {
        public double Size;
        public XFont Regular = null!, Bold = null!, Italic = null!, BoldItalic = null!, Mono = null!;
    }

    private sealed class Ctx
    {
        public PdfDocument Doc = null!;
        public Tokens T = null!;
        public FontPlan Fonts = null!;
        public PassOptions Pass = null!;
        public XGraphics Gfx = null!;
        public FontTriple H1F = null!, H2F = null!, H3F = null!;
        public FontTriple BodyF = null!, SmallF = null!, CaptionF = null!, MonoF = null!;
        public FontTriple CoverTitleF = null!, CoverSubF = null!, KickerF = null!, ChartTitleF = null!;
        public double Left, Right, ContentW, Top, Bottom, Y;
        public int PageNo, ContentPageNo;
        public string RunningTitle = "";
    }

    private static void BindFonts(Ctx c)
    {
        var t = c.T;
        var body = t.Serif ? c.Fonts.SerifFamily : c.Fonts.PrimaryFamily;
        var head = t.Serif ? c.Fonts.SerifFamily : c.Fonts.PrimaryFamily;
        c.BodyF = Triple(c, body, BodySize);
        c.SmallF = Triple(c, body, SmallSize);
        c.CaptionF = Triple(c, body, CaptionSize);
        c.MonoF = Triple(c, c.Fonts.PrimaryFamily, MonoSize);
        c.H1F = Triple(c, head, H1Size);
        c.H2F = Triple(c, head, H2Size);
        c.H3F = Triple(c, head, H3Size);
        c.ChartTitleF = Triple(c, head, ChartTitleSize);
        c.CoverTitleF = Triple(c, head, CoverTitleSize);
        c.CoverSubF = Triple(c, body, 13);
        c.KickerF = Triple(c, body, 9);
    }

    private static FontTriple Triple(Ctx c, string family, double size)
    {
        return new FontTriple
        {
            Size = size,
            Regular = MakeFont(family, size, XFontStyleEx.Regular),
            Bold = MakeFont(family, size, XFontStyleEx.Bold),
            Italic = MakeFont(family, size, XFontStyleEx.Italic),
            BoldItalic = MakeFont(family, size, XFontStyleEx.BoldItalic),
            Mono = MakeFont(c.Fonts.PrimaryFamily, size, XFontStyleEx.Regular),
        };
    }

    private static XFont MakeFont(string family, double size, XFontStyleEx style)
    {
        try { return new XFont(family, size, style); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "无法创建字体 " + family + "（" + size.ToString("0.#", CultureInfo.InvariantCulture) + "pt）：" + ex.Message
                + "。请确认 AGUI_PDF_FONT / fontPath 指向可用字体，或在容器里安装 fonts-wqy-microhei / fonts-droid-fallback。");
        }
    }

    private static double Lh(double size) => size * LhRatio;

    // ===================== 设计令牌 =====================

    private sealed class Tokens
    {
        public string DocType = "report";
        public string Label = "报告";
        public string Accent = "1F3864";
        public string AccentDark = "14213A";
        public string AccentLight = "E8EDF5";
        public string Ink = "1A1A1A";
        public string Muted = "6B6B6B";
        public string Bg = "FFFFFF";
        public string Panel = "F4F6F8";
        public string Rule = "D8DEE4";
        public string CoverBg = "1F3864";
        public string CoverInk = "FFFFFF";
        public bool Dark;
        public bool Serif;
        public double PageW = 595.28, PageH = 841.89;
        public double MarginX = 56.7, MarginTop = 56.7, MarginBottom = 56.7;
    }

    private static readonly Dictionary<string, string> AccentRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["legal"] = "1F3864",     // 法务 → 深藏青
        ["tech"] = "1B6CA8",      // 科技 → 钢蓝
        ["eco"] = "2E6B3E",       // 环保 → 森林绿
        ["academic"] = "0F4C4C",  // 学术 → 深青
        ["finance"] = "14415E",   // 金融 → 藏蓝
        ["creative"] = "B03A2E",  // 创意 → 砖红
        ["health"] = "1E6B62",    // 医疗 → 青绿
        ["luxury"] = "6B4E2E",    // 高端 → 棕金
    };

    /// <summary>
    /// 18 套命名调色板（与 pptx / xlsx 同一套，来自 design-system.md）。
    ///
    /// <para>
    /// 这里只给“品牌三色”：主色（封面/标题）、底色、强调色；其余（AccentDark / AccentLight /
    /// Panel / Rule / Muted）继续由下方既有逻辑从这三色派生——不重复造一套派生规则。
    /// </para>
    /// </summary>
    private static readonly (string Name, string C1, string C2, string C3, string C4, string C5, bool Dark)[] PdfPalettes =
    [
        ("modern-wellness",      "006D77", "83C5BE", "EDF6F9", "FFDDD2", "E29578", false),
        ("business-authority",   "2B2D42", "8D99AE", "EDF2F4", "EF233C", "D90429", false),
        ("nature-outdoors",      "606C38", "283618", "FEFAE0", "DDA15E", "BC6C25", false),
        ("vintage-academic",     "780000", "C1121F", "FDF0D5", "003049", "669BBC", false),
        ("soft-creative",        "CDB4DB", "FFC8DD", "FFAFCC", "BDE0FE", "A2D2FF", false),
        ("bohemian",             "CCD5AE", "E9EDC9", "FEFAE0", "FAEDCD", "D4A373", false),
        ("vibrant-tech",         "8ECAE6", "219EBC", "023047", "FFB703", "FB8500", false),
        ("craft-artisan",        "7F5539", "A68A64", "EDE0D4", "656D4A", "414833", false),
        ("tech-night",           "000814", "001D3D", "003566", "FFC300", "FFD60A", true),
        ("education-charts",     "264653", "2A9D8F", "E9C46A", "F4A261", "E76F51", false),
        ("forest-eco",           "DAD7CD", "A3B18A", "588157", "3A5A40", "344E41", false),
        ("elegant-fashion",      "EDAFB8", "F7E1D7", "DEDBD2", "B0C4B1", "4A5759", false),
        ("art-food",             "335C67", "FFF3B0", "E09F3E", "9E2A2B", "540B0E", false),
        ("luxury-mysterious",    "22223B", "4A4E69", "9A8C98", "C9ADA7", "F2E9E4", false),
        ("pure-tech-blue",       "03045E", "0077B6", "00B4D8", "90E0EF", "CAF0F8", false),
        ("coastal-coral",        "0081A7", "00AFB9", "FDFCDC", "FED9B7", "F07167", false),
        ("vibrant-orange-mint",  "FF9F1C", "FFBF69", "FFFFFF", "CBF3F0", "2EC4B6", false),
        ("platinum-white-gold",  "0A0A0A", "0070F3", "D4AF37", "F5F5F5", "FFFFFF", false),
    ];

    private static double RelLumP(string hex)
    {
        var c = RgbP(hex);
        double Ch(int v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c[0]) + 0.7152 * Ch(c[1]) + 0.0722 * Ch(c[2]);
    }

    private static int[] RgbP(string hex) =>
        [Convert.ToInt32(hex.Substring(0, 2), 16), Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16)];

    private static double ContrastP(string a, string b)
    {
        var la = RelLumP(a); var lb = RelLumP(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>该底色上最易读的文字色（黑或白）。</summary>
    private static string OnColorP(string bg)
        => ContrastP("FFFFFF", bg) >= ContrastP("1A1A1A", bg) ? "FFFFFF" : "1A1A1A";

    private static double ChromaP(string hex)
    {
        var c = RgbP(hex);
        var mx = c.Max(); var mn = c.Min();
        return mx == 0 ? 0 : (mx - mn) / (double)mx;
    }

    /// <summary>把亮色压成“底色”：彩度超标就往白里混（否则会得到一张亮黄底的文档）。</summary>
    private static string SurfaceP(string color)
    {
        if (ChromaP(color) <= 0.25) return color;
        for (var t = 0.1; t <= 0.91; t += 0.1)
        {
            var s = Mix(color, "FFFFFF", t);
            if (ChromaP(s) <= 0.25) return s;
        }
        return "FFFFFF";
    }

    /// <summary>强调色：优先选“在底色上看得见”且彩度最高的那个（不选已当底色/主色的）。</summary>
    private static string AccentFrom(string[] all, string bg, params string[] exclude)
    {
        var pool = all.Where(c => c != bg && !exclude.Contains(c) && ContrastP(c, bg) >= 2.0).ToList();
        if (pool.Count == 0) pool = all.Where(c => c != bg).ToList();
        if (pool.Count == 0) pool = all.ToList();
        return pool.OrderByDescending(ChromaP).First();
    }

    private static string NormalizeDocType(string raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        switch (s)
        {
            case "proposal": case "plan": case "方案": return "proposal";
            case "resume": case "cv": case "简历": return "resume";
            case "academic": case "paper": case "学术": case "论文": return "academic";
            case "minimal": case "simple": case "简洁": return "minimal";
            case "editorial": case "column": case "专栏": return "editorial";
            case "magazine": case "杂志": return "magazine";
            case "terminal": case "code": case "终端": return "terminal";
            default: return "report";
        }
    }

    private static Tokens TokensFor(string docType, JsonElement root)
    {
        var t = new Tokens { DocType = docType };
        switch (docType)
        {
            case "proposal":
                t.Label = "方案"; t.Accent = "1B6CA8";
                t.CoverBg = "FFFFFF"; t.CoverInk = "1A1A1A"; break;
            case "resume":
                t.Label = "简历"; t.Accent = "0F4C5C"; t.MarginX = 64;
                t.CoverBg = "FFFFFF"; t.CoverInk = "1A1A1A"; break;
            case "academic":
                t.Label = "学术"; t.Accent = "0F4C4C"; t.Bg = "F8F5EE"; t.Serif = true; t.MarginX = 68;
                t.CoverBg = "F8F5EE"; t.CoverInk = "2B2B2B"; break;
            case "minimal":
                t.Label = "文档"; t.Accent = "222222"; t.MarginX = 72;
                t.CoverBg = "FFFFFF"; t.CoverInk = "111111"; break;
            case "editorial":
                t.Label = "专栏"; t.Accent = "B03A2E"; t.Serif = true;
                t.CoverBg = "FFFFFF"; t.CoverInk = "1A1A1A"; break;
            case "magazine":
                t.Label = "杂志"; t.Accent = "B4533C"; t.Bg = "FDF6EF"; t.Serif = true;
                t.CoverBg = "FDF6EF"; t.CoverInk = "3A2E2A"; break;
            case "terminal":
                t.Label = "终端"; t.Accent = "35E07A"; t.Bg = "0B0F0C"; t.Ink = "D7E4DA";
                t.Dark = true; t.CoverBg = "0B0F0C"; t.CoverInk = "D7E4DA"; break;
            default: // report
                t.Label = "报告"; t.Accent = "1F3864";
                t.CoverBg = "1F3864"; t.CoverInk = "FFFFFF"; break;
        }

        // 命名调色板（设计系统）：一次给出品牌主色 / 底色 / 强调色，其余交给下面既有的派生逻辑。
        // 放在 docType 之后、accentRole / accent / colors 之前 —— 显式指定的单项始终优先。
        var palName = (Str(root, "palette") ?? "").Trim().ToLowerInvariant();
        if (palName.Length > 0)
        {
            foreach (var p in PdfPalettes)
            {
                if (p.Name != palName) continue;
                var all = new[] { p.C1, p.C2, p.C3, p.C4, p.C5 }
                    .Select(c => c.TrimStart('#').ToUpperInvariant()).ToArray();
                var byLum = all.OrderBy(RelLumP).ToArray();
                var primary = byLum[0];
                t.Dark = p.Dark;
                t.Bg = p.Dark ? primary : SurfaceP(byLum[4]);
                t.Ink = p.Dark ? "E8ECF1" : "1F1F1F";
                t.Accent = AccentFrom(all, t.Bg, primary);
                t.CoverBg = p.Dark ? t.Bg : primary;
                t.CoverInk = OnColorP(t.CoverBg);
                break;
            }
        }

        // 强调色：语义角色 → 显式覆盖
        var role = Str(root, "accentRole");
        if (!string.IsNullOrWhiteSpace(role) && AccentRoles.TryGetValue(role!.Trim(), out var byRole))
            t.Accent = byRole;
        var accent = HexOrNull(Str(root, "accent"));
        if (accent is not null) t.Accent = accent!;

        if (root.TryGetProperty("colors", out var col) && col.ValueKind == JsonValueKind.Object)
        {
            t.Ink = HexOrNull(Str(col, "ink")) ?? t.Ink;
            t.Bg = HexOrNull(Str(col, "bg")) ?? t.Bg;
            t.Panel = HexOrNull(Str(col, "panel")) ?? t.Panel;
            t.Rule = HexOrNull(Str(col, "rule")) ?? t.Rule;
            t.Muted = HexOrNull(Str(col, "muted")) ?? t.Muted;
            t.CoverBg = HexOrNull(Str(col, "coverBg")) ?? t.CoverBg;
            t.CoverInk = HexOrNull(Str(col, "coverInk")) ?? t.CoverInk;
        }

        t.AccentDark = Darken(t.Accent, 0.30);
        t.AccentLight = Mix(t.Accent, t.Bg, 0.90);
        if (t.Dark)
        {
            t.Panel = Mix(t.Ink, t.Bg, 0.92);
            t.Rule = Mix(t.Ink, t.Bg, 0.78);
            t.Muted = Mix(t.Ink, t.Bg, 0.42);
        }
        else
        {
            t.Panel = Mix(t.Ink, t.Bg, 0.94);
            t.Rule = Mix(t.Ink, t.Bg, 0.82);
            t.Muted = Mix(t.Ink, t.Bg, 0.45);
        }
        return t;
    }

    private static (double W, double H) PaperFor(string? size)
    {
        var s = (size ?? "A4").Trim().ToUpperInvariant();
        return s == "LETTER" ? (612.0, 792.0) : (595.28, 841.89);
    }

    // ===================== 字体发现与解析器 =====================

    private sealed class FontPlan
    {
        public string PrimaryFamily = "AGUI_CJK";
        public string SerifFamily = "AGUI_CJK";
        public string PrimaryPath = "";
        public string SerifPath = "";
        public byte[]? PrimaryBytes;
        public byte[]? SerifBytes;
        public bool IsCff;
        /// <summary>降级模式：无法安装自带解析器，改用宿主已安装的解析器（族名即宿主可解析的名字）。</summary>
        public bool HostResolver;
        public string Describe = "";
    }

    /// <summary>
    /// 一次性安装「按族名分发」的字体解析器。
    ///
    /// 关键约束：<c>GlobalFontSettings.FontResolver</c> / <c>FallbackFontResolver</c> 的 setter 在字体工厂
    /// 启用后会抛 <c>InvalidOperationException</c>（只允许设置一次），而技能每次执行都编译进<b>新的 ALC</b>
    /// （同名类型在跨 ALC 时 Type 标识不同，PDFsharp 的「同类型忽略」判断拦不住）。
    /// 因此解析器实例与字形字节都存进 AppDomain 进程级数据：首次执行安装，之后复用同一实例。
    ///
    /// 槽位选择：<b>优先占「回退解析器」槽</b>。主槽留给宿主/其它 PDFsharp 使用者（互不干扰），
    /// 而 PDFsharp 在主解析器未设置或返回 null 时会查回退解析器——我们的族名照常解析（已实测）。
    ///
    /// 降级路径：若两个槽位都已不可写（同一进程里<b>已经有人渲染过 PDF 文字</b> —— PDFsharp 在字体工厂
    /// 启用后即冻结两个 setter），则改为<b>复用宿主已安装的解析器</b>：直接问它能不能解析几个常见中文字体族名，
    /// 能就用那个族名渲染（字体仍会被 PDFsharp 正常嵌入并子集化）。
    /// 这不是测试专用路径：所有 dotnet 技能都跑在 Web 同一进程里，任何一个先渲染 PDF 文字的自建技能
    /// 都会触发它，因此必须真实处理而不是直接报错。
    /// </summary>
    private static void InstallResolver(FontPlan plan, List<string> warnings)
    {
        // 已是「复用宿主解析器」降级模式：字体族名由宿主提供，无需也没法再安装解析器。
        if (plan.HostResolver) return;
        var dict = AppDomain.CurrentDomain.GetData(FacesKey) as Dictionary<string, byte[]>;
        if (dict is null)
        {
            dict = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            AppDomain.CurrentDomain.SetData(FacesKey, dict);
        }
        dict[plan.PrimaryFamily] = plan.PrimaryBytes!;
        if (plan.SerifBytes is not null) dict[plan.SerifFamily] = plan.SerifBytes;
        AppDomain.CurrentDomain.SetData(FallbackKey, plan.PrimaryFamily);

        var existing = AppDomain.CurrentDomain.GetData(RegKey) as IFontResolver;
        if (existing is not null)
        {
            if (!ReferenceEquals(GlobalFontSettings.FallbackFontResolver, existing)
                && !ReferenceEquals(GlobalFontSettings.FontResolver, existing))
            {
                try { GlobalFontSettings.FallbackFontResolver = existing; }
                catch { try { GlobalFontSettings.FontResolver = existing; } catch { } }
            }
            return;
        }

        var reg = new FontRegistry();
        Exception? failure = null;
        try { GlobalFontSettings.FallbackFontResolver = reg; }
        catch (Exception ex) { failure = ex; }
        if (failure is not null)
        {
            failure = null;
            try { GlobalFontSettings.FontResolver = reg; }
            catch (Exception ex) { failure = ex; }
        }
        if (failure is not null)
        {
            if (TryUseHostResolver(plan, warnings)) return;
            throw new InvalidOperationException(
                "PDF 字体解析器无法安装：本进程内已有人初始化过 PDFsharp 的字体工厂，插槽已不可写（"
                + failure.Message + "）。PDFsharp 只允许安装一次，且其内置平台解析器只有拉丁字形。"
                + "请重启服务后重试；若是自建的 dotnet 技能先渲染了 PDF 文字，请改用本内置技能来出 PDF。");
        }
        AppDomain.CurrentDomain.SetData(RegKey, reg);
    }

    /// <summary>
    /// 降级（尽力而为）：复用进程内<b>已安装</b>的解析器。
    /// <para>
    /// 两个 setter 都被冻结时，我们无法再注册自己的字体字节；但若已安装的那个解析器本身能解析中文
    /// （例如将来平台在 Linux 上装了基于 fontconfig 的系统字体解析器），就可以直接用它的族名渲染，
    /// 渲染路径无需任何改动。若它只认得拉丁字体（如 PDFsharp 内置的平台解析器），则本方法返回 false，
    /// 由调用方给出可读错误。
    /// </para>
    /// </summary>
    private static bool TryUseHostResolver(FontPlan plan, List<string> warnings)
    {
        var own = AppDomain.CurrentDomain.GetData(RegKey) as IFontResolver;
        foreach (var existing in HostResolvers())
        {
            // 本技能自己先前装上的注册器不算“宿主”：它总是返回一个 FontResolverInfo（未知族名就回退到
            // 第一个已注册字形），拿它当宿主体检会假阳性。这种情况由 InstallResolver 的已有实例分支处理。
            if (ReferenceEquals(existing, own)) continue;
            foreach (var family in HostFontFamilies())
            {
                FontResolverInfo? info = null;
                try { info = existing.ResolveTypeface(family, false, false); }
                catch { continue; }
                if (info is null) continue;

                plan.PrimaryFamily = family;
                plan.SerifFamily = family;
                plan.SerifBytes = null;
                plan.SerifPath = "";
                plan.HostResolver = true;
                plan.Describe = family + "（复用宿主已安装的字体解析器）";
                warnings.Add("本进程内已有人初始化过 PDFsharp 字体工厂，无法安装本技能自带的解析器："
                    + "已改为复用宿主解析器并选用其中文字体 " + family
                    + "。此模式下 fontPath / AGUI_PDF_FONT 会被忽略。");
                return true;
            }
        }
        return false;
    }

    /// <summary>取进程内可能已安装的解析器（主槽 + 回退槽）；未设置或读取异常则跳过。</summary>
    private static IEnumerable<IFontResolver> HostResolvers()
    {
        var main = TryGetResolver(() => GlobalFontSettings.FontResolver);
        if (main is not null) yield return main;
        var fb = TryGetResolver(() => GlobalFontSettings.FallbackFontResolver);
        if (fb is not null && !ReferenceEquals(fb, main)) yield return fb;
    }

    private static IFontResolver? TryGetResolver(Func<IFontResolver> get)
    {
        try { return get(); } catch { return null; }
    }

    /// <summary>宿主解析器可能认得的中文字体族名（英文名优先，再补中文本名）。</summary>
    private static IEnumerable<string> HostFontFamilies()
    {
        yield return "Microsoft YaHei";
        yield return "SimHei";
        yield return "SimSun";
        yield return "Noto Sans CJK SC";
        yield return "Noto Sans CJK JP";
        yield return "Source Han Sans SC";
        yield return "WenQuanYi Micro Hei";
        yield return "Droid Sans Fallback";
        yield return "PingFang SC";
        yield return "Hiragino Sans GB";
        yield return "Arial Unicode MS";
        yield return "微软雅黑";
        yield return "黑体";
        yield return "宋体";
    }

    /// <summary>按族名分发的解析器；字体字节与回退族名都从 AppDomain 数据实时读取（跨 ALC 安全）。</summary>
    private sealed class FontRegistry : IFontResolver
    {
        public byte[]? GetFont(string faceName)
        {
            var faces = AppDomain.CurrentDomain.GetData(FacesKey) as Dictionary<string, byte[]>;
            return faces is not null && faces.TryGetValue(faceName, out var b) ? b : null;
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var faces = AppDomain.CurrentDomain.GetData(FacesKey) as Dictionary<string, byte[]>;
            if (faces is null || faces.Count == 0) return null;
            var face = faces.ContainsKey(familyName)
                ? familyName
                : (AppDomain.CurrentDomain.GetData(FallbackKey) as string) ?? "";
            if (!faces.ContainsKey(face)) face = faces.Keys.First();
            // 只有一个字形文件时，粗体/斜体交给 PDFsharp 做合成（仿真）
            return new FontResolverInfo(face, isBold, isItalic);
        }
    }

    private static FontPlan FindFonts(string? fontPathFromInput, List<string> warnings)
    {
        var explicitPath = !string.IsNullOrWhiteSpace(fontPathFromInput)
            ? fontPathFromInput!.Trim()
            : Environment.GetEnvironmentVariable("AGUI_PDF_FONT");

        if (!string.IsNullOrWhiteSpace(explicitPath))
            return LoadExplicit(explicitPath!, warnings);

        // 自动探测：优先 glyf（可子集化，产物小）；CFF 仅兜底并警示
        var primaryPath = "";
        byte[] primaryBytes = null!;
        var isCff = false;
        var cffPath = "";
        byte[] cffBytes = null!;
        foreach (var path in PrimaryCandidates())
        {
            if (!File.Exists(path)) continue;
            var loaded = TryLoadFont(path);
            if (loaded is null) continue;
            if (loaded.Value.IsCff)
            {
                if (cffPath.Length == 0) { cffPath = path; cffBytes = loaded.Value.Bytes; }
                continue;
            }
            primaryPath = path;
            primaryBytes = loaded.Value.Bytes;
            warnings.Add("自动选用中文字体：" + path);
            break;
        }
        if (primaryPath.Length == 0)
        {
            if (cffPath.Length == 0)
                throw new InvalidOperationException(
                    "未找到可用的中文字体：生成 PDF 需要一份含中文字形的字体。"
                    + "Linux 容器请安装 fonts-wqy-microhei 或 fonts-droid-fallback"
                    + "（DroidSansFallbackFull.ttf / wqy-microhei.ttc）；"
                    + "也可以用环境变量 AGUI_PDF_FONT 或参数 fontPath 指定 .ttf/.ttc 字体路径。");
            primaryPath = cffPath;
            primaryBytes = cffBytes;
            isCff = true;
            warnings.Add("未找到 glyf(TrueType 轮廓) 中文字体，退回 CFF 字体：" + cffPath
                + "。PDFsharp 不对 CFF 字体做子集化，产出 PDF 会非常大（可能 10MB 以上）。"
                + "建议安装 fonts-wqy-microhei / fonts-droid-fallback，或用 AGUI_PDF_FONT 指定含 glyf 的字体。");
        }
        return PlanFor(primaryPath, primaryBytes, isCff, warnings);
    }

    private static FontPlan LoadExplicit(string path, List<string> warnings)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                "指定的字体文件不存在：" + path
                + "。请检查 fontPath / AGUI_PDF_FONT，或去掉该设置让平台自动探测中文字体。");
        var loaded = TryLoadFont(path);
        if (loaded is null)
            throw new InvalidOperationException("指定的字体文件无法识别为 TTF/TTC 字体：" + path + "。");
        warnings.Add(loaded.Value.IsCff
            ? "指定的字体是 CFF/OTTO（" + Path.GetFileName(path) + "）：PDFsharp 不对 CFF 字体做子集化，产出 PDF 会很大（可能 10MB 以上）。建议改用 glyf/TrueType 字体。"
            : "使用指定的中文字体：" + path);
        return PlanFor(path, loaded.Value.Bytes, loaded.Value.IsCff, warnings);
    }

    private static FontPlan PlanFor(string primaryPath, byte[] primaryBytes, bool isCff, List<string> warnings)
    {
        var plan = new FontPlan
        {
            PrimaryFamily = "AGUI_CJK_" + Hash8(primaryBytes),
            SerifFamily = "AGUI_CJK_" + Hash8(primaryBytes),
            PrimaryPath = primaryPath,
            PrimaryBytes = primaryBytes,
            IsCff = isCff,
        };

        // 可选：另找一份衬线中文字体，让 academic / editorial / magazine 更像印刷品
        if (!isCff)
        {
            foreach (var sp in SerifCandidates())
            {
                if (string.Equals(sp, primaryPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(sp)) continue;
                var s = TryLoadFont(sp);
                if (s is null || s.Value.IsCff) continue;
                plan.SerifFamily = "AGUI_SERIF_" + Hash8(s.Value.Bytes);
                plan.SerifPath = sp;
                plan.SerifBytes = s.Value.Bytes;
                warnings.Add("另启用衬线中文字体：" + sp);
                break;
            }
        }

        plan.Describe = Path.GetFileName(primaryPath)
            + "（" + (isCff ? "CFF/OTTO，未子集化" : "glyf/TTF，已子集化嵌入") + "）"
            + (plan.SerifPath.Length == 0 ? "" : " + 衬线 " + Path.GetFileName(plan.SerifPath));
        return plan;
    }

    private static IEnumerable<string> PrimaryCandidates()
    {
        // Linux（官方 aspnet 镜像不含字体，见 tools/docx-skills/README.md）
        yield return "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf";
        yield return "/usr/share/fonts/truetype/droid/DroidSansFallback.ttf";
        yield return "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc";
        yield return "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc";
        yield return "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc";
        yield return "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc"; // CFF：仅兜底
        yield return "/usr/share/fonts/truetype/noto/NotoSansCJKsc-Regular.otf";
        // Windows
        yield return @"C:\Windows\Fonts\simhei.ttf";
        yield return @"C:\Windows\Fonts\Deng.ttf";
        yield return @"C:\Windows\Fonts\simkai.ttf";
        yield return @"C:\Windows\Fonts\msyh.ttc";
        yield return @"C:\Windows\Fonts\simsun.ttc";
        // macOS
        yield return "/System/Library/Fonts/PingFang.ttc";
        yield return "/System/Library/Fonts/Hiragino Sans GB.ttc";
        yield return "/Library/Fonts/Arial Unicode.ttf";
    }

    private static IEnumerable<string> SerifCandidates()
    {
        yield return "/usr/share/fonts/opentype/noto/NotoSerifCJK-Regular.ttc";
        yield return "/usr/share/fonts/truetype/noto/NotoSerifCJK-Regular.ttc";
        yield return "/usr/share/fonts/truetype/arphic/uming.ttc";
        yield return @"C:\Windows\Fonts\simsun.ttc";
        yield return @"C:\Windows\Fonts\simfang.ttf";
        yield return "/System/Library/Fonts/Supplemental/Songti.ttc";
    }

    /// <summary>读字体文件并取出可用的单字体 sfnt 字节；不是可识别字体时返回 null。IsCff=true 表示 CFF/OTTO。</summary>
    private static (byte[] Bytes, bool IsCff)? TryLoadFont(string path)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            var face = TtcFace.ExtractFace(raw, 0);
            if (face is null || face.Length < 12) return null;
            var tag = BinaryPrimitives.ReadUInt32BigEndian(face);
            if (tag == 0x4F54544F) return (face, true);                        // 'OTTO' → CFF
            if (tag is 0x00010000 or 0x74727565) return (face, false);         // glyf TTF / true
            return null;
        }
        catch { return null; }
    }

    private static string Hash8(byte[] b)
    {
        unchecked
        {
            uint h = 2166136261;
            var n = Math.Min(b.Length, 262144);
            for (var i = 0; i < n; i++) { h ^= b[i]; h *= 16777619; }
            h ^= (uint)b.Length;
            h *= 16777619;
            return h.ToString("x8");
        }
    }

    /// <summary>
    /// TTC（TrueType Collection）→ 独立单字体 sfnt 字节。
    /// 表数据按原文件偏移整体拷贝，重排表目录并重算偏移；表校验和只依赖表内容，故可原样沿用。
    /// 只依赖 System.Buffers.Binary（自包含）。
    /// </summary>
    private static class TtcFace
    {
        private const uint TtcTag = 0x74746366; // 'ttcf'

        /// <summary>返回单字体字节；输入本就是单字体时原样返回；不是可识别的 sfnt 时返回 null。</summary>
        public static byte[]? ExtractFace(byte[] raw, int faceIndex)
        {
            if (raw.Length < 12) return null;
            var tag = BinaryPrimitives.ReadUInt32BigEndian(raw);
            if (tag != TtcTag)
            {
                // 'OTTO' / 0x00010000 / 'true' —— 已是单字体
                return tag is 0x4F54544F or 0x00010000 or 0x74727565 ? raw : null;
            }
            var numFonts = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(8));
            if (faceIndex < 0 || faceIndex >= numFonts) faceIndex = 0;
            var offPos = 12 + faceIndex * 4;
            if (raw.Length < offPos + 4) return null;
            var faceOffset = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(offPos));
            if (faceOffset + 12 > raw.Length) return null;

            var sfntVersion = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan((int)faceOffset));
            var numTables = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan((int)faceOffset + 4));
            if (numTables == 0 || numTables > 512) return null;
            var dirEnd = faceOffset + 12 + numTables * 16;
            if (dirEnd > raw.Length) return null;

            var tables = new List<(string Tag, uint Checksum, byte[] Data)>(numTables);
            for (var i = 0; i < numTables; i++)
            {
                var p = (int)(faceOffset + 12 + i * 16);
                var t = Encoding.ASCII.GetString(raw, p, 4);
                var sum = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(p + 4));
                var offset = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(p + 8));
                var length = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(p + 12));
                if (offset + length > raw.Length) return null;
                tables.Add((t, sum, raw.AsSpan((int)offset, (int)length).ToArray()));
            }

            // 头部大小 + 每表 16 字节目录，表数据逐个 4 字节对齐
            var dataStart = 12 + numTables * 16;
            var total = dataStart;
            var aligned = new List<int>(numTables);
            foreach (var (_, _, data) in tables)
            {
                aligned.Add((total + 3) & ~3);
                total = aligned[aligned.Count - 1] + ((data.Length + 3) & ~3);
            }

            var outBytes = new byte[total];
            BinaryPrimitives.WriteUInt32BigEndian(outBytes, sfntVersion);
            BinaryPrimitives.WriteUInt16BigEndian(outBytes.AsSpan(4), (ushort)numTables);
            var maxPow2 = 1;
            while (maxPow2 * 2 <= numTables) maxPow2 *= 2;
            BinaryPrimitives.WriteUInt16BigEndian(outBytes.AsSpan(6), (ushort)(maxPow2 * 16));      // searchRange
            BinaryPrimitives.WriteUInt16BigEndian(outBytes.AsSpan(8), (ushort)Math.Log2(maxPow2));  // entrySelector
            BinaryPrimitives.WriteUInt16BigEndian(outBytes.AsSpan(10), (ushort)(numTables * 16 - maxPow2 * 16)); // rangeShift

            for (var i = 0; i < numTables; i++)
            {
                var p = 12 + i * 16;
                Encoding.ASCII.GetBytes(tables[i].Tag, outBytes.AsSpan(p, 4));
                BinaryPrimitives.WriteUInt32BigEndian(outBytes.AsSpan(p + 4), tables[i].Checksum);
                BinaryPrimitives.WriteUInt32BigEndian(outBytes.AsSpan(p + 8), (uint)aligned[i]);
                BinaryPrimitives.WriteUInt32BigEndian(outBytes.AsSpan(p + 12), (uint)tables[i].Data.Length);
                tables[i].Data.CopyTo(outBytes, aligned[i]);
            }
            return outBytes;
        }
    }

    // ===================== 页面与流式布局 =====================

    private static int RenderPass(Tokens t, FontPlan fonts, List<Block> blocks, string title,
        string? subtitle, string? author, string? date, PassOptions pass)
    {
        var doc = new PdfDocument();
        try
        {
            doc.Info.Title = title;
            doc.Info.Author = string.IsNullOrWhiteSpace(author) ? "AG-UI 数字员工平台" : author!;
            doc.Info.Creator = "agui pdf_doc";
        }
        catch { /* 元信息失败不影响产出 */ }

        var c = new Ctx { Doc = doc, T = t, Fonts = fonts, Pass = pass, RunningTitle = title };
        BindFonts(c);
        c.Left = t.MarginX;
        c.Right = t.PageW - t.MarginX;
        c.ContentW = c.Right - c.Left;
        c.Top = t.MarginTop;
        c.Bottom = t.PageH - t.MarginBottom;

        var pages = 0;
        try
        {
            if (pass.Cover)
            {
                NewCoverPage(c);
                DrawCover(c, title, subtitle, author, date);
            }
            NewContentPage(c);
            foreach (var b in blocks) RenderBlock(c, b);
            pages = c.PageNo;
            c.Gfx?.Dispose();
            c.Gfx = null!;
            if (pass.Save)
            {
                try { doc.Save(pass.OutputPath ?? ""); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("无法写入 PDF 文件（" + pass.OutputPath + "）：" + ex.Message);
                }
            }
        }
        finally
        {
            try { c.Gfx?.Dispose(); } catch { }
            doc.Dispose();
        }
        return pages;
    }

    private static void NewCoverPage(Ctx c)
    {
        try { c.Gfx?.Dispose(); } catch { }
        var page = c.Doc.AddPage();
        page.Width = XUnit.FromPoint(c.T.PageW);
        page.Height = XUnit.FromPoint(c.T.PageH);
        c.Gfx = XGraphics.FromPdfPage(page);
        c.PageNo++;
        c.Y = c.Top;
        if (c.T.Dark) c.Gfx.DrawRectangle(Brush(c.T.Bg), 0, 0, c.T.PageW, c.T.PageH);
    }

    private static void NewContentPage(Ctx c)
    {
        try { c.Gfx?.Dispose(); } catch { }
        var page = c.Doc.AddPage();
        page.Width = XUnit.FromPoint(c.T.PageW);
        page.Height = XUnit.FromPoint(c.T.PageH);
        c.Gfx = XGraphics.FromPdfPage(page);
        c.PageNo++;
        c.ContentPageNo++;
        c.Y = c.Top;
        DrawChrome(c);
    }

    private static void Need(Ctx c, double h)
    {
        // c.Y > Top 才分页：避免「单块高于一页」时反复分页死循环
        if (c.Y > c.Top + 0.5 && c.Y + h > c.Bottom) NewContentPage(c);
    }

    private static void DrawChrome(Ctx c)
    {
        var t = c.T;
        var g = c.Gfx;
        if (t.Dark) g.DrawRectangle(Brush(t.Bg), 0, 0, t.PageW, t.PageH);
        var headerY = t.MarginTop - 26;
        g.DrawString(Truncate(c, c.RunningTitle, c.CaptionF.Regular, c.ContentW * 0.62), c.CaptionF.Regular,
            Brush(t.Muted), c.Left, headerY, XStringFormats.TopLeft);
        g.DrawString(t.Label, c.CaptionF.Bold, Brush(t.Accent),
            new XRect(c.Left, headerY, c.ContentW, 14), XStringFormats.TopRight);
        g.DrawLine(new XPen(ColorOf(t.Rule), 0.7), c.Left, t.MarginTop - 12, c.Right, t.MarginTop - 12);

        var footY = t.PageH - t.MarginBottom + 16;
        g.DrawLine(new XPen(ColorOf(t.Rule), 0.7), c.Left, footY, c.Right, footY);
        g.DrawString(c.RunningTitle, c.CaptionF.Regular, Brush(t.Muted),
            new XRect(c.Left, footY + 6, c.ContentW * 0.7, 14), XStringFormats.TopLeft);
        g.DrawString("第 " + c.ContentPageNo + " 页", c.CaptionF.Regular, Brush(t.Muted),
            new XRect(c.Right - 120, footY + 6, 120, 14), XStringFormats.TopRight);
    }

    // ===================== 内容块 =====================

    private sealed class Block
    {
        public string Type = "";
        public string? Text;
        public string? Title;
        public string? Caption;
        public string? Cite;
        public string? Kind;
        public string? Path;
        public string? ChartType;
        public string? YLabel;
        public string? Language;
        public int Level = 1;
        public bool Ordered;
        public double WidthMm;
        public double HeightMm;
        public List<string> Items = new();
        public List<string> Headers = new();
        public List<List<string>> Rows = new();
        public List<string> Categories = new();
        public List<(string Name, List<double> Values)> Series = new();
    }

    private static void RenderBlock(Ctx c, Block b)
    {
        switch (b.Type)
        {
            case "h1": RenderHeading(c, b, c.H1F, 1); break;
            case "h2": RenderHeading(c, b, c.H2F, 2); break;
            case "h3": RenderHeading(c, b, c.H3F, 3); break;
            case "p": RenderParagraph(c, b); break;
            case "list": RenderList(c, b); break;
            case "callout": RenderCallout(c, b); break;
            case "quote": RenderQuote(c, b); break;
            case "table": RenderTable(c, b); break;
            case "image": RenderImage(c, b); break;
            case "chart": RenderChart(c, b); break;
            case "code": RenderCode(c, b); break;
            case "divider": RenderDivider(c); break;
            case "caption": RenderCaptionBlock(c, b); break;
            case "pagebreak": NewContentPage(c); break;
            case "spacer": c.Y += b.HeightMm > 0 ? (double)XUnit.FromMillimeter(b.HeightMm) : 12; break;
            case "toc": RenderToc(c, b); break;
            default:
                throw new InvalidOperationException(
                    "未知的内容块类型：" + b.Type
                    + "（支持 h1/h2/h3/p/list/callout/quote/table/image/chart/code/divider/caption/pagebreak/spacer/toc）");
        }
    }

    private static void RenderHeading(Ctx c, Block b, FontTriple f, int level)
    {
        var t = c.T;
        var lh = Lh(f.Size);
        var above = level == 1 ? 16 : level == 2 ? 13 : 9;
        var below = level == 1 ? 9 : level == 2 ? 7 : 5;
        c.Y += above;
        Need(c, lh + below + 6);
        var text = (b.Text ?? "").Trim();
        if (level <= 2 && text.Length > 0 && c.Pass.Record is not null && !c.Pass.Record.ContainsKey(text))
            c.Pass.Record[text] = c.ContentPageNo;
        if (level == 1)
        {
            c.Gfx.DrawRectangle(Brush(t.Accent), c.Left, c.Y, 30, 2.6);
            c.Y += 9;
            Need(c, lh + below);
        }
        else if (level == 2)
        {
            c.Gfx.DrawRectangle(Brush(Mix(t.Accent, t.Bg, 0.55)), c.Left, c.Y, 16, 1.8);
            c.Y += 6;
            Need(c, lh + below);
        }
        DrawRich(c, text, f, ColorOf(t.Ink), c.Left, c.ContentW, lh);
        c.Y += below;
    }

    private static void RenderParagraph(Ctx c, Block b)
    {
        var lh = Lh(BodySize);
        Need(c, lh);
        DrawRich(c, b.Text ?? "", c.BodyF, ColorOf(c.T.Ink), c.Left, c.ContentW, lh);
        c.Y += 7;
    }

    private static void RenderList(Ctx c, Block b)
    {
        var t = c.T;
        var lh = Lh(BodySize);
        const double indent = 18;
        for (var i = 0; i < b.Items.Count; i++)
        {
            Need(c, lh);
            var marker = b.Ordered ? (i + 1) + "." : "•";
            c.Gfx.DrawString(marker, c.BodyF.Bold, Brush(t.Accent), c.Left, c.Y, XStringFormats.TopLeft);
            DrawRich(c, b.Items[i], c.BodyF, ColorOf(t.Ink), c.Left + indent, c.ContentW - indent, lh);
            c.Y += 2;
        }
        c.Y += 5;
    }

    private static (string Tone, string Tint) CalloutColors(Tokens t, string? kind)
    {
        var k = (kind ?? "info").Trim().ToLowerInvariant();
        var tone = k switch
        {
            "warn" or "warning" or "caution" => "B5731F",
            "success" or "ok" or "tip" => "1E7A52",
            "danger" or "error" or "risk" => "B03A2E",
            _ => t.Accent,
        };
        var tint = t.Dark ? Mix(tone, t.Bg, 0.16) : Mix(tone, t.Bg, 0.09);
        return (tone, tint);
    }

    private static void RenderCallout(Ctx c, Block b)
    {
        var t = c.T;
        var (tone, tint) = CalloutColors(t, b.Kind);
        var titleText = b.Title;
        if (string.IsNullOrWhiteSpace(titleText))
        {
            var k = (b.Kind ?? "info").Trim().ToLowerInvariant();
            titleText = k switch
            {
                "warn" or "warning" or "caution" => "注意",
                "success" or "ok" or "tip" => "要点",
                "danger" or "error" or "risk" => "风险",
                _ => "说明",
            };
        }

        const double pad = 11, barW = 3.5;
        var textX = c.Left + barW + pad;
        var textW = c.ContentW - barW - pad * 2;
        var tlh = Lh(SmallSize);
        var bodyLines = WrapAtoms(Atomize(c, ParseRuns(b.Text ?? ""), textW, c.SmallF), textW);
        var h = pad * 2 + Lh(10) + 2 + bodyLines.Count * tlh;
        Need(c, Math.Min(h, c.Bottom - c.Top));

        var y0 = c.Y;
        c.Gfx.DrawRectangle(Brush(tint), c.Left, y0, c.ContentW, h);
        c.Gfx.DrawRectangle(Brush(tone), c.Left, y0, barW, h);
        var y = y0 + pad;
        c.Gfx.DrawString(titleText!, c.SmallF.Bold, Brush(tone), textX, y, XStringFormats.TopLeft);
        y += Lh(10) + 2;
        foreach (var ln in bodyLines) { DrawAtoms(c, ln, textX, y, ColorOf(t.Ink)); y += tlh; }
        c.Y = y0 + h + 9;
    }

    private static void RenderQuote(Ctx c, Block b)
    {
        var t = c.T;
        var lh = Lh(11);
        var runs = ParseRuns(b.Text ?? "");
        foreach (var r in runs) if (!r.Code) r.Italic = true;

        var lines = WrapAtoms(Atomize(c, runs, c.ContentW - 28, c.BodyF), c.ContentW - 28);
        var h = lines.Count * lh + 8;
        var citeH = string.IsNullOrWhiteSpace(b.Cite) ? 0 : lh;
        Need(c, h + citeH + 6);
        var y0 = c.Y;
        c.Gfx.DrawRectangle(Brush(t.Accent), c.Left, y0, 2.6, h);
        var y = y0 + 4;
        foreach (var ln in lines) { DrawAtoms(c, ln, c.Left + 18, y, ColorOf(Mix(t.Ink, t.Bg, 0.72))); y += lh; }
        c.Y = y0 + h;
        if (citeH > 0)
        {
            c.Gfx.DrawString("—— " + b.Cite, c.CaptionF.Regular, Brush(t.Muted),
                new XRect(c.Left, c.Y + 2, c.ContentW, 14), XStringFormats.TopRight);
            c.Y += citeH;
        }
        c.Y += 9;
    }

    private static void RenderTable(Ctx c, Block b)
    {
        var t = c.T;
        var g = c.Gfx;
        var ncol = Math.Max(1, Math.Max(b.Headers.Count, b.Rows.Count == 0 ? 1 : b.Rows.Max(r => r.Count)));
        var colW = c.ContentW / ncol;
        const double pad = 5;
        var lh = Lh(SmallSize);
        var headLines = b.Headers
            .Select(h => WrapAtoms(Atomize(c, ParseRuns(h), colW - pad * 2, c.SmallF), colW - pad * 2))
            .ToList();
        var headH = b.Headers.Count == 0 ? 0 : headLines.Max(l => Math.Max(1, l.Count)) * lh + pad * 2;

        Need(c, headH + lh + pad * 2);
        if (b.Headers.Count > 0) DrawTableHeader(c, headLines, colW, pad, lh);

        for (var ri = 0; ri < b.Rows.Count; ri++)
        {
            var row = b.Rows[ri];
            var cells = new List<List<List<Atom>>>(ncol);
            for (var ci = 0; ci < ncol; ci++)
            {
                var raw = ci < row.Count ? row[ci] : "";
                cells.Add(WrapAtoms(Atomize(c, ParseRuns(raw), colW - pad * 2, c.SmallF), colW - pad * 2));
            }
            var rowH = cells.Max(x => Math.Max(1, x.Count)) * lh + pad * 2;
            if (rowH < c.Bottom - c.Top && c.Y + rowH > c.Bottom)
            {
                NewContentPage(c);
                if (b.Headers.Count > 0) DrawTableHeader(c, headLines, colW, pad, lh);
            }
            var y = c.Y;
            if (ri % 2 == 1) g.DrawRectangle(Brush(t.Panel), c.Left, y, c.ContentW, rowH);
            for (var ci = 0; ci < ncol; ci++)
            {
                var x = c.Left + colW * ci + pad;
                var cy = y + pad;
                foreach (var ln in cells[ci]) { DrawAtoms(c, ln, x, cy, ColorOf(t.Ink)); cy += lh; }
            }
            g.DrawLine(new XPen(ColorOf(t.Rule), 0.5), c.Left, y + rowH, c.Right, y + rowH);
            c.Y = y + rowH;
        }
        c.Y += 5;
        if (!string.IsNullOrWhiteSpace(b.Caption))
        {
            c.Gfx.DrawString(b.Caption, c.CaptionF.Regular, Brush(t.Muted),
                new XRect(c.Left, c.Y, c.ContentW, 14), XStringFormats.TopCenter);
            c.Y += 15;
        }
        c.Y += 5;
    }

    private static void DrawTableHeader(Ctx c, List<List<List<Atom>>> headLines, double colW, double pad, double lh)
    {
        var h = headLines.Max(l => Math.Max(1, l.Count)) * lh + pad * 2;
        c.Gfx.DrawRectangle(Brush(c.T.Accent), c.Left, c.Y, c.ContentW, h);
        var ink = ContrastInk(c.T.Accent);
        for (var ci = 0; ci < headLines.Count; ci++)
        {
            var x = c.Left + colW * ci + pad;
            var y = c.Y + pad;
            foreach (var ln in headLines[ci]) { DrawAtoms(c, ln, x, y, ColorOf(ink)); y += lh; }
        }
        c.Y += h;
    }

    private static void RenderImage(Ctx c, Block b)
    {
        var t = c.T;
        var path = b.Path;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("image 块缺少 path。");
        if (!File.Exists(path))
            throw new InvalidOperationException("图片文件不存在：" + path);

        XImage img;
        try
        {
            using var fs = File.OpenRead(path!);
            img = XImage.FromStream(fs);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "图片无法读取（PDFsharp 支持 PNG/JPEG）：" + path + "：" + ex.Message);
        }

        using (img)
        {
            var maxW = b.WidthMm > 0 ? (double)XUnit.FromMillimeter(b.WidthMm) : c.ContentW;
            if (maxW > c.ContentW) maxW = c.ContentW;
            var pw = Math.Max(1, img.PixelWidth);
            var ph = Math.Max(1, img.PixelHeight);
            var w = maxW;
            var h = w * ph / pw;
            var avail = c.Bottom - c.Top - 24;
            if (h > avail)
            {
                h = avail;
                w = h * pw / ph;
            }
            var capH = string.IsNullOrWhiteSpace(b.Caption) ? 0 : 15.0;
            Need(c, h + capH);
            var x = c.Left + (c.ContentW - w) / 2;
            c.Gfx.DrawImage(img, x, c.Y, w, h);
            c.Y += h + 5;
            if (capH > 0)
            {
                c.Gfx.DrawString(b.Caption, c.CaptionF.Regular, Brush(t.Muted),
                    new XRect(c.Left, c.Y, c.ContentW, 14), XStringFormats.TopCenter);
                c.Y += capH;
            }
            c.Y += 8;
        }
    }

    private static void RenderCode(Ctx c, Block b)
    {
        var t = c.T;
        var code = (b.Text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
        var rawLines = code.Split('\n');
        var innerW = c.ContentW - 20;
        var lh = Lh(MonoSize);
        var lines = new List<List<Atom>>();
        foreach (var rl in rawLines)
        {
            var ln = WrapAtoms(Atomize(c, ParseRuns(rl.Length == 0 ? " " : rl), innerW, c.MonoF), innerW);
            if (ln.Count == 0) ln.Add(new List<Atom>());
            lines.AddRange(ln);
        }
        if (lines.Count == 0) lines.Add(new List<Atom>());

        var bg = t.Dark ? Mix(t.Ink, t.Bg, 0.90) : Mix(t.Ink, t.Bg, 0.96);
        var first = true;
        var i = 0;
        while (i < lines.Count)
        {
            var labelH = first && !string.IsNullOrWhiteSpace(b.Language) ? 14.0 : 0;
            var avail = c.Bottom - c.Y;
            var fit = (int)Math.Floor((avail - labelH - 16) / lh);
            if (fit < 1) fit = 1;
            var take = Math.Min(fit, lines.Count - i);
            var h = 16 + take * lh + labelH;
            if (h > c.Bottom - c.Top) h = c.Bottom - c.Top;
            c.Gfx.DrawRectangle(Brush(bg), c.Left, c.Y, c.ContentW, h);
            if (labelH > 0)
                c.Gfx.DrawString(b.Language, c.CaptionF.Regular, Brush(t.Muted),
                    new XRect(c.Left + 8, c.Y + 5, c.ContentW - 16, 12), XStringFormats.TopRight);
            var y = c.Y + 8 + labelH;
            for (var k = 0; k < take; k++)
            {
                DrawAtoms(c, lines[i + k], c.Left + 10, y, ColorOf(t.Dark ? Mix(t.Ink, t.Bg, 0.85) : t.Ink));
                y += lh;
            }
            i += take;
            if (i < lines.Count) NewContentPage(c);
            else c.Y += h + 9;
        }
    }

    private static void RenderDivider(Ctx c)
    {
        Need(c, 14);
        c.Y += 4;
        c.Gfx.DrawLine(new XPen(ColorOf(c.T.Rule), 0.9), c.Left, c.Y, c.Right, c.Y);
        c.Y += 12;
    }

    private static void RenderCaptionBlock(Ctx c, Block b)
    {
        Need(c, 14);
        c.Gfx.DrawString(b.Text ?? "", c.CaptionF.Regular, Brush(c.T.Muted),
            c.Left, c.Y, XStringFormats.TopLeft);
        c.Y += 15;
    }

    private static void RenderToc(Ctx c, Block b)
    {
        var t = c.T;
        var items = b.Items.Count > 0 ? b.Items : c.Pass.TocItems;
        var title = string.IsNullOrWhiteSpace(b.Title) ? "目录" : b.Title!;
        c.Y += 4;
        Need(c, Lh(H2Size) + 26);
        DrawRich(c, title, c.H2F, ColorOf(t.Ink), c.Left, c.ContentW, Lh(H2Size));
        c.Gfx.DrawRectangle(Brush(t.Accent), c.Left, c.Y + 2, 34, 2.4);
        c.Y += 16;

        var lh = Lh(11);
        for (var i = 0; i < items.Count; i++)
        {
            Need(c, lh);
            var y = c.Y;
            var label = items[i];
            c.Gfx.DrawString((i + 1).ToString("00", CultureInfo.InvariantCulture), c.CaptionF.Bold,
                Brush(t.Accent), c.Left, y + 2, XStringFormats.TopLeft);
            var tx = c.Left + 26;
            var maxTextW = c.ContentW - 26 - 42;
            label = Truncate(c, label, c.SmallF.Regular, maxTextW);
            c.Gfx.DrawString(label, c.SmallF.Regular, Brush(t.Ink), tx, y, XStringFormats.TopLeft);
            if (c.Pass.TocPages.TryGetValue(items[i].Trim(), out var pg) && pg > 0)
            {
                var textW = c.Gfx.MeasureString(label, c.SmallF.Regular).Width;
                var pgText = pg.ToString(CultureInfo.InvariantCulture);
                var pgW = c.Gfx.MeasureString(pgText, c.SmallF.Bold).Width;
                var px = c.Right - pgW;
                c.Gfx.DrawString(pgText, c.SmallF.Bold, Brush(t.Ink), px, y, XStringFormats.TopLeft);
                var lx1 = tx + textW + 6;
                var lx2 = px - 6;
                if (lx2 > lx1)
                {
                    var pen = new XPen(ColorOf(t.Rule), 0.6) { DashStyle = XDashStyle.Dot };
                    c.Gfx.DrawLine(pen, lx1, y + lh * 0.62, lx2, y + lh * 0.62);
                }
            }
            c.Y = y + lh;
        }
        c.Y += 10;
    }

    // ===================== 图表（矢量绘制） =====================

    private static readonly string[] ChartPalette =
    {
        "1F3864", "2E8B8B", "C8A24A", "B4533C", "5B7F4E", "7A5EA8", "4C9A8A", "8D99AE",
    };

    private static XColor SeriesColor(Tokens t, int i)
    {
        if (i == 0) return ColorOf(t.Accent);
        return ColorOf(Mix(ChartPalette[i % ChartPalette.Length], t.Accent, 0.72));
    }

    private static void RenderChart(Ctx c, Block b)
    {
        var t = c.T;
        var chartH = b.HeightMm > 0 ? (double)XUnit.FromMillimeter(b.HeightMm) : 175.0;
        var maxH = c.Bottom - c.Top - 70;
        if (chartH > maxH) chartH = maxH;
        if (chartH < 80) chartH = 80;

        var titleH = string.IsNullOrWhiteSpace(b.Title) ? 0 : Lh(ChartTitleSize) + 5;
        var capH = string.IsNullOrWhiteSpace(b.Caption) ? 0 : 16.0;
        Need(c, titleH + chartH + capH + 12);
        if (titleH > 0)
        {
            DrawRich(c, b.Title!, c.ChartTitleF, ColorOf(t.Ink), c.Left, c.ContentW, Lh(ChartTitleSize));
            c.Y += 5;
        }
        var boxFill = t.Dark ? Mix(t.Ink, t.Bg, 0.92) : Mix(t.Ink, t.Bg, 0.97);
        var box = new XRect(c.Left, c.Y, c.ContentW, chartH);
        c.Gfx.DrawRectangle(Brush(boxFill), box);

        var type = (b.ChartType ?? "bar").Trim().ToLowerInvariant();
        switch (type)
        {
            case "line": DrawLineChart(c, b, box); break;
            case "pie": DrawPieChart(c, b, box, false, boxFill); break;
            case "doughnut": case "donut": case "ring": DrawPieChart(c, b, box, true, boxFill); break;
            default: DrawBarChart(c, b, box); break;
        }

        c.Y += chartH + 5;
        if (capH > 0)
        {
            c.Gfx.DrawString(b.Caption, c.CaptionF.Regular, Brush(t.Muted),
                new XRect(c.Left, c.Y, c.ContentW, 14), XStringFormats.TopCenter);
            c.Y += capH;
        }
        c.Y += 9;
    }

    private static void DrawAxes(Ctx c, double x, double y, double w, double h, double maxV, string? yLabel)
    {
        var t = c.T;
        var g = c.Gfx;
        var gridPen = new XPen(ColorOf(t.Rule), 0.6);
        for (var i = 0; i <= 4; i++)
        {
            var gy = y + h - h * i / 4.0;
            g.DrawLine(gridPen, x, gy, x + w, gy);
            g.DrawString(Fmt(maxV * i / 4.0), c.CaptionF.Regular, Brush(t.Muted),
                new XRect(x - 44, gy - 6, 40, 12), XStringFormats.TopRight);
        }
        g.DrawLine(new XPen(ColorOf(t.Muted), 0.9), x, y, x, y + h);
        g.DrawLine(new XPen(ColorOf(t.Muted), 0.9), x, y + h, x + w, y + h);
        if (!string.IsNullOrWhiteSpace(yLabel))
            g.DrawString("单位：" + yLabel, c.CaptionF.Regular, Brush(t.Muted), x, y - 15, XStringFormats.TopLeft);
    }

    private static void DrawLegend(Ctx c, List<(string Name, List<double> Values)> series, double x, double y)
    {
        if (series.Count <= 1) return;
        var g = c.Gfx;
        var lx = x;
        for (var i = 0; i < series.Count; i++)
        {
            var name = string.IsNullOrWhiteSpace(series[i].Name) ? "系列" + (i + 1) : series[i].Name;
            g.DrawRectangle(new XSolidBrush(SeriesColor(c.T, i)), lx, y + 2, 8, 8);
            g.DrawString(name, c.CaptionF.Regular, Brush(c.T.Muted), lx + 12, y, XStringFormats.TopLeft);
            lx += 12 + g.MeasureString(name, c.CaptionF.Regular).Width + 16;
            if (lx > x + c.ContentW - 60) break;
        }
    }

    private static void DrawBarChart(Ctx c, Block b, XRect box)
    {
        var t = c.T;
        var g = c.Gfx;
        var cats = b.Categories;
        var series = b.Series;
        if (cats.Count == 0)
        {
            var maxLen = series.Count == 0 ? 0 : series.Max(s => s.Values.Count);
            for (var i = 0; i < maxLen; i++) cats.Add((i + 1).ToString(CultureInfo.InvariantCulture));
        }
        if (cats.Count == 0 || series.Count == 0)
        {
            g.DrawString("（无数据）", c.SmallF.Regular, Brush(t.Muted), box.X + 14, box.Y + box.Height / 2, XStringFormats.TopLeft);
            return;
        }
        var maxV = 0.0;
        foreach (var s in series) foreach (var v in s.Values) maxV = Math.Max(maxV, v);
        var niceMax = NiceCeil(maxV);

        var padL = 48.0;
        var padR = 14.0;
        var padT = series.Count > 1 ? 26.0 : 14.0;
        var padB = 28.0;
        var plotX = box.X + padL;
        var plotY = box.Y + padT;
        var plotW = box.Width - padL - padR;
        var plotH = box.Height - padT - padB;
        if (plotW < 30 || plotH < 30) return;
        DrawAxes(c, plotX, plotY, plotW, plotH, niceMax, b.YLabel);
        DrawLegend(c, series, plotX, plotY - 16);

        var n = cats.Count;
        var groupW = plotW / n;
        var ns = series.Count;
        var barW = groupW * 0.66 / ns;
        for (var i = 0; i < n; i++)
        {
            var gx = plotX + groupW * i + groupW * 0.17;
            for (var si = 0; si < ns; si++)
            {
                var s = series[si];
                var v = i < s.Values.Count ? Math.Max(0, s.Values[i]) : 0;
                var bh = plotH * (v / niceMax);
                var x = gx + barW * si;
                g.DrawRectangle(new XSolidBrush(SeriesColor(t, si)), x, plotY + plotH - bh, barW, bh);
                if (v > 0 && n * ns <= 24)
                    g.DrawString(Fmt(v), c.CaptionF.Regular, Brush(t.Muted),
                        new XRect(x - 8, plotY + plotH - bh - 12, barW + 16, 12), XStringFormats.TopCenter);
            }
            g.DrawString(Truncate(c, cats[i], c.CaptionF.Regular, groupW), c.CaptionF.Regular, Brush(t.Muted),
                new XRect(plotX + groupW * i, plotY + plotH + 6, groupW, 12), XStringFormats.TopCenter);
        }
    }

    private static void DrawLineChart(Ctx c, Block b, XRect box)
    {
        var t = c.T;
        var g = c.Gfx;
        var cats = b.Categories;
        var series = b.Series;
        var maxLen = series.Count == 0 ? 0 : series.Max(s => s.Values.Count);
        if (cats.Count == 0) for (var i = 0; i < maxLen; i++) cats.Add((i + 1).ToString(CultureInfo.InvariantCulture));
        if (cats.Count == 0 || series.Count == 0)
        {
            g.DrawString("（无数据）", c.SmallF.Regular, Brush(t.Muted), box.X + 14, box.Y + box.Height / 2, XStringFormats.TopLeft);
            return;
        }
        var maxV = 0.0;
        foreach (var s in series) foreach (var v in s.Values) maxV = Math.Max(maxV, v);
        var niceMax = NiceCeil(maxV);

        var padL = 48.0;
        var padR = 14.0;
        var padT = series.Count > 1 ? 26.0 : 14.0;
        var padB = 28.0;
        var plotX = box.X + padL;
        var plotY = box.Y + padT;
        var plotW = box.Width - padL - padR;
        var plotH = box.Height - padT - padB;
        if (plotW < 30 || plotH < 30) return;
        DrawAxes(c, plotX, plotY, plotW, plotH, niceMax, b.YLabel);
        DrawLegend(c, series, plotX, plotY - 16);

        var n = cats.Count;
        var step = n > 1 ? plotW / (n - 1) : 0;
        for (var si = 0; si < series.Count; si++)
        {
            var s = series[si];
            var pen = new XPen(SeriesColor(t, si), 1.6);
            var brush = new XSolidBrush(SeriesColor(t, si));
            XPoint? prev = null;
            for (var i = 0; i < n && i < s.Values.Count; i++)
            {
                var v = Math.Max(0, s.Values[i]);
                var px = plotX + step * i;
                var py = plotY + plotH - plotH * (v / niceMax);
                var p = new XPoint(px, py);
                if (prev.HasValue) g.DrawLine(pen, prev.Value, p);
                g.DrawEllipse(brush, px - 2.6, py - 2.6, 5.2, 5.2);
                prev = p;
            }
        }
        for (var i = 0; i < n; i++)
        {
            var cw = n > 1 ? step : plotW;
            g.DrawString(Truncate(c, cats[i], c.CaptionF.Regular, Math.Max(30, cw)), c.CaptionF.Regular, Brush(t.Muted),
                new XRect(plotX + step * i - cw / 2, plotY + plotH + 6, cw, 12), XStringFormats.TopCenter);
        }
    }

    private static void DrawPieChart(Ctx c, Block b, XRect box, bool doughnut, string boxFill)
    {
        var t = c.T;
        var g = c.Gfx;
        var values = b.Series.Count > 0 ? b.Series[0].Values : new List<double>();
        var names = new List<string>();
        for (var i = 0; i < values.Count; i++)
            names.Add(i < b.Categories.Count ? b.Categories[i] : (i + 1).ToString(CultureInfo.InvariantCulture));

        var positive = values.Select(v => Math.Max(0, v)).ToList();
        var total = positive.Sum();
        if (total <= 0)
        {
            g.DrawString("（无数据）", c.SmallF.Regular, Brush(t.Muted), box.X + 14, box.Y + box.Height / 2, XStringFormats.TopLeft);
            return;
        }

        var size = Math.Min(box.Height - 28, box.Width * 0.46);
        if (size < 40) return;
        var cx = box.X + 18 + size / 2;
        var cy = box.Y + box.Height / 2;
        var r = size / 2;
        double start = -90;
        for (var i = 0; i < positive.Count; i++)
        {
            if (positive[i] <= 0) continue;
            var sweep = 360.0 * positive[i] / total;
            var pts = new List<XPoint> { new(cx, cy) };
            var steps = Math.Max(10, (int)(Math.Abs(sweep) / 3) + 2);
            for (var k = 0; k <= steps; k++)
            {
                var a = (start + sweep * k / steps) * Math.PI / 180.0;
                pts.Add(new XPoint(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
            }
            g.DrawPolygon(new XSolidBrush(SeriesColor(t, i)), pts.ToArray(), XFillMode.Winding);
            start += sweep;
        }
        if (doughnut)
            g.DrawEllipse(new XSolidBrush(ColorOf(boxFill)), cx - r * 0.52, cy - r * 0.52, r * 1.04, r * 1.04);

        var lx = box.X + size + 40;
        var ly = box.Y + Math.Max(16, box.Height / 2 - positive.Count * 9);
        for (var i = 0; i < positive.Count; i++)
        {
            if (positive[i] <= 0) continue;
            var pct = positive[i] / total * 100.0;
            g.DrawRectangle(new XSolidBrush(SeriesColor(t, i)), lx, ly + 2, 9, 9);
            var label = names[i] + "  " + Fmt(positive[i]) + "（" + pct.ToString("0.#", CultureInfo.InvariantCulture) + "%）";
            g.DrawString(label, c.CaptionF.Regular, Brush(t.Ink), lx + 14, ly, XStringFormats.TopLeft);
            ly += 18;
            if (ly > box.Y + box.Height - 10) break;
        }
    }

    private static double NiceCeil(double v)
    {
        if (v <= 0) return 1;
        var exp = Math.Floor(Math.Log10(v));
        var pow = Math.Pow(10, exp);
        var f = v / pow;
        double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return nice * pow;
    }

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    // ===================== 封面（按文档类型分流） =====================

    private static void DrawCover(Ctx c, string title, string? subtitle, string? author, string? date)
    {
        var t = c.T;
        var g = c.Gfx;
        var W = t.PageW;
        var H = t.PageH;
        var M = t.MarginX;
        var ink = ColorOf(t.CoverInk);
        var meta = Meta(author, date);

        switch (t.DocType)
        {
            case "proposal":
            {
                var splitX = W * 0.42;
                g.DrawRectangle(Brush(t.CoverBg), 0, 0, W, H);
                g.DrawRectangle(Brush(t.Accent), 0, 0, splitX, H);
                g.DrawString("方案 PROPOSAL", c.KickerF.Bold, Brush(Mix(t.Accent, "FFFFFF", 0.45)), M, H * 0.20, XStringFormats.TopLeft);
                DrawRichAt(c, title, c.CoverTitleF, ColorOf("FFFFFF"), M, H * 0.25, splitX - M * 2, Lh(CoverTitleSize) * 1.05);
                g.DrawLine(new XPen(ColorOf(t.Accent), 2.2), splitX + M, H * 0.30, W - M, H * 0.30);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    DrawRichAt(c, subtitle!, c.CoverSubF, ink, splitX + M, H * 0.35, W - M * 2 - splitX + M, Lh(13) * 1.2);
                if (meta.Length > 0)
                    g.DrawString(meta, c.KickerF.Regular, Brush(t.Muted), splitX + M, H - M - 24, XStringFormats.TopLeft);
                break;
            }
            case "resume":
            {
                g.DrawRectangle(Brush(t.CoverBg), 0, 0, W, H);
                g.DrawRectangle(Brush(t.Accent), 0, 0, 9, H);
                var head = title.Length > 0 ? title.Substring(0, 1) : "A";
                var rest = title.Length > 1 ? title.Substring(1) : "";
                var initialFont = MakeFont(t.Serif ? c.Fonts.SerifFamily : c.Fonts.PrimaryFamily, 104, XFontStyleEx.Bold);
                g.DrawString(head, initialFont, Brush(t.Accent), M, H * 0.16, XStringFormats.TopLeft);
                g.DrawString("简历 RESUME", c.KickerF.Bold, Brush(t.Muted), M + 6, H * 0.18, XStringFormats.TopLeft);
                var titleY = H * 0.16 + 116;
                if (rest.Length > 0)
                    DrawRichAt(c, rest, c.CoverTitleF, ink, M, titleY, W - M * 2, Lh(c.CoverTitleF.Size));
                g.DrawLine(new XPen(ColorOf(t.Rule), 0.9), M, titleY + 44, W - M, titleY + 44);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    DrawRichAt(c, subtitle!, c.CoverSubF, ColorOf(t.Muted), M, titleY + 58, W - M * 2, Lh(13));
                if (meta.Length > 0)
                    g.DrawString(meta, c.KickerF.Regular, Brush(t.CoverInk), M, H - M - 20, XStringFormats.TopLeft);
                break;
            }
            case "academic":
            {
                g.DrawRectangle(Brush(t.CoverBg), 0, 0, W, H);
                var y = H * 0.20;
                g.DrawLine(new XPen(ColorOf(t.Accent), 1.4), M, y, W - M, y);
                g.DrawLine(new XPen(ColorOf(t.Accent), 0.6), M, y + 4, W - M, y + 4);
                g.DrawString("ACADEMIC PAPER", c.KickerF.Bold, Brush(t.Muted), new XRect(M, y + 16, W - M * 2, 14), XStringFormats.TopCenter);
                var end = DrawRichAt(c, title, c.CoverTitleF, ink, M, H * 0.30, W - M * 2, Lh(CoverTitleSize) * 1.05, true);
                g.DrawLine(new XPen(ColorOf(t.Rule), 0.8), W / 2 - 70, end + 10, W / 2 + 70, end + 10);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    DrawRichAt(c, subtitle!, c.CoverSubF, ColorOf(t.Muted), M, end + 24, W - M * 2, Lh(13), true);
                if (meta.Length > 0)
                    g.DrawString(meta, c.KickerF.Regular, Brush(t.Muted), new XRect(M, H - M - 30, W - M * 2, 14), XStringFormats.TopCenter);
                g.DrawLine(new XPen(ColorOf(t.Rule), 0.8), M, H - M - 10, W - M, H - M - 10);
                break;
            }
            case "minimal":
            {
                g.DrawRectangle(Brush(t.CoverBg), 0, 0, W, H);
                g.DrawRectangle(Brush(t.Accent), 0, 0, W, 5);
                g.DrawString("DOCUMENT", c.KickerF.Bold, Brush(t.Muted), M, H * 0.22, XStringFormats.TopLeft);
                var end = DrawRichAt(c, title, c.CoverTitleF, ink, M, H * 0.26, W - M * 2, Lh(CoverTitleSize) * 1.08);
                g.DrawRectangle(Brush(t.Accent), M, end + 12, 44, 2.6);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    DrawRichAt(c, subtitle!, c.CoverSubF, ColorOf(t.Muted), M, end + 28, W - M * 2, Lh(13));
                if (meta.Length > 0)
                    g.DrawString(meta, c.KickerF.Regular, Brush(t.Muted), M, H - M - 20, XStringFormats.TopLeft);
                break;
            }
            case "editorial":
            {
                g.DrawRectangle(Brush(t.CoverBg), 0, 0, W, H);
                var ghost = title.ToUpperInvariant();
                var ghostFont = MakeFont(t.Serif ? c.Fonts.SerifFamily : c.Fonts.PrimaryFamily, 104, XFontStyleEx.Bold);
                g.DrawString(Truncate(c, ghost, ghostFont, W * 0.98), ghostFont, new XSolidBrush(ColorOf(Mix(t.Ink, t.Bg, 0.90))), M - 6, H * 0.24, XStringFormats.TopLeft);
                g.DrawString("专栏 · EDITORIAL", c.KickerF.Bold, Brush(t.Accent), M, H * 0.20, XStringFormats.TopLeft);
                var end = DrawRichAt(c, title, c.CoverTitleF, ink, M, H * 0.44, W - M * 2, Lh(CoverTitleSize) * 1.05);
                g.DrawRectangle(Brush(t.Accent), M, end + 12, 96, 6);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    DrawRichAt(c, subtitle!, c.CoverSubF, ColorOf(t.Muted), M, end + 32, W - M * 2, Lh(13));
                if (meta.Length > 0)
                    g.DrawString(meta, c.KickerF.Regular, Brush(t.Muted), M, H - M - 20, XStringFormats.TopLeft);
                break;
            }
            case "magazine":
            {
                g.DrawRectangle(Brush(t.CoverBg), 0, 0, W, H);
                g.DrawEllipse(Brush(t.Accent), W / 2 - 26, H * 0.13, 52, 52);
                g.DrawString("MAGAZINE", c.KickerF.Bold, Brush(t.Muted), new XRect(M, H * 0.24, W - M * 2, 14), XStringFormats.TopCenter);
                var end = DrawRichAt(c, title, c.CoverTitleF, ink, M, H * 0.29, W - M * 2, Lh(CoverTitleSize) * 1.08, true);
                g.DrawLine(new XPen(ColorOf(t.Accent), 1.6), W / 2 - 40, end + 12, W / 2 + 40, end + 12);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    DrawRichAt(c, subtitle!, c.CoverSubF, ColorOf(t.Muted), M, end + 28, W - M * 2, Lh(13), true);
                if (meta.Length > 0)
                    g.DrawString(meta, c.KickerF.Regular, Brush(t.Muted), new XRect(M, H - M - 30, W - M * 2, 14), XStringFormats.TopCenter);
                break;
            }
            case "terminal":
            {
                g.DrawRectangle(Brush(t.CoverBg), 0, 0, W, H);
                var gridPen = new XPen(ColorOf(Mix(t.Accent, t.Bg, 0.80)), 0.4);
                for (var x = M; x < W; x += 26) g.DrawLine(gridPen, x, 0, x, H);
                for (var y = 0.0; y < H; y += 26) g.DrawLine(gridPen, 0, y, W, y);
                var boxPen = new XPen(ColorOf(t.Accent), 1.1);
                g.DrawRectangle(boxPen, M * 0.55, M * 0.55, W - M * 1.1, H - M * 1.1);
                g.DrawString("> agui pdf_doc --type terminal", c.KickerF.Bold, Brush(t.Accent), M, M + 6, XStringFormats.TopLeft);
                DrawRichAt(c, "> " + title, c.CoverTitleF, ColorOf("EAF6EE"), M, H * 0.30, W - M * 2, Lh(CoverTitleSize) * 1.05);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    DrawRichAt(c, "# " + subtitle!, c.CoverSubF, ColorOf(Mix(t.Ink, t.Bg, 0.65)), M, H * 0.52, W - M * 2, Lh(13));
                if (meta.Length > 0)
                    g.DrawString("$ echo \"" + meta + "\"", c.KickerF.Regular, Brush(Mix(t.Ink, t.Bg, 0.72)), M, H - M - 24, XStringFormats.TopLeft);
                break;
            }
            default: // report：深色满版封面
            {
                g.DrawRectangle(Brush(t.CoverBg), 0, 0, W, H);
                g.DrawRectangle(Brush(t.Accent), 0, H * 0.66, W, 5);
                g.DrawString("报 告 · REPORT", c.KickerF.Bold, Brush(Mix(t.CoverBg, "FFFFFF", 0.35)), M, H * 0.28, XStringFormats.TopLeft);
                var end = DrawRichAt(c, title, c.CoverTitleF, ink, M, H * 0.33, W - M * 2, Lh(CoverTitleSize) * 1.06);
                if (!string.IsNullOrWhiteSpace(subtitle))
                    DrawRichAt(c, subtitle!, c.CoverSubF, ColorOf(Mix(t.CoverInk, t.CoverBg, 0.72)), M, end + 16, W - M * 2, Lh(13));
                if (meta.Length > 0)
                    g.DrawString(meta, c.KickerF.Regular, Brush(Mix(t.CoverInk, t.CoverBg, 0.62)), M, H - M - 24, XStringFormats.TopLeft);
                g.DrawString("AG-UI", c.KickerF.Bold, Brush(Mix(t.CoverBg, "FFFFFF", 0.35)),
                    new XRect(M, H - M - 24, W - M * 2, 14), XStringFormats.TopRight);
                break;
            }
        }
    }

    private static string Meta(string? author, string? date)
    {
        if (string.IsNullOrWhiteSpace(author)) return (date ?? "").Trim();
        if (string.IsNullOrWhiteSpace(date)) return author!.Trim();
        return author!.Trim() + " · " + date!.Trim();
    }

    // ===================== 富文本：内联标记 / 原子化 / 折行 =====================

    private sealed class RichRun
    {
        public string Text = "";
        public bool Bold;
        public bool Italic;
        public bool Code;
    }

    private sealed class Atom
    {
        public string Text = "";
        public XFont Font = null!;
        public double W;
        public bool Space;
        public bool Break;
    }

    /// <summary>解析 `**粗体**` / `*斜体*` / `` `等宽` ``。</summary>
    private static List<RichRun> ParseRuns(string s)
    {
        var runs = new List<RichRun>();
        var sb = new StringBuilder();
        var bold = false;
        var italic = false;
        var code = false;
        void Flush()
        {
            if (sb.Length == 0) return;
            runs.Add(new RichRun { Text = sb.ToString(), Bold = bold, Italic = italic, Code = code });
            sb.Length = 0;
        }
        var text = s ?? "";
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (!code && ch == '*' && i + 1 < text.Length && text[i + 1] == '*') { Flush(); bold = !bold; i++; continue; }
            if (!code && ch == '*') { Flush(); italic = !italic; continue; }
            if (ch == '`') { Flush(); code = !code; continue; }
            sb.Append(ch);
        }
        Flush();
        if (runs.Count == 0) runs.Add(new RichRun());
        return runs;
    }

    private static XFont FontOf(FontTriple f, RichRun r)
    {
        if (r.Code) return f.Mono;
        if (r.Bold && r.Italic) return f.BoldItalic;
        if (r.Bold) return f.Bold;
        if (r.Italic) return f.Italic;
        return f.Regular;
    }

    private static bool IsCjk(char ch)
        => ch >= 0x2E80 || (ch >= 0xFF00 && ch <= 0xFFEF) || ch == '\u3000';

    private static List<Atom> Atomize(Ctx c, List<RichRun> runs, double maxW, FontTriple triple)
    {
        var atoms = new List<Atom>();
        foreach (var r in runs)
        {
            var f = FontOf(triple, r);
            var s = r.Text ?? "";
            var i = 0;
            while (i < s.Length)
            {
                var ch = s[i];
                if (ch == '\n')
                {
                    atoms.Add(new Atom { Text = "", Font = f, W = 0, Break = true });
                    i++;
                    continue;
                }
                if (ch == ' ' || ch == '\t')
                {
                    atoms.Add(new Atom { Text = " ", Font = f, W = Measure(c, f, " "), Space = true });
                    i++;
                    continue;
                }
                if (IsCjk(ch))
                {
                    var one = ch.ToString();
                    atoms.Add(new Atom { Text = one, Font = f, W = Measure(c, f, one) });
                    i++;
                    continue;
                }
                var j = i;
                while (j < s.Length && s[j] != ' ' && s[j] != '\t' && s[j] != '\n' && !IsCjk(s[j])) j++;
                var word = s.Substring(i, j - i);
                var w = Measure(c, f, word);
                if (w > maxW && word.Length > 1)
                {
                    foreach (var ch2 in word)
                    {
                        var one = ch2.ToString();
                        atoms.Add(new Atom { Text = one, Font = f, W = Measure(c, f, one) });
                    }
                }
                else
                {
                    atoms.Add(new Atom { Text = word, Font = f, W = w });
                }
                i = j;
            }
        }
        return atoms;
    }

    private static double Measure(Ctx c, XFont f, string s) => c.Gfx.MeasureString(s, f).Width;

    private static List<List<Atom>> WrapAtoms(List<Atom> atoms, double maxW)
    {
        var lines = new List<List<Atom>>();
        var cur = new List<Atom>();
        double curW = 0;
        foreach (var a in atoms)
        {
            if (a.Break)
            {
                TrimTrailing(cur);
                lines.Add(cur);
                cur = new List<Atom>();
                curW = 0;
                continue;
            }
            if (a.Space)
            {
                if (cur.Count == 0) continue;
                cur.Add(a);
                curW += a.W;
                continue;
            }
            if (cur.Count > 0 && curW + a.W > maxW + 0.01)
            {
                TrimTrailing(cur);
                lines.Add(cur);
                cur = new List<Atom>();
                curW = 0;
            }
            cur.Add(a);
            curW += a.W;
        }
        TrimTrailing(cur);
        lines.Add(cur);
        return lines;
    }

    private static void TrimTrailing(List<Atom> line)
    {
        while (line.Count > 0 && line[line.Count - 1].Space) line.RemoveAt(line.Count - 1);
    }

    private static void DrawAtoms(Ctx c, List<Atom> line, double x, double y, XColor color)
    {
        var brush = new XSolidBrush(color);
        var cx = x;
        foreach (var a in line)
        {
            if (!a.Space)
                c.Gfx.DrawString(a.Text, a.Font, brush, cx, y, XStringFormats.TopLeft);
            cx += a.W;
        }
    }

    /// <summary>流式绘制（自动分页）。</summary>
    private static void DrawRich(Ctx c, string text, FontTriple f, XColor color, double x, double maxW, double lineH)
    {
        var lines = WrapAtoms(Atomize(c, ParseRuns(text), maxW, f), maxW);
        foreach (var ln in lines)
        {
            Need(c, lineH);
            DrawAtoms(c, ln, x, c.Y, color);
            c.Y += lineH;
        }
    }

    /// <summary>固定位置绘制（封面用；不分页），返回底部 y。</summary>
    private static double DrawRichAt(Ctx c, string text, FontTriple f, XColor color, double x, double y, double maxW, double lineH, bool centered = false)
    {
        var lines = WrapAtoms(Atomize(c, ParseRuns(text), maxW, f), maxW);
        var cy = y;
        foreach (var ln in lines)
        {
            if (centered)
            {
                var lw = ln.Sum(a => a.W);
                DrawAtoms(c, ln, c.Left + (c.ContentW - lw) / 2, cy, color);
            }
            else
            {
                DrawAtoms(c, ln, x, cy, color);
            }
            cy += lineH;
        }
        return cy;
    }

    private static string Truncate(Ctx c, string s, XFont f, double maxW)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (maxW <= 4) return "";
        if (Measure(c, f, s) <= maxW) return s;
        var t = s;
        while (t.Length > 1 && Measure(c, f, t + "…") > maxW) t = t.Substring(0, t.Length - 1);
        return t + "…";
    }

    // ===================== 输入解析 =====================

    private static List<Block> ParseBlocks(JsonElement root, List<string> warnings)
    {
        var blocks = new List<Block>();
        if (root.TryGetProperty("blocks", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                blocks.Add(ParseBlock(el));
            }
        }
        var md = Str(root, "markdown") ?? Str(root, "md") ?? Str(root, "content") ?? Str(root, "text");
        if (!string.IsNullOrWhiteSpace(md))
        {
            if (blocks.Count > 0) warnings.Add("同时提供了 blocks 与 markdown：以 blocks 为准，markdown 被忽略。");
            else blocks = ParseMarkdown(md!);
        }
        return blocks;
    }

    private static Block ParseBlock(JsonElement el)
    {
        var rawType = (Str(el, "type") ?? Str(el, "blockType") ?? "").Trim().ToLowerInvariant();
        var b = new Block();
        var level = Num(el, "level");
        b.Level = level.HasValue ? (int)level.Value : (rawType == "h2" ? 2 : rawType == "h3" ? 3 : 1);
        b.Type = NormalizeBlockType(rawType, b.Level);
        b.Text = Str(el, "text") ?? Str(el, "content") ?? Str(el, "body") ?? Str(el, "code");
        b.Title = Str(el, "title") ?? Str(el, "heading") ?? Str(el, "label");
        b.Caption = Str(el, "caption");
        b.Cite = Str(el, "cite") ?? Str(el, "source");
        b.Kind = Str(el, "kind") ?? Str(el, "variant") ?? Str(el, "tone") ?? Str(el, "level");
        b.Path = Str(el, "path") ?? Str(el, "src") ?? Str(el, "image") ?? Str(el, "url");
        b.ChartType = Str(el, "chartType") ?? Str(el, "chart");
        b.YLabel = Str(el, "yLabel") ?? Str(el, "unit");
        b.Language = Str(el, "language") ?? Str(el, "lang");
        var wmm = Num(el, "widthMm");
        if (!wmm.HasValue) wmm = Num(el, "width");
        b.WidthMm = wmm ?? 0;
        b.HeightMm = Num(el, "heightMm") ?? 0;
        b.Ordered = Bool(el, "ordered") ?? false;

        if (el.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
                b.Items.Add(it.ValueKind == JsonValueKind.String ? it.GetString() ?? "" : CellText(it));

        if (el.TryGetProperty("headers", out var hs) && hs.ValueKind == JsonValueKind.Array)
            foreach (var h in hs.EnumerateArray()) b.Headers.Add(CellText(h));

        if (el.TryGetProperty("rows", out var rs) && rs.ValueKind == JsonValueKind.Array)
            foreach (var r in rs.EnumerateArray())
            {
                var row = new List<string>();
                if (r.ValueKind == JsonValueKind.Array)
                    foreach (var cell in r.EnumerateArray()) row.Add(CellText(cell));
                b.Rows.Add(row);
            }

        if (el.TryGetProperty("categories", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var cc in cs.EnumerateArray()) b.Categories.Add(CellText(cc));

        if (el.TryGetProperty("series", out var ss) && ss.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in ss.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var vals = new List<double>();
                if (s.TryGetProperty("values", out var vv) && vv.ValueKind == JsonValueKind.Array)
                    foreach (var v in vv.EnumerateArray()) vals.Add(ToNum(v));
                b.Series.Add((Str(s, "name") ?? "", vals));
            }
        }
        else if (el.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array)
        {
            var vals = new List<double>();
            foreach (var v in vs.EnumerateArray()) vals.Add(ToNum(v));
            b.Series.Add((Str(el, "name") ?? "", vals));
        }

        // table 的简写：{"type":"table","data":[["a","b"], …]}（第一行当表头）
        if (b.Type == "table" && b.Rows.Count > 0 && b.Headers.Count == 0)
        {
            b.Headers.AddRange(b.Rows[0]);
            b.Rows.RemoveAt(0);
        }
        return b;
    }

    private static string NormalizeBlockType(string raw, int level)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        switch (s)
        {
            case "h1": case "heading": case "title": case "h": case "section": return level <= 1 ? "h1" : level == 2 ? "h2" : "h3";
            case "h2": return "h2";
            case "h3": return "h3";
            case "p": case "paragraph": case "body": case "text": case "content": case "para": return "p";
            case "list": case "ul": case "ol": case "bullet": case "bullets": case "items": case "numbered": return "list";
            case "callout": case "note": case "admonition": case "tip": case "warning": case "alert": return "callout";
            case "quote": case "blockquote": case "quotation": return "quote";
            case "table": case "grid": return "table";
            case "image": case "img": case "figure": case "picture": case "photo": return "image";
            case "chart": case "graph": case "plot": return "chart";
            case "code": case "pre": case "codeblock": case "code-block": case "snippet": return "code";
            case "divider": case "hr": case "rule": case "separator": return "divider";
            case "caption": case "figcaption": return "caption";
            case "pagebreak": case "page-break": case "newpage": case "break": return "pagebreak";
            case "spacer": case "space": case "gap": return "spacer";
            case "toc": case "contents": case "outline": return "toc";
            default: return s;
        }
    }

    private static List<Block> ParseMarkdown(string md)
    {
        var blocks = new List<Block>();
        var lines = md.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var para = new List<string>();

        void FlushPara()
        {
            if (para.Count == 0) return;
            blocks.Add(new Block { Type = "p", Text = string.Join(" ", para).Trim() });
            para.Clear();
        }

        var i = 0;
        while (i < lines.Length)
        {
            var trim = lines[i].Trim();
            if (trim.Length == 0) { FlushPara(); i++; continue; }

            if (trim.StartsWith("```", StringComparison.Ordinal))
            {
                FlushPara();
                var lang = trim.Substring(3).Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Append(lines[i]).Append('\n');
                    i++;
                }
                i++;
                blocks.Add(new Block
                {
                    Type = "code",
                    Text = code.ToString().TrimEnd('\n'),
                    Language = lang.Length == 0 ? null : lang,
                });
                continue;
            }

            if (trim.StartsWith("#", StringComparison.Ordinal))
            {
                FlushPara();
                var lvl = 0;
                while (lvl < trim.Length && trim[lvl] == '#') lvl++;
                blocks.Add(new Block
                {
                    Type = lvl <= 1 ? "h1" : lvl == 2 ? "h2" : "h3",
                    Text = trim.Substring(lvl).Trim(),
                });
                i++;
                continue;
            }

            if (trim == "---" || trim == "***" || trim == "___")
            {
                FlushPara();
                blocks.Add(new Block { Type = "divider" });
                i++;
                continue;
            }

            if (trim.StartsWith("|", StringComparison.Ordinal) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushPara();
                var block = new Block { Type = "table" };
                block.Headers.AddRange(SplitRow(trim));
                i += 2;
                while (i < lines.Length && lines[i].Trim().StartsWith("|", StringComparison.Ordinal))
                {
                    block.Rows.Add(SplitRow(lines[i].Trim()));
                    i++;
                }
                blocks.Add(block);
                continue;
            }

            if (trim.StartsWith(">", StringComparison.Ordinal))
            {
                FlushPara();
                var q = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    q.Append(lines[i].TrimStart().Substring(1).Trim()).Append(' ');
                    i++;
                }
                blocks.Add(new Block { Type = "quote", Text = q.ToString().Trim() });
                continue;
            }

            if (IsBullet(trim) || IsOrdered(trim))
            {
                FlushPara();
                var ordered = IsOrdered(trim);
                var list = new Block { Type = "list", Ordered = ordered };
                while (i < lines.Length)
                {
                    var lt = lines[i].Trim();
                    var bullet = IsBullet(lt);
                    var numbered = IsOrdered(lt);
                    if (!bullet && !numbered) break;
                    if (numbered != ordered) break; // 项目符号 / 编号切换 → 另起一个列表
                    list.Items.Add(numbered ? StripOrdered(lt) : lt.Substring(1).Trim());
                    i++;
                }
                blocks.Add(list);
                continue;
            }

            para.Add(trim);
            i++;
        }
        FlushPara();
        return blocks;
    }

    private static bool IsTableSeparator(string line)
    {
        var t = (line ?? "").Trim().Trim('|').Trim();
        if (t.Length == 0) return false;
        foreach (var ch in t)
            if (ch != '-' && ch != ':' && ch != ' ' && ch != '|') return false;
        return t.Contains('-');
    }

    private static List<string> SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("|", StringComparison.Ordinal)) t = t.Substring(1);
        if (t.EndsWith("|", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
        return t.Split('|').Select(x => x.Trim()).ToList();
    }

    private static bool IsBullet(string s)
        => s.StartsWith("- ", StringComparison.Ordinal) || s.StartsWith("* ", StringComparison.Ordinal)
        || s.StartsWith("• ", StringComparison.Ordinal) || s.StartsWith("+ ", StringComparison.Ordinal);

    private static bool IsOrdered(string s)
    {
        var k = 0;
        while (k < s.Length && char.IsDigit(s[k])) k++;
        return k > 0 && k + 1 < s.Length && (s[k] == '.' || s[k] == ')') && s[k + 1] == ' ';
    }

    private static string StripOrdered(string s)
    {
        var k = 0;
        while (k < s.Length && char.IsDigit(s[k])) k++;
        k++;
        while (k < s.Length && s[k] == ' ') k++;
        return s.Substring(k).Trim();
    }

    private sealed class TocRequest
    {
        public bool Enabled;
        public string? Title;
        public List<string> Items = new();
    }

    private static TocRequest ParseTocRequest(JsonElement root)
    {
        var req = new TocRequest();
        if (!root.TryGetProperty("toc", out var el)) return req;
        if (el.ValueKind == JsonValueKind.True) { req.Enabled = true; return req; }
        if (el.ValueKind == JsonValueKind.False) return req;
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s)) { req.Enabled = true; req.Title = s; }
            return req;
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            req.Enabled = true;
            req.Title = Str(el, "title");
            if (el.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray()) req.Items.Add(CellText(it));
        }
        return req;
    }

    private static List<string> CollectHeadings(List<Block> blocks, List<string> explicitItems)
    {
        if (explicitItems.Count > 0) return explicitItems;
        var list = new List<string>();
        foreach (var b in blocks)
            if ((b.Type == "h1" || b.Type == "h2") && !string.IsNullOrWhiteSpace(b.Text))
                list.Add(b.Text!.Trim());
        return list;
    }

    private static string? FirstHeading(List<Block> blocks)
    {
        foreach (var b in blocks)
            if (b.Type == "h1" && !string.IsNullOrWhiteSpace(b.Text)) return b.Text!.Trim();
        foreach (var b in blocks)
            if (!string.IsNullOrWhiteSpace(b.Text)) return b.Text!.Trim();
        return null;
    }

    // ===================== 落盘 =====================

    private static string ResolveOutputPath(JsonElement root, string title)
    {
        var outPath = Str(root, "outputPath");
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            var p = Path.GetFullPath(outPath!.Trim());
            try
            {
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("无法创建输出目录（" + outPath + "）：" + ex.Message);
            }
            return p;
        }
        var d = DefaultOutputDir();
        return UniquePath(d, SafeFileNameFromTitle(title));
    }

    /// <summary>默认输出目录：AGUI_PDF_OUT &gt; AGUI_DOCX_OUT（Docker 下为 /app/docs）&gt; AGUI_PPTX_OUT &gt; 主目录/agui-pdf &gt; 临时目录/agui-pdf。</summary>
    private static string DefaultOutputDir()
    {
        foreach (var envName in new[] { "AGUI_PDF_OUT", "AGUI_DOCX_OUT", "AGUI_PPTX_OUT" })
        {
            var configured = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(configured)) continue;
            try
            {
                var d = Path.GetFullPath(configured!.Trim());
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
                var d = Path.Combine(home, "agui-pdf");
                Directory.CreateDirectory(d);
                return d;
            }
        }
        catch { /* 无主目录 → 临时目录 */ }
        var tmp = Path.Combine(Path.GetTempPath(), "agui-pdf");
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
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4).TrimEnd();
        if (name.Length == 0) name = "document";
        if (name.Length > 80) name = name.Substring(0, 80).TrimEnd();
        var upper = name.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL" || upper.StartsWith("COM") || upper.StartsWith("LPT"))
            name = "_" + name;
        return name;
    }

    private static string UniquePath(string dir, string baseName)
    {
        var candidate = Path.Combine(dir, baseName + ".pdf");
        if (!File.Exists(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(dir, baseName + "-" + i + ".pdf");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, baseName + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pdf");
    }

    // ===================== 通用工具 =====================

    private static XBrush Brush(string hex) => new XSolidBrush(ColorOf(hex));

    private static XColor ColorOf(string hex)
    {
        var s = (hex ?? "").Trim().TrimStart('#');
        if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        if (s.Length != 6) s = "000000";
        var r = Convert.ToInt32(s.Substring(0, 2), 16);
        var g = Convert.ToInt32(s.Substring(2, 2), 16);
        var b = Convert.ToInt32(s.Substring(4, 2), 16);
        return XColor.FromArgb(r, g, b);
    }

    private static string? HexOrNull(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v!.Trim().TrimStart('#').ToUpperInvariant();
        return s.Length == 6 && s.All(Uri.IsHexDigit) ? s : null;
    }

    /// <summary>按 wa 权重混合 a（wa）与 b（1-wa），返回 RRGGBB。</summary>
    private static string Mix(string a, string b, double wa)
    {
        var ca = ColorOf(a);
        var cb = ColorOf(b);
        var f = Math.Max(0, Math.Min(1, wa));
        return HexOf(
            (int)Math.Round(ca.R * f + cb.R * (1 - f)),
            (int)Math.Round(ca.G * f + cb.G * (1 - f)),
            (int)Math.Round(ca.B * f + cb.B * (1 - f)));
    }

    private static string Darken(string hex, double amount) => Mix(hex, "000000", 1 - amount);

    private static string HexOf(int r, int g, int b)
    {
        static string H(int v) => Math.Max(0, Math.Min(255, v)).ToString("X2", CultureInfo.InvariantCulture);
        return H(r) + H(g) + H(b);
    }

    private static string ContrastInk(string bg)
    {
        var c = ColorOf(bg);
        return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 140 ? "FFFFFF" : "141414";
    }

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

    private static double? Num(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    private static bool? Bool(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = (v.GetString() ?? "").Trim().ToLowerInvariant();
            if (s is "true" or "1" or "yes") return true;
            if (s is "false" or "0" or "no") return false;
        }
        return null;
    }

    private static double ToNum(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out var d2)) return d2;
        }
        if (v.ValueKind == JsonValueKind.True) return 1;
        if (v.ValueKind == JsonValueKind.False) return 0;
        return 0;
    }

    private static string CellText(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
        if (v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Undefined) return "";
        return v.ToString();
    }

    private static string Safe(string s) => (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

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
                    if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else b.Append(c);
                    break;
            }
        }
        b.Append("\"");
        return b.ToString();
    }
}
