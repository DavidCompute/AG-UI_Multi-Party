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
//     "theme": "business|tech|warm|minimal|dark|vivid",   // 可选，历史主题（默认 business）
//              // 或 18 套命名调色板（推荐，按场景挑），详见下方 PALETTES
//     "style": "sharp|soft|rounded|pill",                  // 可选，版式风格，默认 soft
//     "action": "read", "path": "…pptx",                  // 可选：只读取既有 pptx 的文本，不生成文件
//     "template": "…pptx",                                 // 可选：套用该模板的母版/版式/配色出稿（不动原件）
//     "keepTemplateSlides": true,                          // 可选：保留模板原有页（默认清空，只借其皮）
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
//     stats    大数字    title, items:[ {value,label} ], cols
//     grid     网格卡    title, items:[ {title,text} ], cols:2|3
//     timeline 时间轴    title, items:[ {title,detail} ]（最多 6 步）
//     iconRows 图标行    title, items:[ {icon,title,text} ]（最多 6 行）
//     quote    引言      text, cite
//     image    图片      title, path, caption
//     chart    图表      title, chartType:"bar|line|pie|doughnut", categories:[…],
//                        series:[ {name,values:[…]} ], yLabel, caption
//                        chartType 加 "-native" 后缀 → 生成原生可编辑图表（DrawingML ChartPart）
//                        支持 bar/line/pie（doughnut 会自动降级为图片并在返回里说明）
//     summary  小结      title, bullets:[…]
//     end      结束页    title, subtitle
//   content 还可用 "layout":"timeline|grid|stats|iconRows" 直接指定子类型。
//   任何页都可带 "notes"（备注文字），写入演讲者备注。
//
//   【图表：图片 vs 原生】默认渲染成 PNG（视觉可控、兼容性最好，但不可在 PowerPoint 里改数据）。
//   对“要拿回去继续改数据”的场景可用原生图表（ChartPart + 嵌入数据工作簿）。
//   取舍：原生图表的 schema 严格得多（子元素顺序错就报“需要修复”），所以默认不开。
//
//   【套模板】template 传入既有 .pptx：复制到 outputPath 后再改副本（绝不写原件），
//   沿用模板的母版/版式，并从其主题读出配色与字体；默认清空模板原有页面（只借皮），
//   传 keepTemplateSlides:true 则追加在其后。
//
//   PALETTES（theme 的命名调色板，18 套；每套 5 色，角色由亮度/彩度自动分配）：
//     modern-wellness  business-authority  nature-outdoors  vintage-academic
//     soft-creative    bohemian            vibrant-tech     craft-artisan
//     tech-night(深底) education-charts    forest-eco       elegant-fashion
//     art-food         luxury-mysterious   pure-tech-blue   coastal-coral
//     vibrant-orange-mint                 platinum-white-gold
//   选法：医疗/健康→modern-wellness，年报/金融→business-authority，
//   学术/历史→vintage-academic，户外/农业→nature-outdoors，母婴/甜品→soft-creative，
//   婚礼/家居→bohemian，体育/路演→vibrant-tech，咖啡/手作→craft-artisan，
//   科技发布/天文→tech-night，统计/教育→education-charts，ESG/环保→forest-eco，
//   时尚/艺术→elegant-fashion，美食/展览→art-food，珠宝/高端咨询→luxury-mysterious，
//   云/AI/医疗→pure-tech-blue，旅行/饮品→coastal-coral，活动/快消→vibrant-orange-mint，
//   金融科技/品牌官网→platinum-white-gold。
//
//   STYLE：sharp（数据密集/严谨）| soft（通用，默认）| rounded（产品/市场）| pill（品牌/发布）。
//   它只影响页边距/间距/圆角，与 theme 正交，可自由组合（如 tech-night + pill）。
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
using C = DocumentFormat.OpenXml.Drawing.Charts;
using SS = DocumentFormat.OpenXml.Spreadsheet;
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

    // 版式度量由 style 决定（见 MetricsFor）。这里是属性而不是 const，
    // 于是所有渲染代码不用改就能跟着 style 走——换风格只需要换一次 _m。
    private static long MX => _m.Mx;
    private static long CW => W - 2 * _m.Mx;   // 内容宽
    private static long TitleY => _m.TitleY;
    private static long TitleH => _m.TitleH;
    private static long BodyY => _m.BodyY;
    private static long BodyH => _m.BodyH;
    private static long BadgeW => _m.BadgeW;
    private static long BadgeX => W - _m.Mx - _m.BadgeW;
    private static long BadgeY => _m.BadgeY;

    /// <summary>
    /// 版式度量：把页边距 / 标题位 / 正文区 / 内边距 / 元素间距 / 圆角收在一处，
    /// 由 <c>style</c>（sharp|soft|rounded|pill）一次性选定。
    ///
    /// <para>
    /// 移植自 MiniMax pptx-generator 的 design-system.md「Style Recipes」：
    /// 同一套内容换圆角与留白就能变成 4 种气质（紧密严谨 ↔ 通透高端）。
    /// 它给的是 10"×5.625" 画布的英寸值，这里按 13.333"/10" 等比换算到本技能的 16:9 画布。
    /// </para>
    /// </summary>
    private sealed class Metrics
    {
        public long Mx, TitleY, TitleH, BodyY, BodyH, BadgeW, BadgeY;
        public long Pad;   // 容器内边距（卡片 / 两栏 / 表格单元格）
        public long Gap;   // 元素间距
        public int Radius; // roundRect 的 adj 值（0..50000，50000 为半圆/胶囊）
        /// <summary>内部细碎尺寸的等比系数（以 soft 的 Pad 为 1）。</summary>
        public double Scale = 1.0;
        /// <summary>规范化后的风格名（回显给调用方/排障）。</summary>
        public string Name = "soft";
    }

    [ThreadStatic] private static Metrics? _currentMetrics;
    private static Metrics _m => _currentMetrics ?? (_currentMetrics = MetricsFor("soft"));

    /// <summary>本次生成里用了几张原生图表（回显给调用方）。</summary>
    [ThreadStatic] private static int _nativeCharts;
    /// <summary>请求了原生图表、但该图型不支持时的降级原因（不静默降级，写进返回 JSON）。</summary>
    [ThreadStatic] private static string? _nativeFallback;

    private static Metrics MetricsFor(string style)
    {
        // soft 的各项与历史常量完全一致（默认风格 = 换风格前的观感，保证老调用方观感不变）
        var m = new Metrics
        {
            Mx = 838200, TitleY = 457200, TitleH = 762000,
            BodyY = 1524000, BodyH = H - 1524000 - 838200,
            BadgeW = 457200, BadgeY = H - 685800,
            Pad = 228600, Gap = 342900, Radius = 12000,
        };
        switch ((style ?? "").Trim().ToLowerInvariant())
        {
            case "sharp": // 数据密集、严谨：窄边距 + 直角
                m.Mx = 594360; m.TitleY = 388620; m.TitleH = 685800;
                m.BodyY = 1280160; m.BodyH = H - 1280160 - 640080;
                m.BadgeW = 411480; m.BadgeY = H - 640080;
                m.Pad = 152400; m.Gap = 182880; m.Radius = 0;
                break;
            case "rounded": // 产品介绍 / 市场：大圆角 + 舒展
                m.Mx = 1005840; m.TitleY = 502920; m.TitleH = 800100;
                m.BodyY = 1645920; m.BodyH = H - 1645920 - 914400;
                m.BadgeW = 502920; m.BadgeY = H - 731520;
                m.Pad = 304800; m.Gap = 304800; m.Radius = 25000;
                break;
            case "pill": // 品牌发布 / 高端：胶囊圆角 + 大留白
                m.Mx = 1219200; m.TitleY = 548640; m.TitleH = 838200;
                m.BodyY = 1783080; m.BodyH = H - 1783080 - 1005840;
                m.BadgeW = 548640; m.BadgeY = H - 800100;
                m.Pad = 381000; m.Gap = 381000; m.Radius = 40000;
                break;
            default: break; // soft = 默认值
        }
        m.Scale = m.Pad / 228600.0; // 以 soft 为基准；内部零碎尺寸跟着等比缩放
        m.Name = (style ?? "").Trim().ToLowerInvariant() switch
        {
            "sharp" => "sharp",
            "rounded" => "rounded",
            "pill" => "pill",
            _ => "soft",
        };
        return m;
    }

    /// <summary>把内部零碎尺寸按当前 style 等比缩放（soft 下恒等）。</summary>
    private static long Sz(long v) => (long)Math.Round(v * _m.Scale);

    private const string NS_A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NS_P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string NS_R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NS_C = "http://schemas.openxmlformats.org/drawingml/2006/chart";

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
        /// <summary>画在 Accent 底色上的文字色（页码徽标）。</summary>
        public string OnAccent = "FFFFFF";
        /// <summary>画在 Primary 底色上的次要文字色（章节页副标题）。</summary>
        public string OnPrimary = "E8EEF7";
    }

    /// <summary>
    /// 18 套命名调色板（移植自 MiniMax pptx-generator 的 design-system.md）。
    /// 每套只给 5 个色值 + 是否深色底，<b>“谁当主色/底色”由亮度与彩度推出</b>（见 DeriveTheme）：
    /// 调色板是「设计语言」，角色映射是「渲染规则」，分开才好在 18 套上一致地成立。
    /// </summary>
    private static readonly (string Name, string C1, string C2, string C3, string C4, string C5, bool Dark)[] Palettes =
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

    [ThreadStatic] private static Theme? _currentTheme;

    private static Theme ResolveTheme(JsonElement root)
    {
        var t = LegacyTheme((Str(root, "theme") ?? "business").Trim().ToLowerInvariant())
            ?? FromPalette((Str(root, "theme") ?? "business").Trim().ToLowerInvariant())
            ?? LegacyTheme("business")!;
        // themeColors 覆盖（逐项）
        if (root.TryGetProperty("themeColors", out var tc) && tc.ValueKind == JsonValueKind.Object)
        {
            t.Primary = Hex(Str(tc, "primary"), t.Primary);
            t.Secondary = Hex(Str(tc, "secondary"), t.Secondary);
            t.Accent = Hex(Str(tc, "accent"), t.Accent);
            t.Light = Hex(Str(tc, "light"), t.Light);
            t.Bg = Hex(Str(tc, "bg"), t.Bg);
            t.Text = Hex(Str(tc, "text"), t.Text);
            // 用户改过颜色后，原来的“底色上的字”可能已经看不清，重算一次
            t.OnAccent = OnColor(t.Accent, t.Bg);
            t.OnPrimary = EnsureContrast(t.Light, t.Primary, 3.0);
        }
        t.FontTitle = Str(root, "fontTitle") ?? t.FontTitle;
        t.FontBody = Str(root, "fontBody") ?? t.FontBody;
        return t;
    }

    /// <summary>历史主题名（保持原有观感，不要改这些色值——老调用方依赖它们）。</summary>
    private static Theme? LegacyTheme(string name)
    {
        var t = new Theme();
        switch (name)
        {
            case "business": break; // 即默认值
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
            default: return null;
        }
        // 历史主题一律保持原样：徽标用底色作字（这是改造前的行为，不要“顺手改好”）。
        // 只补一个 OnPrimary：章节页副标题原本用 Light 画在主色底上，这里确认它看得清。
        t.OnAccent = t.Bg;
        t.OnPrimary = EnsureContrast(t.Light, t.Primary, 3.0);
        return t;
    }

    private static Theme? FromPalette(string name)
    {
        foreach (var p in Palettes)
            if (p.Name == name) return DeriveTheme(p.C1, p.C2, p.C3, p.C4, p.C5, p.Dark);
        return null;
    }

    /// <summary>
    /// 把调色板的 5 个色值分成角色：最深=primary（标题），最亮=bg（底色），
    /// 最花=accent（强调线/徽标），次亮=light（卡片底），secondary 取 primary 的浅色版以形成层级。
    ///
    /// <para>
    /// 最后统一过一道<b>可读性兜底</b>：调色板里难免有“很浅的次要色”或“很亮的黄”，
    /// 直接用会出现看不清的字。宁可把颜色调暗/调亮，也不能出现看不清——这条由
    /// 单测 <c>PptxDeckSkillTests.AllPalettes_KeepTextReadable</c> 对 18 套逐对断言。
    /// </para>
    /// </summary>
    private static Theme DeriveTheme(string c1, string c2, string c3, string c4, string c5, bool dark)
    {
        var all = new[] { c1, c2, c3, c4, c5 }.Select(Normalize).ToArray();
        var byLum = all.OrderBy(RelLum).ToArray(); // 暗 → 亮
        var t = new Theme();
        if (dark)
        {
            t.Bg = byLum[0]; t.Primary = byLum[4]; t.Light = byLum[1];
            t.Text = "E8ECF1";
        }
        else
        {
            // 底色必须是“面”，不能是个饱和色：实测 education-charts 的最亮色是
            // E9C46A（亮黄），直接当底色会做出一张黄底幻灯片。这里把彩度压下来。
            t.Bg = Surface(byLum[4]);
            t.Primary = byLum[0]; t.Light = byLum[3];
            t.Text = "333333";
        }
        // secondary：primary 往底色方向混一点，形成「主/次」层级（两个模式下方向都正确）
        t.Secondary = EnsureContrast(Mix(t.Primary, t.Bg, 0.25), t.Bg, 3.0);
        t.Accent = EnsureContrast(AccentFor(all, t.Bg, t.Primary), t.Bg, 3.0);
        // 强调色同时是“色块底”（页码徽标/序号圆）：必须至少一种文字色能在它上面读清楚。
        // 实测踩到：不调的话像 BC6C25 / CC7F16 这种中间调，白字 3.9、黑字 4.4，两边都不达标。
        t.Accent = EnsureReadableUnder(t.Accent, 4.5);
        t.Text = EnsureContrast(t.Text, t.Bg, 4.5);
        t.Primary = EnsureContrast(t.Primary, t.Bg, 4.5);
        t.OnAccent = OnColor(t.Accent, t.Bg);
        t.OnPrimary = EnsureContrast(t.Light, t.Primary, 3.0);
        return t;
    }

    /// <summary>
    /// 把候选底色压成“底色”：往白里混直到彩度降到可当背景的程度。
    /// 保留一点调色板的色相（比纯白更有味道），但不会是一条亮黄/亮橙。
    /// </summary>
    private static string Surface(string color)
    {
        if (Chroma(color) <= 0.25) return color;
        for (var t = 0.1; t <= 0.91; t += 0.1)
        {
            var s = Mix(color, "FFFFFF", t);
            if (Chroma(s) <= 0.25) return s;
        }
        return "FFFFFF";
    }

    // ---- 颜色工具（WCAG 相对亮度）----

    private static string Normalize(string hex)
        => hex.Trim().TrimStart('#').ToUpperInvariant();

    private static int[] RgbOf(string hex)
        => [Convert.ToInt32(hex.Substring(0, 2), 16), Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16)];

    private static double RelLum(string hex)
    {
        var c = RgbOf(hex);
        double Ch(int v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c[0]) + 0.7152 * Ch(c[1]) + 0.0722 * Ch(c[2]);
    }

    /// <summary>WCAG 对比度（1~21）。</summary>
    private static double Contrast(string a, string b)
    {
        var la = RelLum(a); var lb = RelLum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>线性混色：t=0 得到 a，t=1 得到 b。</summary>
    private static string Mix(string a, string b, double t)
    {
        var ca = RgbOf(a); var cb = RgbOf(b);
        var sb = new StringBuilder(6);
        for (var i = 0; i < 3; i++)
            sb.Append(((int)Math.Round(ca[i] + (cb[i] - ca[i]) * t)).ToString("X2"));
        return sb.ToString();
    }

    /// <summary>把 fg 朝黑/白方向调，直到与 bg 的对比度达标（WCAG）。</summary>
    private static string EnsureContrast(string fg, string bg, double min)
    {
        if (Contrast(fg, bg) >= min) return fg;
        var target = RelLum(bg) > 0.5 ? "000000" : "FFFFFF";
        for (var t = 0.1; t <= 1.0001; t += 0.1)
        {
            var c = Mix(fg, target, t);
            if (Contrast(c, bg) >= min) return c;
        }
        return target;
    }

    /// <summary>
    /// 把颜色调到「黑或白至少有一个能在其上达到 min 对比度」。
    /// 用于既是“色块底”又要承载文字的颜色（强调色）。中间调（如 BC6C25）朝黑调深。
    /// </summary>
    private static string EnsureReadableUnder(string color, double min)
    {
        double Best(string c) => Math.Max(Contrast(c, "FFFFFF"), Contrast(c, "1A1A1A"));
        if (Best(color) >= min) return color;
        for (var t = 0.05; t <= 1.0001; t += 0.05)
        {
            var c = Mix(color, "000000", t);
            if (Best(c) >= min) return c;
        }
        return "000000";
    }

    /// <summary>该底色上最易读的文字色：优先用给定色（保持主题感），对比不够就取黑/白里更好的那个。</summary>
    private static string OnColor(string bg, string preferred)
        => Contrast(preferred, bg) >= 4.5 ? preferred
         : (Contrast("FFFFFF", bg) >= Contrast("1A1A1A", bg) ? "FFFFFF" : "1A1A1A");

    /// <summary>彩度（HSI 意义上的饱和度）：用来挑“最花”的那个色做强调色。</summary>
    private static double Chroma(string hex)
    {
        var c = RgbOf(hex);
        var mx = c.Max(); var mn = c.Min();
        return mx == 0 ? 0 : (mx - mn) / (double)mx;
    }

    /// <summary>HSL 色相（0~360）；灰阶无色相，返回 0。</summary>
    private static double Hue(string hex)
    {
        var c = RgbOf(hex);
        double r = c[0] / 255.0, g = c[1] / 255.0, b = c[2] / 255.0;
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
    /// 挑强调色：优先“与主色色相明显不同”的候选。
    ///
    /// <para>
    /// 只按彩度挑会选出跟主色几乎同色的强调色——实测 forest-eco 选中了 3A5A40，
    /// 与主色 344E41 放在一起根本看不出差别，强调线/序号圆就白做了。
    /// </para>
    /// </summary>
    private static string AccentFor(string[] colors, string bg, string primary)
    {
        var pool = colors.Where(c => c != bg && c != primary).ToList();
        if (pool.Count == 0) pool = colors.ToList();

        var ph = Hue(primary);
        var distinct = pool.Where(c => HueDistance(Hue(c), ph) >= 30).ToList();
        if (distinct.Count > 0) pool = distinct;

        // 能在底色上看得见才考虑（否则强调线等于没画）
        var readable = pool.Where(c => Contrast(c, bg) >= 2.0).ToList();
        if (readable.Count > 0) pool = readable;

        return pool.OrderByDescending(Chroma).First();
    }

    private static string Hex(string? v, string fallback)
    {
        if (string.IsNullOrWhiteSpace(v)) return fallback;
        var s = v.Trim().TrimStart('#').ToUpperInvariant();
        return s.Length == 6 && s.All(Uri.IsHexDigit) ? s : fallback;
    }

    /// <summary>深色底上正文用亮色；用于判断某主题的 bg 是否为深色。</summary>
    private static bool IsDarkBg(Theme t) => RelLum(t.Bg) < 0.5;

    // ===== 入口 =====
    public static string Run(string input)
    {
        try
        {
            // 读取模式：不生成文件，只把既有 pptx 的内容读回来
            var readPath = ExtractReadPath(input);
            if (readPath is not null) return ReadDeck(readPath);

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
                // 图表字体报出来：容器里若没有中文字体，图表中文会缺字（乱码），有这个字段好排障
                + ",\"chartFont\":" + Js(ChartFontName) + ",\"chartFontCjk\":" + (ChartFontHasCjk ? "true" : "false")
                // 回显实际生效的配色与版式：用户/排障能直接看到“到底用了哪套色”。
                // 单测也靠它断言可读性底线（不必去解 XML）。
                + ",\"style\":" + Js(_m.Name)
                + ",\"palette\":" + PaletteJson(_currentTheme)
                // 原生图表用量与降级原因：调了却没用上必须说清楚，不能静默降级
                + ",\"nativeCharts\":" + _nativeCharts
                + ",\"nativeChartFallback\":" + (_nativeFallback is null ? "null" : Js(_nativeFallback))
                + ",\"message\":" + Js("已生成演示文稿：" + built.Path
                    + (ChartFontHasCjk ? "" : "（提示：当前环境未找到含中文字形的字体，图表中的中文可能显示为缺字/乱码；"
                        + "可在容器里安装 fonts-noto-cjk / fonts-droid-fallback 后重启）")) + "}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"scene\":" + Js(SceneName)
                + ",\"message\":" + Js("生成失败：" + ex.GetType().Name + "：" + ex.Message) + "}";
        }
    }

    /// <summary>若 <c>action=read</c> 则返回待读文件路径，否则 null。解析失败一律当“不是读取”处理，不影响生成路径。</summary>
    private static string? ExtractReadPath(string input)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(input));
            var root = doc.RootElement;
            if (!string.Equals((Str(root, "action") ?? "").Trim(), "read", StringComparison.OrdinalIgnoreCase))
                return null;
            return Str(root, "path") ?? Str(root, "template") ?? "";
        }
        catch { return null; }
    }

    // ===== 构建 =====
    private static (int Slides, string Path) Build(string input)
    {
        using var reqDoc = JsonDocument.Parse(ExtractJson(input));
        var root = reqDoc.RootElement;

        var theme = ResolveTheme(root);
        _currentTheme = theme;
        // 版式风格（sharp|soft|rounded|pill）：与主题正交，只影响页边距/间距/圆角
        _currentMetrics = MetricsFor(Str(root, "style") ?? "soft");
        _nativeCharts = 0;
        _nativeFallback = null;
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

        // 表格自动分页：过长的表格页在这里拆成多页。
        // 必须在渲染前拆——RenderSlide 一次只出一页，页型自己开不了新页。
        var pageJson = new List<string>();
        foreach (var s in slides) pageJson.AddRange(ExpandTableSlide(s));

        var path = ResolveOutputPath(root, title);

        // 套模板：保留模板的母版/版式/主题，把我们的内容渲染进去（改副本，不动原件）
        var templatePath = Str(root, "template");
        if (!string.IsNullOrWhiteSpace(templatePath))
            return BuildFromTemplate(root, pageJson, path, theme, templatePath!.Trim());

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
            for (var i = 0; i < pageJson.Count; i++)
            {
                using var elDoc = JsonDocument.Parse(pageJson[i]);
                var el = elDoc.RootElement;
                var sp = presPart.AddNewPart<SlidePart>();
                sp.AddPart(layoutPart); // 每张幻灯片必须挂一个版式
                var ctx = new SlideCtx(sp, theme, i + 1, pageJson.Count, title, subtitle, author, dateText);
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

        /// <summary>
        /// 挂一个原生图表部件（DrawingML ChartPart），返回关系 id。
        ///
        /// <para>
        /// 同时嵌入一份数据工作簿：没它 PowerPoint 仍能显示（缓存值在），
        /// 但「编辑数据」拿不到表格；有了它才是真正“可改数据”的图表。
        /// </para>
        /// </summary>
        public string AddChart(string chartSpaceXml, byte[] workbook)
        {
            var chartPart = Part.AddNewPart<ChartPart>();
            chartPart.ChartSpace = new C.ChartSpace(chartSpaceXml);
            var data = chartPart.AddEmbeddedPackagePart(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            using (var ms = new MemoryStream(workbook)) data.FeedData(ms);
            return Part.GetIdOfPart(chartPart);
        }
    }

    // ===== 读取 / 套模板（Phase 4）=====

    /// <summary>
    /// 读取既有 pptx：按放映顺序取出每页的文本。
    ///
    /// <para>
    /// 用途是“把这份 PPT 改一改 / 总结一下 / 照着它再出一份”——先把内容取回来才能谈后续。
    /// 只取文本，不做版式还原：我们不承诺“无损读取”，返回里也如实标注。
    /// </para>
    /// </summary>
    private static string ReadDeck(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return "{\"ok\":false,\"action\":\"read\",\"message\":" + Js("找不到文件：" + (path ?? "（未提供 path）")) + "}";
        try
        {
            using var doc = PresentationDocument.Open(path, false);
            var presPart = doc.PresentationPart;
            if (presPart?.Presentation is null)
                return "{\"ok\":false,\"action\":\"read\",\"message\":" + Js("不是有效的 .pptx（缺少 presentation.xml）") + "}";

            var sb = new StringBuilder();
            var slidesJson = new StringBuilder();
            var index = 0;
            foreach (var id in presPart.Presentation.SlideIdList?.Elements<P.SlideId>() ?? [])
            {
                var relId = id.RelationshipId?.Value;
                if (relId is null || presPart.GetPartById(relId) is not SlidePart sp) continue;
                var texts = sp.Slide.Descendants<A.Text>()
                    .Select(x => (x.Text ?? "").Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                index++;
                var joined = string.Join("\n", texts);
                sb.Append("【第 ").Append(index).Append(" 页】\n").Append(joined).Append("\n\n");
                if (slidesJson.Length > 0) slidesJson.Append(',');
                slidesJson.Append("{\"index\":").Append(index)
                    .Append(",\"notes\":").Append(Js(ReadNotes(sp)))
                    .Append(",\"texts\":[")
                    .Append(string.Join(",", texts.Select(Js))).Append("]}");
            }
            return "{\"ok\":true,\"action\":\"read\",\"scene\":" + Js(SceneName)
                + ",\"source\":" + Js(path)
                + ",\"slides\":" + index
                + ",\"slideTexts\":[" + slidesJson + "]"
                + ",\"text\":" + Js(sb.ToString().TrimEnd())
                + ",\"message\":" + Js("已读取 " + index + " 页（仅文本，不包含版式与图片）") + "}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"action\":\"read\",\"scene\":" + Js(SceneName)
                + ",\"message\":" + Js("读取失败：" + ex.GetType().Name + "：" + ex.Message) + "}";
        }
    }

    private static string ReadNotes(SlidePart sp)
    {
        try
        {
            return string.Join("\n", sp.NotesSlidePart?.NotesSlide?.Descendants<A.Text>()
                .Select(x => (x.Text ?? "").Trim()).Where(s => s.Length > 0) ?? []);
        }
        catch { return ""; }
    }

    /// <summary>
    /// 从模板的主题里读配色（accent1..3 / lt1 / lt2 / dk2）与字体。
    ///
    /// <para>
    /// 取色不一定是模板作者的原意（各家的主题用法不一），所以取出后仍会过一遍
    /// <see cref="EnsureContrast"/> 类守卫——宁可颜色略有出入，也不能出现看不清的字。
    /// </para>
    /// </summary>
    private static Theme? ThemeFromTemplate(SlideMasterPart? master, string? slideBgHint = null)
    {
        var themePart = master?.ThemePart;
        var elements = themePart?.Theme?.ThemeElements;
        if (elements is null) return null;

        string? Val(OpenXmlElement? c)
            => (c as A.Color2Type)?.RgbColorModelHex?.Val?.Value
            ?? (c as A.Color2Type)?.SystemColor?.LastColor?.Value;

        var cs = elements.ColorScheme;
        var t = new Theme();
        var accent1 = Val(cs?.Accent1Color);
        var accent2 = Val(cs?.Accent2Color);
        var accent3 = Val(cs?.Accent3Color);
        var lt1 = Val(cs?.Light1Color);
        var lt2 = Val(cs?.Light2Color);
        var dk2 = Val(cs?.Dark2Color);

        t.Primary = Hex(accent1, t.Primary);
        t.Secondary = Hex(accent2, t.Secondary);
        t.Accent = Hex(accent3, t.Primary);
        t.Light = Hex(lt2, t.Light);
        // 底色不能只读 lt1（主题色板里的 lt1 几乎总是 sysClr(window)，永远是白）。
        // 真正生效的是“幻灯片自己的 <p:bg> → 母版的 <p:bg> → lt1”这个优先级：
        // OOXML 里页背景会覆盖母版背景。实测踩到：反着取就会读到母版那份白底，
        // 于是拿深色模板出稿反而得到白底。
        t.Bg = slideBgHint ?? BackgroundHex(master) ?? Hex(lt1, "FFFFFF");
        t.Text = Hex(dk2, "333333");
        t.FontTitle = elements.FontScheme?.MajorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } mt ? mt : t.FontTitle;
        t.FontBody = elements.FontScheme?.MinorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } bt ? bt : t.FontBody;

        // 模板配色不可信：过一遍可读性守卫（模板用“主色当底”的玩法差异很大）
        if (RelLum(t.Bg) > 0.5)
        {
            t.Text = EnsureContrast(t.Text, t.Bg, 4.5);
            t.Primary = EnsureContrast(t.Primary, t.Bg, 4.5);
        }
        else
        {
            t.Text = EnsureContrast(t.Text, t.Bg, 4.5);
            t.Primary = EnsureContrast(t.Primary, t.Bg, 4.5);
        }
        t.Secondary = EnsureContrast(Mix(t.Primary, t.Bg, 0.25), t.Bg, 3.0);
        t.Accent = EnsureReadableUnder(EnsureContrast(t.Accent, t.Bg, 3.0), 4.5);
        t.OnAccent = OnColor(t.Accent, t.Bg);
        t.OnPrimary = EnsureContrast(t.Light, t.Primary, 3.0);
        return t;
    }

    /// <summary>从模板第一张幻灯片取底色（有些模板的底色只做在页上、不做在母版上）。</summary>
    private static string? FirstSlideBackgroundHex(PresentationPart presPart)
    {
        try
        {
            foreach (var id in presPart.Presentation.SlideIdList?.Elements<P.SlideId>() ?? [])
            {
                var rel = id.RelationshipId?.Value;
                if (rel is null || presPart.GetPartById(rel) is not SlidePart sp) continue;
                var hex = sp.Slide?.CommonSlideData?.Background
                    ?.Descendants<A.RgbColorModelHex>().FirstOrDefault()?.Val?.Value;
                if (!string.IsNullOrWhiteSpace(hex)) return hex;
            }
        }
        catch { /* 取不到就当没有 */ }
        return null;
    }

    /// <summary>从母版的背景填充里取底色；取不到返回 null（调用方回退到主题色板的 lt1）。</summary></summary>
    private static string? BackgroundHex(SlideMasterPart? master)
    {
        try
        {
            var hex = master?.SlideMaster?.CommonSlideData?.Background
                ?.Descendants<A.RgbColorModelHex>().FirstOrDefault()?.Val?.Value;
            return string.IsNullOrWhiteSpace(hex) ? null : hex;
        }
        catch { return null; }
    }

    /// <summary>
    /// 套模板出稿：保留模板的母版 / 版式 / 主题，把我们的内容渲染进去。
    ///
    /// <para>
    /// 不修改用户上传的原件——先复制到输出路径再改副本。
    /// 默认清空模板原有的幻灯片（只借它的“皮”），传 <c>keepTemplateSlides:true</c> 则追加在后面。
    /// </para>
    /// </summary>
    private static (int Slides, string Path) BuildFromTemplate(JsonElement root, List<string> pageJson,
        string path, Theme theme, string templatePath)
    {
        if (!File.Exists(templatePath))
            throw new InvalidOperationException("模板文件不存在：" + templatePath);
        if (string.Equals(Path.GetFullPath(templatePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("模板路径与输出路径相同，会覆盖原件；请指定不同的 outputPath。");

        var keep = root.TryGetProperty("keepTemplateSlides", out var kv) && kv.ValueKind == JsonValueKind.True;

        var src = Path.GetFullPath(templatePath);
        var dst = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, true);   // 改副本，不动原件

        using (var doc = PresentationDocument.Open(dst, true))
        {
            var presPart = doc.PresentationPart
                ?? throw new InvalidOperationException("模板缺少 presentation.xml，不是有效的 .pptx。");
            var master = presPart.SlideMasterParts.FirstOrDefault();
            var layout = master?.SlideLayoutParts.FirstOrDefault()
                ?? throw new InvalidOperationException("模板里没有可用的版式（slideLayout）。");

            // 模板里没显式给主题时，用模板自己的配色（这就是“套模板”的意义）
            if (Str(root, "theme") is null && !root.TryGetProperty("themeColors", out _))
            {
                var fromTemplate = ThemeFromTemplate(master, FirstSlideBackgroundHex(presPart));
                if (fromTemplate is not null)
                {
                    theme.Primary = fromTemplate.Primary; theme.Secondary = fromTemplate.Secondary;
                    theme.Accent = fromTemplate.Accent; theme.Light = fromTemplate.Light;
                    theme.Bg = fromTemplate.Bg; theme.Text = fromTemplate.Text;
                    theme.OnAccent = fromTemplate.OnAccent; theme.OnPrimary = fromTemplate.OnPrimary;
                    theme.FontTitle = fromTemplate.FontTitle; theme.FontBody = fromTemplate.FontBody;
                }
            }
            _currentTheme = theme;

            var pres = presPart.Presentation;
            var idList = pres.SlideIdList ??= new P.SlideIdList();
            if (!keep)
            {
                // 只借皮：把模板原有页面连同它们的部件一起删掉。
                // 【别只删 SlideId】只解除引用的话，ppt/slides/slideN.xml 还留在包里（仍占体积、
                // 文本仍可被搜到），实测产物里会同时存在模板的旧页——看着像“清空失败”。
                foreach (var id in idList.Elements<P.SlideId>().ToList())
                {
                    var rel = id.RelationshipId?.Value;
                    var part = rel is null ? null : presPart.GetPartById(rel);
                    id.Remove();
                    if (part is not null) presPart.DeletePart(part);
                }
            }
            var nextId = idList.Elements<P.SlideId>()
                .Select(x => x.Id?.Value ?? 0u).DefaultIfEmpty(255u).Max() + 1;

            var title = Str(root, "title") ?? "演示文稿";
            var total = pageJson.Count;
            for (var i = 0; i < total; i++)
            {
                using var elDoc = JsonDocument.Parse(pageJson[i]);
                var el = elDoc.RootElement;
                var sp = presPart.AddNewPart<SlidePart>();
                sp.AddPart(layout);
                var ctx = new SlideCtx(sp, theme, i + 1, total, title,
                    Str(root, "subtitle"), Str(root, "author"), Str(root, "date"));
                sp.Slide = new P.Slide(RenderSlide(el, ctx));
                var notes = Str(el, "notes");
                if (!string.IsNullOrWhiteSpace(notes))
                {
                    var notesMaster = presPart.NotesMasterPart;
                    AttachNotes(sp, notes!, notesMaster);
                }
                sp.Slide.Save();
                idList.Append(new P.SlideId { Id = nextId++, RelationshipId = presPart.GetIdOfPart(sp) });
            }
            if (!keep)
            {
                // 清完后要把 sldIdLst 留在 presentation 里（空列表比缺元素更稳）
                pres.SlideIdList = idList;
            }
            pres.Save();
            // 返回**整份稿子的总页数**，不是本次新生成的页数：
            // keepTemplateSlides=true 时两者不同，报“新生成的页数”会让调用方/用户以为丢了页。
            var totalSlides = idList.Elements<P.SlideId>().Count();
            return (totalSlides, dst);
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
            case "twocol":     // 注意：type 已 ToLowerInvariant，case 必须全小写
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
            case "timeline":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(TimelineBody(el, ctx));
                break;
            case "grid":
            case "cards":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(GridBody(el, ctx));
                break;
            case "stats":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(StatsBody(el, ctx));
                break;
            case "iconrows":   // 注意：type 已 ToLowerInvariant，case 必须全小写
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(IconRowsBody(el, ctx));
                break;
            case "chart":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(ChartBody(el, ctx));
                break;
            case "summary":
                shapes.Add(SlideTitle(Str(el, "title") ?? "小结", ctx));
                shapes.Add(BulletBody(el, ctx, accent: true));
                break;
            default: // content（可用 layout 指定子类型，省得模型记多个 type）
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add((Str(el, "layout") ?? "").Trim().ToLowerInvariant() switch
                {
                    "timeline" or "process" or "steps" => TimelineBody(el, ctx),
                    "grid" or "cards" => GridBody(el, ctx),
                    "stats" or "callouts" or "numbers" => StatsBody(el, ctx),
                    "iconrows" or "icon-rows" or "rows" => IconRowsBody(el, ctx),
                    _ => BulletBody(el, ctx, accent: false),
                });
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
            paras.Append(Para(sub!, 1600, t.OnPrimary, align: "l", spaceBefore: 12));
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
            Para(ctx.Index.ToString("00"), 1100, t.OnAccent, bold: true, align: "ctr"), anchor: "ctr"));
        return sb.ToString();
    }

    private static string Footnote(string text, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var color = IsDarkBg(t) ? t.Light : "7A7A7A";
        return TextBox(ctx.NextId(), MX, H - 1097280, CW - BadgeW - _m.Pad, 365760,
            Para(text, 1100, color, align: "l"));
    }

    // ---- 正文：项目符号 ----

    /// <summary>
    /// 「内容太多不越界」的统一处理：按正文区高度估算占用，放不下就<b>整体缩字号与行距</b>。
    ///
    /// <para>
    /// 实测踩到：本类文本框是固定高度、且没有 autofit，要点一多/一长就直接溢出正文区，
    /// 进而画出页面底部（越界）。这里先量一下再排版。
    /// </para>
    /// </summary>
    private static double FitScaleFor(List<(string Text, int Size, int SpaceBefore, double LineSpacing)> paras, long boxH)
    {
        var need = EstimateHeightEmu(paras, CW - _m.Gap, boxH);
        if (need <= boxH) return 1.0;
        // 下限 0.6：再小就看不清了，宁可字号小一点也不要溢出页面
        return Math.Max(0.6, (double)boxH / need);
    }

    /// <summary>
    /// 估算一组段落排版后的总高度（EMU）。CJK 按 1 em、ASCII 按 0.55 em 估宽。
    ///
    /// <para>
    /// <b>行距单位是「倍率」</b>（1.25 = 1.25 倍行距），与所有调用方传的值一致。
    /// 实测踩过：这里原本写成 <c>lineSpacing / 100.0</c>（当成百分数），
    /// 而调用方一律传 1.25 这种倍率——于是估出来的高度只有真实值的 1%，
    /// <c>need &lt;= boxH</c> 永远成立、缩放系数永远是 1.0，
    /// 也就是说<b>「缩字号」一直是死代码</b>（正文一多就直接溢出，没人发现）。
    /// 这里同时兼容传入百分数（&gt;5 视为百分数）以防以后有人写 125。
    /// </para>
    /// </summary>
    private static long EstimateHeightEmu(List<(string Text, int Size, int SpaceBefore, double LineSpacing)> paras, long availW, long boxH)
    {
        if (availW < 100000) availW = 100000;
        long total = 0;
        foreach (var (text, size, spaceBefore, lineSpacing) in paras)
        {
            var em = 0.0;
            foreach (var ch in text) em += ch < 0x2E80 ? 0.55 : 1.0;
            var widthEmu = em * size * 127.0;                       // 1pt = 12700 EMU，size 以百分之一磅计
            var lines = Math.Max(1, (int)Math.Ceiling(widthEmu / availW));
            var mult = lineSpacing <= 0 ? 1.25 : (lineSpacing > 5 ? lineSpacing / 100.0 : lineSpacing);
            total += (long)(lines * size * 127.0 * mult) + spaceBefore * 127L;
        }
        return total;
    }

    private static string BulletBody(JsonElement el, SlideCtx ctx, bool accent)
    {
        var t = ctx.Theme;
        var items = StringList(el, "bullets");
        if (items.Count == 0)
        {
            var p = Str(el, "text");
            if (!string.IsNullOrWhiteSpace(p)) items.Add(p!);
        }
        if (items.Count == 0) items.Add("（本页暂无要点）");

        // 先把「要排什么」整理成数据，再估算高度决定字号缩放，最后才生成 XML（内容太多就缩，不越界）
        var plan = new List<(string Text, int Size, int SpaceBefore, double LineSpacing)>();
        var shapes = new List<(bool TwoLine, string Label, string Detail)>();
        foreach (var raw in items)
        {
            // 支持「小标题：说明」的两行结构，让内容页更有层次
            var parts = SplitLabel(raw);
            if (parts is null)
            {
                shapes.Add((false, raw, ""));
                plan.Add((raw, 1700, 10, 1.25));
            }
            else
            {
                shapes.Add((true, parts.Value.Label, parts.Value.Detail));
                plan.Add((parts.Value.Label, 1800, 12, 1.20));
                plan.Add((parts.Value.Detail, 1500, 0, 1.25));
            }
        }
        var scale = FitScaleFor(plan, BodyH);

        var paras = new StringBuilder();
        foreach (var s in shapes)
        {
            var color = accent ? t.Primary : t.Text;
            if (!s.TwoLine)
            {
                paras.Append(Para(s.Label, Scaled(1700, scale), color, align: "l", bullet: "•",
                    spaceBefore: 10, lineSpacing: (int)Math.Round(125 * scale), marL: (int)_m.Gap));
            }
            else
            {
                paras.Append(Para(s.Label, Scaled(1800, scale), t.Primary, bold: true, align: "l",
                    bullet: "•", spaceBefore: 12, lineSpacing: (int)Math.Round(120 * scale), marL: (int)_m.Gap));
                paras.Append(Para(s.Detail, Scaled(1500, scale), t.Secondary, align: "l",
                    lineSpacing: (int)Math.Round(125 * scale), marL: (int)_m.Gap));
            }
        }
        return TextBox(ctx.NextId(), MX, BodyY, CW, BodyH, paras.ToString(), anchor: "t");
    }

    /// <summary>按缩放系数调整字号（size 以百分之一磅计），并守住可读下限（≈12pt）。</summary>
    private static int Scaled(int size, double scale)
        => Math.Max(1200, (int)Math.Round(size * scale));

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

        // 目录项一多/一长就会撑出正文区（本类文本框是固定高度、无 autofit），先估高再缩
        var plan = items.Select(it => (Text: it, Size: 1800, SpaceBefore: 14, LineSpacing: 1.20)).ToList();
        var scale = ScaleForHeight(plan, CW - _m.Gap, BodyH);

        var paras = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            paras.Append(Para((i + 1).ToString("00") + "   " + items[i], Scaled(1800, scale), t.Text,
                align: "l", spaceBefore: (int)Math.Round(14 * scale), lineSpacing: (int)Math.Round(120 * scale)));
        }
        return TextBox(ctx.NextId(), MX, BodyY, CW, BodyH, paras.ToString(), anchor: "t");
    }

    // ---- 两栏 ----
    private static string TwoColBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var gap = _m.Gap;
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
            if (bullets.Count == 0) bullets.Add("（空）");
            // 两栏是「小标题 + 要点」，要点一多就溢出卡片：先估高再缩（两栏各自算，栏内保持一致）
            var availW = colW - 2 * _m.Pad;
            var availH = BodyH - 2 * _m.Pad;
            var plan = new List<(string Text, int Size, int SpaceBefore, double LineSpacing)>();
            if (!string.IsNullOrWhiteSpace(heading)) plan.Add((heading!, 1900, 0, 1.10));
            foreach (var b in bullets) plan.Add((b, 1500, 10, 1.25));
            var scale = ScaleForHeight(plan, availW, availH);

            if (!string.IsNullOrWhiteSpace(heading))
                inner.Append(Para(heading!, Scaled(1900, scale), t.Primary, bold: true, align: "l",
                    lineSpacing: (int)Math.Round(110 * scale)));
            foreach (var b in bullets)
                inner.Append(Para(b, Scaled(1500, scale), t.Text, align: "l", bullet: "•",
                    spaceBefore: (int)Math.Round(10 * scale), lineSpacing: (int)Math.Round(125 * scale),
                    marL: (int)_m.Gap));
            shapes.Append(TextBox(ctx.NextId(), x + _m.Pad, BodyY + _m.Pad, availW, availH,
                inner.ToString(), anchor: "t"));
        }
        return shapes.ToString();
    }

    // ---- 表格自动分页 ----

    /// <summary>读 rows 二维数组（TableBody 与分页共用同一套读法，避免两处口径不一致）。</summary>
    private static List<List<string>> ReadRows(JsonElement el)
    {
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
        return rows;
    }

    /// <summary>
    /// 一页最多放几行表体。按**字号下限**保守估算，口径与 TableBody 一致。
    /// 保守取值的好处是每页切得少一些、实际渲染时不必被压到下限（先切满再压字号反而难看）。
    /// </summary>
    private static int TableRowsPerPage(int cols, List<List<string>> rows)
    {
        if (cols <= 0) return Math.Max(1, rows.Count);
        var colW = CW / cols;
        var cellMarH = Sz(91440);
        var cellMarV = Sz(45720);
        const double s = 0.70;
        var sz = Math.Max(1000, (int)Math.Round(1300 * s));
        var lineH = (long)(sz * 127.0 * 1.10);
        var avail = Math.Max(100000.0, colW - 2 * cellMarH);
        var used = (long)(457200 * s);          // 表头行
        var fit = 0;
        foreach (var row in rows)
        {
            var lines = 1;
            for (var c = 0; c < cols; c++)
            {
                var em = 0.0;
                foreach (var ch in c < row.Count ? row[c] : "") em += ch < 0x2E80 ? 0.55 : 1.0;
                lines = Math.Max(lines, (int)Math.Ceiling(em * sz * 127.0 / avail));
            }
            var h = Math.Max((long)(Sz(320000) * s), lines * lineH + 2 * cellMarV);
            if (used + h > BodyH) break;
            used += h;
            fit++;
        }
        return Math.Max(1, fit);
    }

    /// <summary>
    /// 把一页展开成 1..n 页（目前只有过长的表格页会拆）。返回**JSON 字符串**列表。
    ///
    /// <para>
    /// 为什么在渲染前拆：<c>RenderSlide</c> 一次只出一页（返回一个 slide 的 XML），
    /// 页型自己没法“再开一页”。所以在 <see cref="Build"/> 进入渲染前先把超长表格切块，
    /// 每块变成一页正常的 table 页（标题带“（n/m）”，方便一眼看出是续表）。
    /// </para>
    /// </summary>
    private static List<string> ExpandTableSlide(JsonElement el)
    {
        var single = new List<string> { el.GetRawText() };
        if (!string.Equals((Str(el, "type") ?? "").Trim(), "table", StringComparison.OrdinalIgnoreCase))
            return single;
        var rows = ReadRows(el);
        if (rows.Count == 0) return single;
        var headers = StringList(el, "headers");
        var cols = Math.Max(headers.Count, rows.Max(r => r.Count));
        var perPage = TableRowsPerPage(cols, rows);
        if (rows.Count <= perPage) return single;

        var pages = (rows.Count + perPage - 1) / perPage;
        var baseTitle = Str(el, "title") ?? "";
        var result = new List<string>(pages);
        for (var p = 0; p < pages; p++)
        {
            var chunk = rows.Skip(p * perPage).Take(perPage).ToList();
            var sb = new StringBuilder("{\"type\":\"table\",\"title\":");
            sb.Append(Js(baseTitle + "（" + (p + 1) + "/" + pages + "）"));
            sb.Append(",\"headers\":[").Append(string.Join(",", headers.Select(Js))).Append(']');
            sb.Append(",\"rows\":[");
            sb.Append(string.Join(",", chunk.Select(r => "[" + string.Join(",", r.Select(Js)) + "]")));
            sb.Append(']');
            // 备注只挂第一页，不重复到每一页
            var notes = Str(el, "notes");
            if (p == 0 && !string.IsNullOrWhiteSpace(notes)) sb.Append(",\"notes\":").Append(Js(notes!));
            sb.Append('}');
            result.Add(sb.ToString());
        }
        return result;
    }

    // ---- 表格 ----
    private static string TableBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var headers = StringList(el, "headers");
        var rows = ReadRows(el);
        var cols = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
        if (cols == 0) return BulletBody(el, ctx, accent: false);

        var colW = CW / cols;
        var headerH = 457200L;
        const int headerSz = 1500, cellSz = 1300;
        var cellMarH = Sz(91440);                       // 与 TableCell 的 marL/marR 一致
        var cellMarV = Sz(45720);                       // 与 marT/marB 一致
        var minRowH = Sz(320000);

        // 行高要按“最长那一列折几行”算，而不是写死一个高度：写死时长文本会被卡在行内/撑出表格。
        int LinesFor(string text, int size)
        {
            var em = 0.0;
            foreach (var ch in text) em += ch < 0x2E80 ? 0.55 : 1.0;
            var avail = Math.Max(100000.0, colW - 2 * cellMarH);
            return Math.Max(1, (int)Math.Ceiling(em * size * 127.0 / avail));
        }

        long RowH(int r, double s)   // TableCell 用 lineSpacing=110 → 约 1.10 倍行距
        {
            var sz = Math.Max(1000, (int)Math.Round(cellSz * s));
            var lineH = (long)(sz * 127.0 * 1.10);
            var lines = 1;
            for (var c = 0; c < cols; c++)
                lines = Math.Max(lines, LinesFor(c < rows[r].Count ? rows[r][c] : "", sz));
            return Math.Max((long)(minRowH * s), lines * lineH + 2 * cellMarV);
        }

        // 字号下限 0.70（约 9pt）：再小就不如不显示
        const double minScale = 0.70;
        var scale = 1.0;
        for (var i = 0; i < 4 && scale > minScale; i++)
        {
            long total = (long)(headerH * scale);
            for (var r = 0; r < rows.Count; r++) total += RowH(r, scale);
            if (total <= BodyH) break;
            scale = Math.Max(minScale, scale * ((double)BodyH / total));
        }
        var hdrH = (long)(headerH * scale);
        var szHeader = Math.Max(1000, (int)Math.Round(headerSz * scale));
        var szCell = Math.Max(1000, (int)Math.Round(cellSz * scale));
        var lineHCell = (long)(szCell * 127.0 * 1.10);

        // 缩到下限还装不下时，**减少行数并明说**：
        // 一张幻灯片本来就装不下“30 行 × 每格折 4 行”这种东西，硬画出去只会得到看不见的表。
        // 宁可只显示能显示的行 + 一行“另有 N 行未显示”，也不静默丢数据。
        var noteH = Math.Max((long)(minRowH * scale), lineHCell + 2 * cellMarV);
        var shownRows = rows.Count;
        var used = hdrH;
        for (var r = 0; r < rows.Count; r++)
        {
            var h = RowH(r, scale);
            var reserve = r < rows.Count - 1 ? noteH : 0;   // 先给“未显示”提示行留位
            if (used + h + reserve > BodyH && r > 0) { shownRows = r; break; }
            used += h;
        }
        var truncated = shownRows < rows.Count;

        var tbl = new StringBuilder();
        // 斑马纹 + 表头底色：不用内置表格样式（依赖 theme 部件），直接给单元格上色，兼容性最好
        tbl.Append("<a:tbl><a:tblPr firstRow=\"1\" bandRow=\"0\"/><a:tblGrid>");
        for (var c = 0; c < cols; c++) tbl.Append("<a:gridCol w=\"").Append(colW).Append("\"/>");
        tbl.Append("</a:tblGrid>");

        var tableH = 0L;
        if (headers.Count > 0)
        {
            tbl.Append("<a:tr h=\"").Append(hdrH).Append("\">");
            for (var c = 0; c < cols; c++)
            {
                var text = c < headers.Count ? headers[c] : "";
                tbl.Append(TableCell(text, szHeader, t.Bg, t.Primary, bold: true, colW));
            }
            tbl.Append("</a:tr>");
            tableH += hdrH;
        }
        for (var r = 0; r < shownRows; r++)
        {
            var rh = RowH(r, scale);
            tbl.Append("<a:tr h=\"").Append(rh).Append("\">");
            var fill = r % 2 == 1 ? t.Light : t.Bg;
            for (var c = 0; c < cols; c++)
            {
                var text = c < rows[r].Count ? rows[r][c] : "";
                tbl.Append(TableCell(text, szCell, t.Text, fill, bold: false, colW));
            }
            tbl.Append("</a:tr>");
            tableH += rh;
        }
        if (truncated)
        {
            // 明说还有多少行没显示（不静默丢数据）
            var note = $"… 另有 {rows.Count - shownRows} 行未显示（本页共 {rows.Count} 行，完整数据请拆页或改用附件）";
            tbl.Append("<a:tr h=\"").Append(noteH).Append("\">");
            for (var c = 0; c < cols; c++)
            {
                var text = c == 0 ? note : "";
                tbl.Append(TableCell(text, szCell, t.Secondary, t.Light, bold: false, colW));
            }
            tbl.Append("</a:tr>");
            tableH += noteH;
        }
        tbl.Append("</a:tbl>");
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

        // 卡片内是「大数字 + 说明」，数字字号很大，说明文字一长/一多就会溢出卡片。
        // 所有卡片共用一个缩放系数，避免同一排卡片字号不一样。
        var availW = cardW - Sz(182880);
        var availH = cardH - Sz(457200);
        var scale = 1.0;
        foreach (var it in items)
        {
            var plan = new List<(string Text, int Size, int SpaceBefore, double LineSpacing)>
                { (it.Value, 3200, 0, 1.00), (it.Label, 1300, 6, 1.15) };
            scale = Math.Min(scale, ScaleForHeight(plan, availW, availH));
        }

        for (var i = 0; i < items.Count; i++)
        {
            var row = i / n;
            var col = i % n;
            var x = MX + (cardW + gap) * col;
            var y = BodyY + (cardH + gap) * row;
            shapes.Append(Rect(ctx.NextId(), x, y, cardW, cardH, t.Light, radius: true));
            shapes.Append(Rect(ctx.NextId(), x, y, cardW, 45720, t.Accent));
            var inner = new StringBuilder();
            inner.Append(Para(items[i].Value, Scaled(3200, scale), t.Primary, bold: true, align: "ctr",
                lineSpacing: (int)Math.Round(100 * scale), font: t.FontTitle));
            if (items[i].Label.Length > 0)
                inner.Append(Para(items[i].Label, Scaled(1300, scale), t.Secondary, align: "ctr",
                    spaceBefore: 6, lineSpacing: (int)Math.Round(115 * scale)));
            shapes.Append(TextBox(ctx.NextId(), x + Sz(91440), y + _m.Pad, availW, availH,
                inner.ToString(), anchor: "ctr"));
        }
        return shapes.ToString();
    }

    // ---- 条目读取（新页型共用）----

    /// <summary>
    /// 从 <c>items</c> 里按候选键依次取字段（如 value/label、title/text、icon）。
    /// 字符串项直接当第一个字段（允许 <c>items: ["要点一", …]</c> 这种简写）。
    /// </summary>
    private static List<(string V1, string V2, string V3)> Items(
        JsonElement el, string[] k1, string[] k2, string[] k3)
    {
        var list = new List<(string, string, string)>();
        if (!el.TryGetProperty("items", out var iv) || iv.ValueKind != JsonValueKind.Array) return list;
        foreach (var it in iv.EnumerateArray())
        {
            if (it.ValueKind == JsonValueKind.String)
            {
                var s = (it.GetString() ?? "").Trim();
                if (s.Length > 0) list.Add((s, "", ""));
                continue;
            }
            if (it.ValueKind != JsonValueKind.Object) continue;
            var v1 = PickKey(it, k1);
            var v2 = PickKey(it, k2);
            var v3 = PickKey(it, k3);
            if (v1.Length == 0 && v2.Length == 0 && v3.Length == 0) continue;
            list.Add((v1, v2, v3));
        }
        return list;
    }

    private static string PickKey(JsonElement o, string[] keys)
    {
        foreach (var k in keys)
        {
            var v = Str(o, k);
            if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
        }
        return "";
    }

    private static int IntOf(JsonElement o, string name, int fallback)
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        }
        return fallback;
    }

    /// <summary>把一组段落估高并按可用高度给出缩放（下限 0.6）。</summary>
    private static double ScaleForHeight(List<(string Text, int Size, int SpaceBefore, double LineSpacing)> plan, long availW, long availH)
    {
        if (availH <= 0) return 0.6;
        var need = EstimateHeightEmu(plan, availW, availH);
        return need <= availH ? 1.0 : Math.Max(0.6, (double)availH / need);
    }

    // ---- 大数字看板（stat callouts）----

    /// <summary>
    /// 大号数字 + 小标签。与 <c>kpi</c> 的区别：不加卡片底、数字更大（移植自 design-system.md
    /// 的 “Large stat callouts 60-72pt” 与 “Large stat callouts (big numbers with small labels below)”。
    /// </summary>
    private static string StatsBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var items = Items(el, ["value"], ["label", "text", "detail"], []);
        if (items.Count == 0) return BulletBody(el, ctx, accent: false);

        var cols = Math.Max(1, Math.Min(items.Count, IntOf(el, "cols", items.Count <= 3 ? items.Count : 3)));
        var rowsN = (items.Count + cols - 1) / cols;
        var cellW = (CW - _m.Gap * (cols - 1)) / cols;
        var cellH = (BodyH - _m.Gap * (rowsN - 1)) / rowsN;
        var ruleH = Sz(285750);                       // 数字上方的强调线占用的高度
        var availH = Math.Max(Sz(228600), cellH - ruleH);

        // 数字很大（48pt）且单元格窄，先按最长的一条估高再统一缩放（全页一致，不逐个变字号）
        var scale = 1.0;
        foreach (var it in items)
        {
            var plan = new List<(string Text, int Size, int SpaceBefore, double LineSpacing)>
                { (it.V1, 4800, 0, 1.05), (it.V2, 1400, 8, 1.15) };
            scale = Math.Min(scale, ScaleForHeight(plan, cellW, availH));
        }

        var shapes = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var x = MX + (cellW + _m.Gap) * (i % cols);
            var y = BodyY + (cellH + _m.Gap) * (i / cols);
            var inner = new StringBuilder();
            inner.Append(Para(items[i].V1, Scaled(4800, scale), t.Accent, bold: true, align: "l",
                lineSpacing: (int)Math.Round(95 * scale), font: t.FontTitle));
            if (items[i].V2.Length > 0)
                inner.Append(Para(items[i].V2, Scaled(1400, scale), t.Text, align: "l", spaceBefore: 8,
                    lineSpacing: (int)Math.Round(115 * scale)));
            shapes.Append(Rect(ctx.NextId(), x, y, Sz(685800), Sz(45720), t.Accent));
            shapes.Append(TextBox(ctx.NextId(), x, y + ruleH, cellW, availH, inner.ToString(), anchor: "t"));
        }
        return shapes.ToString();
    }

    // ---- 时间轴 / 流程 ----

    /// <summary>横向分步：序号圆 + 连接线 + 标题/说明。最多 6 步（再多横向排不下，请拆页）。</summary>
    private static string TimelineBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var all = Items(el, ["title", "label", "step"], ["detail", "text", "desc"], []);
        if (all.Count == 0) return BulletBody(el, ctx, accent: false);
        var steps = all.Take(6).ToList();
        var n = steps.Count;

        var slotW = (CW - _m.Gap * (n - 1)) / n;
        var ring = Math.Min(Sz(685800), slotW - Sz(45720));
        var cy = BodyY + Math.Min(BodyH / 3, Sz(914400));      // 圆心
        var textTop = cy + ring / 2 + _m.Gap;
        var textH = Math.Max(Sz(457200), BodyY + BodyH - textTop);

        var scale = 1.0;
        foreach (var s in steps)
        {
            var plan = new List<(string Text, int Size, int SpaceBefore, double LineSpacing)>
                { (s.V1, 1600, 0, 1.10), (s.V2, 1250, 6, 1.15) };
            scale = Math.Min(scale, ScaleForHeight(plan, slotW, textH));
        }

        var shapes = new StringBuilder();
        // 连接线：圆心高度，首尾各留半个槽宽
        if (n > 1)
            shapes.Append(Rect(ctx.NextId(), MX + slotW / 2, cy - Sz(22860), CW - slotW, Sz(45720), t.Light));

        for (var i = 0; i < n; i++)
        {
            var x = MX + (slotW + _m.Gap) * i;
            var cxc = x + slotW / 2;
            shapes.Append(Ellipse(ctx.NextId(), cxc - ring / 2, cy - ring / 2, ring, ring, t.Accent));
            shapes.Append(TextBox(ctx.NextId(), cxc - ring / 2, cy - ring / 2, ring, ring,
                Para((i + 1).ToString("00"), Scaled(1500, scale), t.OnAccent, bold: true, align: "ctr"), anchor: "ctr"));

            var txt = new StringBuilder();
            txt.Append(Para(steps[i].V1, Scaled(1600, scale), t.Primary, bold: true, align: "ctr",
                lineSpacing: (int)Math.Round(110 * scale)));
            if (steps[i].V2.Length > 0)
                txt.Append(Para(steps[i].V2, Scaled(1250, scale), t.Text, align: "ctr", spaceBefore: 6,
                    lineSpacing: (int)Math.Round(115 * scale)));
            shapes.Append(TextBox(ctx.NextId(), x, textTop, slotW, textH, txt.ToString(), anchor: "t"));
        }
        return shapes.ToString();
    }

    // ---- 网格卡片 ----

    /// <summary>N 列网格卡片（cols 2~3）。适合并列要点、能力矩阵、方案对比。</summary>
    private static string GridBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var cards = Items(el, ["title", "heading", "label"], ["text", "detail", "desc"], []);
        if (cards.Count == 0) return BulletBody(el, ctx, accent: false);

        var cols = Math.Max(2, Math.Min(IntOf(el, "cols", 2), 3));
        var rowsN = (cards.Count + cols - 1) / cols;
        var cardW = (CW - _m.Gap * (cols - 1)) / cols;
        var cardH = (BodyH - _m.Gap * (rowsN - 1)) / rowsN;
        var availW = cardW - 2 * _m.Pad;
        var availH = cardH - 2 * _m.Pad;

        var shapes = new StringBuilder();
        for (var i = 0; i < cards.Count; i++)
        {
            var x = MX + (cardW + _m.Gap) * (i % cols);
            var y = BodyY + (cardH + _m.Gap) * (i / cols);
            var plan = new List<(string Text, int Size, int SpaceBefore, double LineSpacing)>
                { (cards[i].V1, 1700, 0, 1.10), (cards[i].V2, 1300, 8, 1.20) };
            var scale = ScaleForHeight(plan, availW, availH);

            var inner = new StringBuilder();
            if (cards[i].V1.Length > 0)
                inner.Append(Para(cards[i].V1, Scaled(1700, scale), t.Primary, bold: true, align: "l",
                    lineSpacing: (int)Math.Round(110 * scale)));
            if (cards[i].V2.Length > 0)
                inner.Append(Para(cards[i].V2, Scaled(1300, scale), t.Text, align: "l", spaceBefore: 8,
                    lineSpacing: (int)Math.Round(120 * scale)));

            shapes.Append(Rect(ctx.NextId(), x, y, cardW, cardH, t.Light, radius: true));
            shapes.Append(Rect(ctx.NextId(), x, y, Sz(45720), cardH, t.Accent));   // 左侧色条
            shapes.Append(TextBox(ctx.NextId(), x + _m.Pad, y + _m.Pad, availW, availH, inner.ToString(), anchor: "t"));
        }
        return shapes.ToString();
    }

    // ---- 图标行 ----

    /// <summary>
    /// 图标行：彩色圆 + 短标（1~2 字）+ 标题 + 说明。
    /// 本技能不携带图标字体/图标素材，所以“图标”由调用方给极短文字（如 “✓” “1” “A”），
    /// 缺省时用序号——总比画一个空框好。
    /// </summary>
    private static string IconRowsBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var all = Items(el, ["title", "heading", "label"], ["text", "detail", "desc"], ["icon", "badge", "mark"]);
        if (all.Count == 0) return BulletBody(el, ctx, accent: false);
        var rows = all.Take(6).ToList();
        var n = rows.Count;

        var rowH = (BodyH - _m.Gap * (n - 1)) / n;
        var ring = Math.Min(Sz(609600), rowH);
        var textX = ring + _m.Pad;
        var textW = CW - textX;
        var availH = rowH - Sz(114300);

        var scale = 1.0;
        foreach (var r in rows)
        {
            var plan = new List<(string Text, int Size, int SpaceBefore, double LineSpacing)>
                { (r.V1, 1600, 0, 1.10), (r.V2, 1300, 6, 1.20) };
            scale = Math.Min(scale, ScaleForHeight(plan, textW, availH));
        }

        var shapes = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            var y = BodyY + (rowH + _m.Gap) * i;
            // 圆：文字色用 OnAccent（与页码徽标同一套“底色上的字”规则）
            shapes.Append(Ellipse(ctx.NextId(), MX, y, ring, ring, t.Accent));
            var mark = rows[i].V3.Length > 0 ? rows[i].V3 : (i + 1).ToString();
            if (IsIconName(mark))
            {
                // 内置图标：在圆里画一张 PNG（颜色跟主题走）。比“1~2 个字”像样得多。
                var iconPx = Math.Min((int)(ring / 12700), 256);
                var png = RenderIcon(mark, Math.Max(48, iconPx), t.OnAccent);
                var inset = ring / 5;               // 图标不贴圆边
                shapes.Append(Picture(ctx.NextId(), ctx.AddImage(png),
                    MX + inset, y + inset, ring - 2 * inset, ring - 2 * inset));
            }
            else
            {
                shapes.Append(TextBox(ctx.NextId(), MX, y, ring, ring,
                    Para(mark, Scaled(1800, scale), t.OnAccent, bold: true, align: "ctr"), anchor: "ctr"));
            }

            var inner = new StringBuilder();
            if (rows[i].V1.Length > 0)
                inner.Append(Para(rows[i].V1, Scaled(1600, scale), t.Primary, bold: true, align: "l",
                    lineSpacing: (int)Math.Round(110 * scale)));
            if (rows[i].V2.Length > 0)
                inner.Append(Para(rows[i].V2, Scaled(1300, scale), t.Text, align: "l", spaceBefore: 6,
                    lineSpacing: (int)Math.Round(120 * scale)));
            shapes.Append(TextBox(ctx.NextId(), MX + textX, y + Sz(57150), textW, availH, inner.ToString(), anchor: "t"));
        }
        return shapes.ToString();
    }

    // ---- 内置图标 ----

    /// <summary>内置图标名。在彩色圆里画的几何图形，<b>不依赖任何图标字体/素材文件</b>。</summary>
    private static readonly string[] IconNames =
    {
        "check", "cross", "arrow", "star", "dot", "warn",
        "lock", "user", "chart", "clock", "gear", "bulb",
    };

    private static bool IsIconName(string s) => IconNames.Contains(s.Trim().ToLowerInvariant());

    /// <summary>
    /// 把内置图标画成 PNG（透明底 + 指定颜色）。
    ///
    /// <para>
    /// 为什么自己画：技能是**单个编译单元**，既不能携带字体/素材文件，也不能假设宿主装了某个图标字体。
    /// ImageSharp 已经为图表引入了，画几个几何图形是顺手的事；颜色还能直接跟着主题走。
    /// </para>
    /// </summary>
    private static byte[] RenderIcon(string name, int px, string color)
    {
        using var img = new Image<Rgba32>(px, px);
        var c = ImgColor.ParseHex(BareHex(color));
        var t = px * 0.11f;                       // 线宽
        var m = px * 0.26f;                       // 内边距
        var e = px - m;                           // 内边距终点
        var mid = px / 2f;
        img.Mutate(x =>
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "check":
                    x.DrawLine(c, t, new ImgPointF(m, mid), new ImgPointF(px * 0.42f, e));
                    x.DrawLine(c, t, new ImgPointF(px * 0.42f, e), new ImgPointF(e, m));
                    break;
                case "cross":
                    x.DrawLine(c, t, new ImgPointF(m, m), new ImgPointF(e, e));
                    x.DrawLine(c, t, new ImgPointF(e, m), new ImgPointF(m, e));
                    break;
                case "arrow":
                    x.DrawLine(c, t, new ImgPointF(m, mid), new ImgPointF(e, mid));
                    x.DrawLine(c, t, new ImgPointF(px * 0.62f, m), new ImgPointF(e, mid));
                    x.DrawLine(c, t, new ImgPointF(px * 0.62f, e), new ImgPointF(e, mid));
                    break;
                case "star":
                    x.Fill(c, Star(px * 0.5f, px * 0.44f));
                    break;
                case "dot":
                    x.Fill(c, new ImgEllipse(new ImgPointF(mid, mid), px * 0.22f));
                    break;
                case "warn":
                    x.Fill(c, Poly(new ImgPointF(mid, m), new ImgPointF(e, e), new ImgPointF(m, e)));
                    break;
                case "lock":
                    x.Fill(c, new ImgRect(m, px * 0.46f, e - m, px * 0.34f));
                    // 锁梁：本版本的 ImageSharp.Drawing 没有 DrawArc，用一个环代替（视觉上就是一个挂锁）
                    x.Draw(c, t, new ImgEllipse(new ImgPointF(mid, px * 0.44f), px * 0.18f));
                    break;
                case "user":
                    x.Fill(c, new ImgEllipse(new ImgPointF(mid, px * 0.35f), px * 0.16f));
                    x.Fill(c, new ImgEllipse(new ImgPointF(mid, px * 0.92f), px * 0.28f));
                    break;
                case "chart":
                    x.Fill(c, new ImgRect(m, px * 0.55f, px * 0.14f, e - px * 0.55f));
                    x.Fill(c, new ImgRect(px * 0.43f, px * 0.38f, px * 0.14f, e - px * 0.38f));
                    x.Fill(c, new ImgRect(px * 0.62f, px * 0.22f, px * 0.14f, e - px * 0.22f));
                    break;
                case "clock":
                    x.Draw(c, t, new ImgEllipse(new ImgPointF(mid, mid), px * 0.34f));
                    x.DrawLine(c, t, new ImgPointF(mid, mid), new ImgPointF(mid, px * 0.30f));
                    x.DrawLine(c, t, new ImgPointF(mid, mid), new ImgPointF(px * 0.68f, mid));
                    break;
                case "gear":
                    x.Draw(c, t, new ImgEllipse(new ImgPointF(mid, mid), px * 0.26f));
                    for (var i = 0; i < 8; i++)
                    {
                        var ang = i * Math.PI / 4.0;
                        var r0 = px * 0.30;
                        var r1 = px * 0.40;
                        x.DrawLine(c, t,
                            new ImgPointF(mid + (float)(r0 * Math.Cos(ang)), mid + (float)(r0 * Math.Sin(ang))),
                            new ImgPointF(mid + (float)(r1 * Math.Cos(ang)), mid + (float)(r1 * Math.Sin(ang))));
                    }
                    break;
                default: // bulb
                    x.Draw(c, t, new ImgEllipse(new ImgPointF(mid, px * 0.42f), px * 0.26f));
                    x.Fill(c, new ImgRect(px * 0.40f, px * 0.72f, px * 0.20f, px * 0.14f));
                    break;
            }
        });
        return ToPng(img);
    }

    /// <summary>多边形填充路径（自己围，见饼图那个坑：闭合靠 CloseFigure，不靠“路径没闭合就自动补”）。</summary>
    private static SixLabors.ImageSharp.Drawing.IPath Poly(params ImgPointF[] pts)
    {
        var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
        pb.MoveTo(pts[0]);
        for (var i = 1; i < pts.Length; i++) pb.LineTo(pts[i]);
        pb.CloseFigure();
        return pb.Build();
    }

    /// <summary>五角星（外顶点 5 个、内顶点 5 个交替）。</summary>
    private static SixLabors.ImageSharp.Drawing.IPath Star(float cx, float r)
    {
        var pts = new ImgPointF[10];
        for (var i = 0; i < 10; i++)
        {
            var rr = i % 2 == 0 ? r : r * 0.42f;
            var ang = -Math.PI / 2 + i * Math.PI / 5.0;
            pts[i] = new ImgPointF(cx + (float)(rr * Math.Cos(ang)), cx + (float)(rr * Math.Sin(ang)));
        }
        return Poly(pts);
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
    // ---- 原生图表（DrawingML ChartPart，可在 PowerPoint 里改数据）----

    /// <summary>支持原生图表的图型；其余（如 doughnut）仍走 ImageSharp 渲图。</summary>
    private static bool SupportsNativeChart(string kind) => kind is "bar" or "line" or "pie";

    /// <summary>
    /// 数据工作簿（xlsx），挂在 ChartPart 下供 PowerPoint「编辑数据」用。
    ///
    /// <para>
    /// <b>没有它图表仍能显示</b>（缓存值写在 ChartSpace 里），但“编辑数据”拿不到表格；
    /// 既然做原生图表的卖点就是“可改数据”，就必须一并嵌入。
    /// 第 1 行：分类 / 各系列名；第 2..n+1 行：分类值 / 各系列数值。
    /// </para>
    /// </summary>
    private static byte[] DataWorkbook(List<string> cats, List<(string Name, double[] Values)> series)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook, true))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new SS.Workbook();
            var ws = wb.AddNewPart<WorksheetPart>();

            var head = new SS.Row();
            head.Append(InlineCell("A1", "分类"));
            for (var s = 0; s < series.Count; s++)
                head.Append(InlineCell(ColName(s + 1) + "1",
                    series[s].Name.Length > 0 ? series[s].Name : "系列" + (s + 1)));
            var sheetData = new SS.SheetData(head);

            for (var i = 0; i < cats.Count; i++)
            {
                var row = new SS.Row();
                row.Append(InlineCell("A" + (i + 2), cats[i]));
                for (var s = 0; s < series.Count; s++)
                    row.Append(NumberCell(ColName(s + 1) + (i + 2),
                        i < series[s].Values.Length ? series[s].Values[i] : 0));
                sheetData.Append(row);
            }

            ws.Worksheet = new SS.Worksheet(sheetData);
            ws.Worksheet.Save();
            wb.Workbook.Append(new SS.Sheets(new SS.Sheet
            {
                Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "Sheet1",
            }));
            wb.Workbook.Save();
        }
        return ms.ToArray();
    }

    private static string ColName(int i) => ((char)('A' + i)).ToString();

    private static SS.Cell InlineCell(string reference, string text)
        => new()
        {
            CellReference = reference,
            DataType = SS.CellValues.InlineString,
            InlineString = new SS.InlineString(new SS.Text(text ?? "")),
        };

    private static SS.Cell NumberCell(string reference, double v)
        => new()
        {
            CellReference = reference,
            CellValue = new SS.CellValue(v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        };

    /// <summary>
    /// 构造 ChartSpace XML。
    ///
    /// <para>
    /// <b>子元素顺序是 schema 强制的</b>（比如 catAx 必须 axId→scaling→delete→axPos…），
    /// 顺序错了 PowerPoint 就会报“需要修复”，而这类错误单部件校验器不一定拦得住——
    /// 改这里请对照 ECMA-376 的 CT_* 定义，不要“看着差不多”就挪。
    /// </para>
    /// </summary>
    private static string ChartSpaceXml(string kind, string? title, List<string> cats,
        List<(string Name, double[] Values)> series, Theme t)
    {
        var sb = new StringBuilder();
        sb.Append("<c:chartSpace xmlns:c=\"").Append(NS_C).Append("\" xmlns:a=\"").Append(NS_A)
          .Append("\" xmlns:r=\"").Append(NS_R).Append("\"><c:chart>");
        if (!string.IsNullOrWhiteSpace(title))
            sb.Append("<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"zh-CN\" sz=\"1400\" b=\"0\">")
              .Append("<a:solidFill>").Append(Rgb(t.Primary)).Append("</a:solidFill>")
              .Append("<a:latin typeface=\"").Append(Xml(t.FontTitle)).Append("\"/>")
              .Append("<a:ea typeface=\"").Append(Xml(t.FontTitle)).Append("\"/></a:rPr>")
              .Append("<a:t>").Append(Xml(title!)).Append("</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=\"0\"/></c:title>");
        sb.Append("<c:autoTitleDeleted val=\"0\"/><c:plotArea><c:layout/>");

        var legend = series.Count > 1 || kind == "pie";
        switch (kind)
        {
            case "line":
                sb.Append("<c:lineChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>");
                for (var i = 0; i < series.Count; i++) sb.Append(SerXml(i, cats, series[i], line: true));
                sb.Append("<c:marker val=\"0\"/><c:axId val=\"111111111\"/><c:axId val=\"222222222\"/></c:lineChart>");
                break;
            case "pie":
                sb.Append("<c:pieChart><c:varyColors val=\"1\"/>");
                sb.Append(SerXml(0, cats, series[0], line: false));
                sb.Append("<c:firstSliceAng val=\"0\"/></c:pieChart>");
                break;
            default: // bar
                sb.Append("<c:barChart><c:barDir val=\"col\"/><c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/>");
                for (var i = 0; i < series.Count; i++) sb.Append(SerXml(i, cats, series[i], line: false));
                sb.Append("<c:gapWidth val=\"90\"/><c:axId val=\"111111111\"/><c:axId val=\"222222222\"/></c:barChart>");
                break;
        }

        if (kind != "pie")
        {
            sb.Append("<c:catAx><c:axId val=\"111111111\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling>")
              .Append("<c:delete val=\"0\"/><c:axPos val=\"b\"/><c:tickLblPos val=\"nextTo\"/><c:crossAx val=\"222222222\"/>")
              .Append("<c:crosses val=\"autoZero\"/><c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/></c:catAx>");
            sb.Append("<c:valAx><c:axId val=\"222222222\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling>")
              .Append("<c:delete val=\"0\"/><c:axPos val=\"l\"/><c:majorGridlines/>")
              .Append("<c:numFmt formatCode=\"General\" sourceLinked=\"1\"/><c:tickLblPos val=\"nextTo\"/>")
              .Append("<c:crossAx val=\"111111111\"/><c:crosses val=\"autoZero\"/><c:crossBetween val=\"between\"/></c:valAx>");
        }

        sb.Append("</c:plotArea>");
        if (legend) sb.Append("<c:legend><c:legendPos val=\"b\"/><c:overlay val=\"0\"/></c:legend>");
        sb.Append("<c:plotVisOnly val=\"1\"/><c:dispBlanksAs val=\"gap\"/></c:chart></c:chartSpace>");
        return sb.ToString();
    }

    /// <summary>一条系列（idx/order/tx/spPr/cat/val，顺序固定）。</summary>
    private static string SerXml(int idx, List<string> cats, (string Name, double[] Values) s, bool line)
    {
        var sb = new StringBuilder();
        sb.Append("<c:ser><c:idx val=\"").Append(idx).Append("\"/><c:order val=\"").Append(idx).Append("\"/>");
        sb.Append("<c:tx><c:strRef><c:f>Sheet1!$").Append(ColName(idx + 1)).Append("$1</c:f>")
          .Append("<c:strCache><c:ptCount val=\"1\"/><c:pt idx=\"0\"><c:v>").Append(Xml(s.Name)).Append("</c:v></c:pt></c:strCache></c:strRef></c:tx>");
        sb.Append("<c:spPr><a:solidFill>").Append(Rgb(Palette[idx % Palette.Length]))
          .Append("</a:solidFill><a:ln>").Append(SolidFill(Palette[idx % Palette.Length])).Append("</a:ln></c:spPr>");
        if (line) sb.Append("<c:marker><c:symbol val=\"none\"/></c:marker>");

        sb.Append("<c:cat><c:strRef><c:f>Sheet1!$A$2:$A$").Append(cats.Count + 1)
          .Append("</c:f><c:strCache><c:ptCount val=\"").Append(cats.Count).Append("\"/>");
        for (var i = 0; i < cats.Count; i++)
            sb.Append("<c:pt idx=\"").Append(i).Append("\"><c:v>").Append(Xml(cats[i])).Append("</c:v></c:pt>");
        sb.Append("</c:strCache></c:strRef></c:cat>");

        sb.Append("<c:val><c:numRef><c:f>Sheet1!$").Append(ColName(idx + 1)).Append("$2:$")
          .Append(ColName(idx + 1)).Append("$").Append(s.Values.Length + 1)
          .Append("</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val=\"")
          .Append(s.Values.Length).Append("\"/>");
        for (var i = 0; i < s.Values.Length; i++)
            sb.Append("<c:pt idx=\"").Append(i).Append("\"><c:v>")
              .Append(s.Values[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append("</c:v></c:pt>");
        sb.Append("</c:numCache></c:numRef></c:val>");
        if (line) sb.Append("<c:smooth val=\"0\"/>");
        sb.Append("</c:ser>");
        return sb.ToString();
    }

    /// <summary>幻灯片上的图表占位框（graphicFrame + c:chart r:id）。</summary>
    private static string NativeChartFrame(int id, string relId, long x, long y, long cx, long cy)
    {
        var sb = new StringBuilder();
        sb.Append("<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"").Append(id).Append("\" name=\"Chart ").Append(id).Append("\"/>")
          .Append("<p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>")
          .Append("<p:xfrm><a:off x=\"").Append(x).Append("\" y=\"").Append(y)
          .Append("\"/><a:ext cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/></p:xfrm>")
          .Append("<a:graphic><a:graphicData uri=\"").Append(NS_C).Append("\">")
          .Append("<c:chart xmlns:c=\"").Append(NS_C).Append("\" xmlns:r=\"").Append(NS_R)
          .Append("\" r:id=\"").Append(relId).Append("\"/></a:graphicData></a:graphic></p:graphicFrame>");
        return sb.ToString();
    }

    private static string ChartBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var kindRaw = (Str(el, "chartType") ?? Str(el, "type2") ?? "bar").Trim().ToLowerInvariant();
        // 原生图表可以写 chartType:"bar-native"，也可以顶层写 chartData:"native"
        var nativeBySuffix = kindRaw.EndsWith("-native", StringComparison.Ordinal);
        var kind = nativeBySuffix ? kindRaw[..^"-native".Length] : kindRaw;
        var wantNative = nativeBySuffix
            || string.Equals((Str(el, "chartData") ?? "").Trim(), "native", StringComparison.OrdinalIgnoreCase);
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

        var availHn = BodyH - (string.IsNullOrWhiteSpace(caption) ? 0 : 457200);

        // 原生图表：交给 PowerPoint 自己画，用户可在里面改数据（代价是 schema 风险，故默认不开）
        if (wantNative && SupportsNativeChart(kind))
        {
            var relId = ctx.AddChart(
                ChartSpaceXml(kind, title, categories, series, t),
                DataWorkbook(categories, series));
            _nativeCharts++;
            var native = new StringBuilder();
            native.Append(NativeChartFrame(ctx.NextId(), relId, MX, BodyY, CW, availHn));
            if (!string.IsNullOrWhiteSpace(caption))
                native.Append(TextBox(ctx.NextId(), MX, BodyY + availHn, CW, 365760,
                    Para(caption!, 1100, t.Secondary, align: "ctr")));
            return native.ToString();
        }
        if (wantNative && !SupportsNativeChart(kind))
            _nativeFallback = kind;   // 记下来，返回 JSON 里如实说明降级了

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
                paras.Append(Para(it, sz, color, align: "l", bullet: "•", spaceBefore: 10, lineSpacing: 125, marL: (int)_m.Gap));
            else
            {
                paras.Append(Para(parts.Value.Label, sz + 100, boldColor, bold: true, align: "l",
                    bullet: "•", spaceBefore: 12, lineSpacing: 120, marL: (int)_m.Gap));
                paras.Append(Para(parts.Value.Detail, sz - 100, color, align: "l", lineSpacing: 125, marL: (int)_m.Gap));
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

    private static string Rgb(string hex) => "<a:srgbClr val=\"" + BareHex(hex) + "\"/>";

    /// <summary>
    /// 去掉可选的前导 <c>#</c>。
    ///
    /// <para>
    /// 图表调色板常量带 <c>#</c>（ImageSharp 的 <c>ParseHex</c> 接受），但 DrawingML 的
    /// <c>srgbClr/@val</c> 是 xsd:hexBinary，带 <c>#</c> 就不合法。实测踩到：
    /// 原生图表的系列填充写成 <c>#4F81BD</c>，OpenXmlValidator 直接报“不是合法 hexBinary”。
    /// 在输出层统一归一化，比要求每个调用点都记得去 <c>#</c> 可靠。
    /// </para>
    /// </summary>
    private static string BareHex(string hex)
        => string.IsNullOrEmpty(hex) || hex[0] != '#' ? hex : hex.Substring(1);

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
            sb.Append("<a:prstGeom prst=\"roundRect\"><a:avLst><a:gd name=\"adj\" fmla=\"val ").Append(_m.Radius)
              .Append("\"/></a:avLst></a:prstGeom>");
        else
            sb.Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>");
        sb.Append("<a:solidFill>");
        if (alpha < 100) sb.Append("<a:srgbClr val=\"").Append(BareHex(fill)).Append("\"><a:alpha val=\"").Append(alpha * 1000).Append("\"/></a:srgbClr>");
        else sb.Append(Rgb(fill));
        sb.Append("</a:solidFill><a:ln><a:noFill/></a:ln></p:spPr>")
          .Append("<p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody></p:sp>");
        return sb.ToString();
    }

    /// <summary>椭圆（cx==cy 即正圆）：用来做时间轴的序号节点 / 图标圆。</summary>
    private static string Ellipse(int id, long x, long y, long cx, long cy, string fill)
    {
        var sb = new StringBuilder();
        sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id).Append("\" name=\"Ellipse ").Append(id).Append("\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>")
          .Append("<p:spPr><a:xfrm><a:off x=\"").Append(x).Append("\" y=\"").Append(y)
          .Append("\"/><a:ext cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm>")
          .Append("<a:prstGeom prst=\"ellipse\"><a:avLst/></a:prstGeom>")
          .Append("<a:solidFill>").Append(Rgb(fill)).Append("</a:solidFill><a:ln><a:noFill/></a:ln></p:spPr>")
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

    private static string Srgb(string hex) => "<a:srgbClr val=\"" + Xml(BareHex(hex)) + "\"/>";

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
    /// <summary>所选图表字体是否能画中文（供返回信息与排障用）。</summary>
    private static bool _fontCjk;

    private static SixLabors.Fonts.Font Family(float size)
    {
        lock (_fontLock)
        {
            if (_fontFamily is null) PickFamily();
            return _fontFamily!.Value.CreateFont(size);
        }
    }

    /// <summary>所选图表字体族名（供返回信息与排障）。</summary>
    private static string ChartFontName => _fontName;
    /// <summary>所选图表字体是否含中文字形。</summary>
    private static bool ChartFontHasCjk => _fontCjk;

    /// <summary>
    /// 挑图表字体。
    ///
    /// <para>
    /// <b>关键：不能只按“族名命中”就选定</b>。实测踩到——容器里（Linux）前几候选
    /// （Microsoft YaHei / SimHei / SimSun / Arial）都不存在，名单里第一个存在的是
    /// <c>DejaVu Sans</c>，而那是**纯拉丁字体**，于是图表里的中文标题、分类标签、
    /// 系列名全部变成缺字（乱码/空白），而英文坐标数字正常，看着很像“字体渲染错乱”。
    /// 因此命中候选后必须验字形覆盖：确认它能画出中文再采用。
    /// </para>
    /// </summary>
    private static void PickFamily()
    {
        // 1) 候选名单里第一个<b>真的含中文字形</b>的
        foreach (var name in FontCandidates)
            if (SixLabors.Fonts.SystemFonts.TryGet(name, out var f) && CanRenderCjk(f))
            {
                _fontFamily = f; _fontName = name; _fontCjk = true;
                return;
            }

        // 2) 名单都没命中，但系统里确实有中文字体（族名未知，如容器里的 Noto Sans CJK SC）
        foreach (var f in SixLabors.Fonts.SystemFonts.Collection.Families)
            if (CanRenderCjk(f))
            {
                _fontFamily = f; _fontName = f.Name; _fontCjk = true;
                return;
            }

        // 3) 没有中文字体：退回名单里第一个可用字体（能出图，中文会缺字，故记 _fontCjk=false 供上层提示）
        foreach (var name in FontCandidates)
            if (SixLabors.Fonts.SystemFonts.TryGet(name, out var f))
            {
                _fontFamily = f; _fontName = name; _fontCjk = false;
                return;
            }
        if (SixLabors.Fonts.SystemFonts.Collection.Families.Any())
        {
            var f = SixLabors.Fonts.SystemFonts.Collection.Families.First();
            _fontFamily = f; _fontName = f.Name; _fontCjk = false;
            return;
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
        public List<LegendItem> Items = new();
        public int Rows;
        public int Hidden;
    }

    /// <summary>
    /// 图例排版：按可用宽度<b>换行</b>，最多 maxRows 行；放不下的计入 Hidden。
    /// <para>
    /// 原实现是一行横向累加 x，系列一多就直接画出画布右边（典型“内容太多越界”）。
    /// </para>
    /// </summary>
    private static LegendLayout LayoutLegend(List<(string Name, double[] Values)> series, double availW, float size, int maxRows)
    {
        var layout = new LegendLayout();
        if (availW < 60) { layout.Hidden = series.Count; return layout; }
        float x = 0;
        var row = 0;
        for (var s = 0; s < series.Count; s++)
        {
            var nm = string.IsNullOrEmpty(series[s].Name) ? "系列" + (s + 1) : series[s].Name;
            var text = FitText(nm, size, Math.Min(240, availW - 44));
            if (text.Length == 0) text = "系列" + (s + 1);
            var itemW = 20 + (float)MeasureText(text, size) + 24;
            if (x > 0 && x + itemW > availW) { row++; x = 0; }
            if (row >= maxRows) { layout.Hidden = series.Count - s; break; }
            layout.Items.Add(new LegendItem { Text = text, X = x, Y = row * 24f, ColorIndex = s });
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
            x.Fill(col, new ImgRect(left + it.X, top + it.Y, 15, 15));
            x.DrawText(it.Text, font, fg, new ImgPointF(left + it.X + 20, top + it.Y - 3));
        }
        if (legend.Hidden > 0 && legend.Rows > 0)
        {
            var last = legend.Items[legend.Items.Count - 1];
            var hint = "…等 " + (legend.Items.Count + legend.Hidden) + " 项";
            var hx = left + last.X + 20 + (float)MeasureText(last.Text, 14f) + 22;
            var hw = (float)MeasureText(hint, 13f);
            if (hx + hw <= left + availW) x.DrawText(hint, Family(13f), muted, new ImgPointF(hx, top + last.Y));
        }
    }

    private static byte[] RenderBar(int w, int h, string? title, string? yLabel, List<string> cats,
        List<(string Name, double[] Values)> series, bool dark, Theme t)
    {
        using var img = new Image<Rgba32>(w, h);
        var fg = dark ? ImgColor.White : ImgColor.Black;
        var muted = dark ? ImgColor.FromRgba(170, 178, 190, 255) : ImgColor.FromRgba(90, 90, 90, 255);
        var maxV = Math.Max(0.0001, series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max());
        const int padR = 36, padB = 76;

        // 刻度文案先算出来：左边距按“最宽的刻度”留，否则大数字会压到绘图区（越界）
        const int ticks = 4;
        var tickTexts = new string[ticks + 1];
        for (var i = 0; i <= ticks; i++) tickTexts[i] = (maxV * i / ticks).ToString("0.##");
        var tickW = tickTexts.Max(s => MeasureText(s, 13f));
        var padL = (int)Math.Min(200, Math.Max(72, tickW + 30));

        // 图例先排版（含换行），据此预留顶部高度：既不把绘图区挤没，也不会自身越界
        var legend = LayoutLegend(series, w - padL - padR, 14f, 3);
        var padT = (title is null ? 40 : 74) + legend.Rows * 24;

        img.Mutate(x =>
        {
            x.Fill(dark ? ImgColor.FromRgba(13, 17, 23, 255) : ImgColor.White);
            if (!string.IsNullOrWhiteSpace(title))
                x.DrawText(FitText(title, 26f, w - padL - padR), Family(26f), fg, new ImgPointF(padL, 24));

            for (var i = 0; i <= ticks; i++)
            {
                var y = h - padB - (float)((h - padT - padB) * i / (double)ticks);
                x.DrawLine(muted, 1f, new ImgPointF(padL, y), new ImgPointF(w - padR, y));
                // 右对齐到轴线左侧，不再固定 x=8
                x.DrawText(tickTexts[i], Family(13f), muted,
                    new ImgPointF((float)(padL - 10 - MeasureText(tickTexts[i], 13f)), y - 9));
            }
            if (!string.IsNullOrWhiteSpace(yLabel))
                x.DrawText(FitText(yLabel, 13f, w - padL - padR), Family(13f), muted,
                    new ImgPointF(padL, Math.Max(6, padT - 22)));

            if (legend.Rows > 0) DrawLegend(x, legend, padL, padT - legend.Rows * 24f, w - padL - padR, Family(14f), fg, muted);

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
                // 分类标签：按槽宽裁剪（而不是按字符数），并在分类太多时隔位显示，避免叠成一团
                var stride = Math.Max(1, (int)Math.Ceiling(52.0 / slot));
                if (c % stride != 0) continue;
                var lab = FitText(cats[c], 14f, slot * stride * 0.96);
                if (lab.Length == 0) continue;
                var tw = (float)MeasureText(lab, 14f);
                var lx = ClampX((float)(padL + slot * c + slot / 2 - tw / 2), padL, (float)(w - padR - tw));
                x.DrawText(lab, Family(14f), fg, new ImgPointF(lx, h - padB + 10));
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
        var maxV = Math.Max(0.0001, series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max());
        const int padR = 36, padB = 76;

        const int ticks = 4;
        var tickTexts = new string[ticks + 1];
        for (var i = 0; i <= ticks; i++) tickTexts[i] = (maxV * i / ticks).ToString("0.##");
        var tickW = tickTexts.Max(s => MeasureText(s, 13f));
        var padL = (int)Math.Min(200, Math.Max(72, tickW + 30));
        var legend = LayoutLegend(series, w - padL - padR, 14f, 3);
        var padT = (title is null ? 40 : 74) + legend.Rows * 24;

        img.Mutate(x =>
        {
            x.Fill(dark ? ImgColor.FromRgba(13, 17, 23, 255) : ImgColor.White);
            if (!string.IsNullOrWhiteSpace(title))
                x.DrawText(FitText(title, 26f, w - padL - padR), Family(26f), fg, new ImgPointF(padL, 24));

            for (var i = 0; i <= ticks; i++)
            {
                var y = h - padB - (float)((h - padT - padB) * i / (double)ticks);
                x.DrawLine(muted, 1f, new ImgPointF(padL, y), new ImgPointF(w - padR, y));
                x.DrawText(tickTexts[i], Family(13f), muted,
                    new ImgPointF((float)(padL - 10 - MeasureText(tickTexts[i], 13f)), y - 9));
            }
            if (!string.IsNullOrWhiteSpace(yLabel))
                x.DrawText(FitText(yLabel, 13f, w - padL - padR), Family(13f), muted,
                    new ImgPointF(padL, Math.Max(6, padT - 22)));
            x.DrawLine(fg, 1.6f, new ImgPointF(padL, padT), new ImgPointF(padL, h - padB));
            x.DrawLine(fg, 1.6f, new ImgPointF(padL, h - padB), new ImgPointF(w - padR, h - padB));
            if (legend.Rows > 0) DrawLegend(x, legend, padL, padT - legend.Rows * 24f, w - padL - padR, Family(14f), fg, muted);

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
                var stride = Math.Max(1, (int)Math.Ceiling(52.0 / slot));
                if (c % stride != 0) continue;
                var lab = FitText(cats[c], 14f, slot * stride * 0.96);
                if (lab.Length == 0) continue;
                var tw = (float)MeasureText(lab, 14f);
                var lx = ClampX((float)(padL + slot * c + slot / 2 - tw / 2), padL, (float)(w - padR - tw));
                x.DrawText(lab, Family(14f), fg, new ImgPointF(lx, h - padB + 10));
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
        var muted = dark ? ImgColor.FromRgba(170, 178, 190, 255) : ImgColor.FromRgba(90, 90, 90, 255);
        img.Mutate(x =>
        {
            x.Fill(bg);
            if (!string.IsNullOrWhiteSpace(title))
                x.DrawText(FitText(title, 26f, w - 80), Family(26f), fg, new ImgPointF(40, 22));

            var positives = values.Select(v => Math.Max(0, v)).ToArray();
            var total = positives.Sum();
            if (total <= 0)
            {
                x.DrawText(FitText("（无正数数据）", 20f, w - 80), Family(20f), fg, new ImgPointF(60, h / 2f));
                return;
            }

            var size = Math.Max(40, Math.Min(h - 150, w / 2 - 60));
            var cx = 70 + size / 2f;
            var cy = h / 2f + 22;
            var radius = size / 2f;
            var start = -90f; // 从 12 点开始，顺时针
            for (var i = 0; i < positives.Length; i++)
            {
                if (positives[i] <= 0) continue;
                var sweep = (float)(360.0 * positives[i] / total);
                // 扇形必须由「圆心 → 沿弧走一圈 → 回圆心」围成。
                // 【几何易错，勿改】PathBuilder.AddArc 只是往当前图形里追加一段弧：它既不先移到圆心，
                // 也不会自动补上两条半径。只写 AddArc + CloseFigure 得到的是「弧 + 弦」围成的弓形，
                // 面积几乎为 0——实测 40 项饼图只剩贴外缘的一圈发丝线，圆盘内部整片留白（看着像没画）。
                // 这里用多边形逼近（每 2° 一段；320px 半径下弦高约 0.05px，肉眼不可见）显式围出扇形。
                var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / 2f));
                var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
                pb.MoveTo(new ImgPointF(cx, cy));
                for (var s = 0; s <= steps; s++)
                {
                    var ang = (start + sweep * s / steps) * (float)Math.PI / 180f;
                    pb.LineTo(new ImgPointF(cx + radius * (float)Math.Cos(ang), cy + radius * (float)Math.Sin(ang)));
                }
                pb.CloseFigure();
                x.Fill(ImgColor.ParseHex(Palette[i % Palette.Length]), pb.Build());
                start += sweep;
            }
            if (doughnut)
                x.Fill(bg, new ImgEllipse(new ImgPointF(cx, cy), radius * 0.55f));

            // 图例：按可用宽度裁剪文案、按可用高度限制行数；放不下的不再硬画到画布外
            var legendX = w / 2f + 40;
            var availW = w - legendX - 24;
            var rowH = 30f;
            var maxRows = Math.Max(1, (int)((h - 110 - 24) / rowH));
            var n = Math.Min(cats.Count, positives.Length);
            var shown = Math.Min(n, maxRows);
            var labSize = shown > 10 ? 13f : 15f;
            for (var i = 0; i < shown; i++)
            {
                var pct = positives[i] / total * 100;
                var ly = 100f + i * rowH;
                x.Fill(ImgColor.ParseHex(Palette[i % Palette.Length]), new ImgRect(legendX, ly, 16, 16));
                var label = cats[i] + "  " + positives[i].ToString("0.##") + "（" + pct.ToString("0.#") + "%）";
                x.DrawText(FitText(label, labSize, availW - 28), Family(labSize), fg, new ImgPointF(legendX + 24, ly - 3));
            }
            if (shown < n)
            {
                var more = "…等 " + n + " 项";
                var ly = 100f + shown * rowH;
                if (ly + 18 < h)
                    x.DrawText(FitText(more, 13f, availW - 28), Family(13f), muted, new ImgPointF(legendX + 24, ly));
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

    /// <summary>回显实际生效的配色（含“底色上的字”两个派生色）。</summary>
    private static string PaletteJson(Theme? t)
    {
        if (t is null) return "null";
        return "{\"primary\":" + Js(t.Primary)
             + ",\"secondary\":" + Js(t.Secondary)
             + ",\"accent\":" + Js(t.Accent)
             + ",\"light\":" + Js(t.Light)
             + ",\"bg\":" + Js(t.Bg)
             + ",\"text\":" + Js(t.Text)
             + ",\"onAccent\":" + Js(t.OnAccent)
             + ",\"onPrimary\":" + Js(t.OnPrimary) + "}";
    }

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
