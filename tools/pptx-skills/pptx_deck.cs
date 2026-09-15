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
//     "fontPair": "georgia-calibri",                       // 可选，命名字体配对（只换拉丁字面）
//     "fontCjk": "微软雅黑",                                // 可选，东亚字体（汉字走它）
//     "titleRule": true,                                   // 可选，加上“标题下强调线”（默认不加）
//     "action": "read", "path": "…pptx",                  // 可选：只读取既有 pptx 的文本，不生成文件
//     "action": "qa", "path": "…pptx",                    // 可选：只自检（占位符/空页/只有标题/越界）
//     "action": "edit", "path": "…", "outputPath": "…",   // 可选：改既有 pptx 的结构（见下方 OPS）
//       "ops": [ {"op":"delete","slides":[3]},
//                {"op":"reorder","order":[1,3,2]},
//                {"op":"duplicate","slide":2,"count":2},
//                {"op":"replaceText","slides":[1,2],"map":{"旧":"新"}},
//                {"op":"append","slides":[ {"type":"content",…} ]} ]
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
//   slides 每项是 { "type": "…", … }，type 取值（大多可用 "variant" 换版式）：
//     cover    封面      title, subtitle, author, date
//                        variant: left(默认) | center | image(背景图+蒙层) | split(左文右图)
//     toc      目录      title, items:[ "…" ], variant: list(默认) | grid | sidebar
//     section  章节分隔  title, subtitle, variant: number(默认) | bar | full
//     content  要点页    title, bullets:[ "…" ], note
//     twoCol   两栏      title, left:{heading,bullets:[…]}, right:{heading,bullets:[…]}
//     table    表格      title, headers:[…], rows:[[…]]（过长自动分页，不丢行）
//     kpi      指标卡    title, items:[ {value,label} ]
//     stats    大数字    title, items:[ {value,label} ], cols
//     progress 进度/仪表 title, items:[ {label,value} ], max(默认 100),
//                        variant: bar(默认，横向进度条) | ring(环形仪表)
//     grid     网格卡    title, items:[ {title,text} ], cols:2|3
//     timeline 时间轴    title, items:[ {title,detail} ]（最多 6 步）
//     iconRows 图标行    title, items:[ {icon,title,text} ]（最多 6 行）
//     quote    引言      text, cite
//     image    配图      title, path, caption
//                        variant: full(默认) | left(图左文右) | right(文左图右) |
//                                 bleed(半出血+叠字，自包含标题) | gallery(images:[{path,caption}] 2~4 张)
//                        left/right/bleed 可配 heading/bullets 写文字侧
//     chart    图表      title, chartType:"bar|line|pie|doughnut|scatter|radar", categories:[…],
//                        series:[ {name,values:[…]} ], yLabel, xLabel, caption
//                        散点图：series:[ {name,points:[[x,y],…]} ]
//                        chartType 加 "-native" 后缀 → 生成原生可编辑图表（DrawingML ChartPart）
//                        支持 bar/line/pie（其余会自动降级为图片并在返回里说明）
//     summary  小结      title, bullets:[…]
//                        variant: list(默认) | cta(items:[行动项], contact) | split(bullets+actions+contact)
//     end      结束页    title, subtitle
//   content 还可用 "layout":"timeline|grid|stats|iconRows|progress" 直接指定子类型。
//   任何页都可带 "notes"（备注文字），写入演讲者备注。
//
//   【出稿后自检】生成/编辑完会自动跑一遍 QA（占位符、空页、只有标题、形状越界），
//   结果在返回 JSON 的 qa 字段；降级行为（如图片缺失改用占位块）在 warnings 里。
//   两者都是“不静默降级”的产物：调用方应看它们，别直接把有问题的稿子交出去。
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
    /// <summary>本次请求是否要画“标题下强调线”（默认 false，见 SlideTitle 的说明）。</summary>
    [ThreadStatic] private static bool _titleRule;
    /// <summary>本次生成的降级/提示信息（如图片缺失改成色块）。不静默降级，随返回 JSON 报出。</summary>
    [ThreadStatic] private static List<string>? _warnings;

    private static void Warn(string message)
    {
        (_warnings ??= new List<string>()).Add(message);
    }

    /// <summary>
    /// 页型变体：优先读 <c>variant</c>，其次读 <c>layout</c>（content 页历史上用 layout 指定子类型），
    /// 都没有则用该页型的默认变体。
    /// </summary>
    private static string VariantOf(JsonElement el, string fallback)
    {
        var v = (Str(el, "variant") ?? Str(el, "layout") ?? "").Trim().ToLowerInvariant();
        return v.Length == 0 ? fallback : v;
    }
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
        /// <summary>东亚字体（<c>a:ea</c>）：汉字/CJK 符号走它。与拉丁字面分开，才能做“标题 Georgia + 正文 Calibri + 中文微软雅黑”这类配对。</summary>
        public string FontCjk = "微软雅黑";
        /// <summary>生效的命名字体配对（回显给调用方/排障）；非配对时为 null。</summary>
        public string? FontPair;
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
        // 命名字体配对（参考 design-system.md 的 Font Pairings）：只换拉丁字母的字面，
        // 中文仍走 FontCjk——否则拿 Georgia 去排汉字会整段掉到 fallback（甚至缺字）。
        if (Str(root, "fontPair") is { Length: > 0 } pairName)
        {
            var pair = FontPair(pairName);
            if (pair is { } p)
            {
                t.FontTitle = p.Title;
                t.FontBody = p.Body;
                t.FontPair = p.Name;
            }
        }
        if (Str(root, "fontTitle") is { Length: > 0 } ft) { t.FontTitle = ft.Trim(); t.FontPair = null; }
        if (Str(root, "fontBody") is { Length: > 0 } fb) { t.FontBody = fb.Trim(); t.FontPair = null; }
        // 显式给了中文字体就听它的；否则认“看起来就是中文字体”的入参，再退到默认。
        // 注意取值顺序：先看标题字体，命中就不再看正文字体——
        // 否则 fontTitle:"宋体" 会被默认的正文字体（微软雅黑）反手盖掉（实测踩到）。
        if (Str(root, "fontCjk") is { Length: > 0 } fc) t.FontCjk = fc.Trim();
        else if (LooksCjkFont(t.FontTitle)) t.FontCjk = t.FontTitle;
        else if (LooksCjkFont(t.FontBody)) t.FontCjk = t.FontBody;
        return t;
    }

    /// <summary>
    /// 命名字体配对（移植自 MiniMax pptx-generator 的 design-system.md「Font Pairings」）。
    /// 只给“标题字体 + 正文字体”两个拉丁字面；中文字面统一走 <see cref="Theme.FontCjk"/>。
    ///
    /// <para>
    /// 设计文档明确要求“别一路 Arial 到底”——标题选有性格的字面、正文配干净的字面。
    /// 但服务器（Linux 容器）通常没有 Georgia / Calibri 这些字体，因此：
    /// 这些只影响 PPT 里写入的<b>字体名</b>，在装了字体的 PowerPoint/Windows 上才看得到差异；
    /// 图表（服务端渲成 PNG）仍然只用容器里真实存在的字体。
    /// </para>
    /// </summary>
    private static (string Name, string Title, string Body)? FontPair(string name)
    {
        var key = (name ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        return key switch
        {
            "yahei" or "default" or "none" => ("yahei", "微软雅黑", "微软雅黑"),
            "georgia-calibri" => ("georgia-calibri", "Georgia", "Calibri"),
            "cambria-calibri" => ("cambria-calibri", "Cambria", "Calibri"),
            "calibri-light" or "calibri-calibrilight" => ("calibri-light", "Calibri", "Calibri Light"),
            "trebuchet-calibri" => ("trebuchet-calibri", "Trebuchet MS", "Calibri"),
            "arial-black-arial" => ("arial-black-arial", "Arial Black", "Arial"),
            "impact-arial" => ("impact-arial", "Impact", "Arial"),
            "palatino-garamond" => ("palatino-garamond", "Palatino Linotype", "Garamond"),
            "consolas-calibri" => ("consolas-calibri", "Consolas", "Calibri"),
            _ => null,
        };
    }

    /// <summary>
    /// 这个字体名是否“本身就是中文字体”。用于自动把 <c>a:ea</c>（东亚字体）指向它——
    /// 否则用户传 <c>fontTitle:"宋体"</c> 时，中文会被 FontCjk 的默认值（微软雅黑）盖掉，
    /// 属于把用户的显式选择弄丢。
    /// </summary>
    private static bool LooksCjkFont(string face)
    {
        var f = (face ?? "").Trim();
        if (f.Length == 0) return false;
        foreach (var mark in new[] { "宋", "黑", "楷", "隶", "雅黑", "明体", "等线", "圆" })
            if (f.Contains(mark, StringComparison.Ordinal)) return true;
        var lower = f.ToLowerInvariant();
        foreach (var mark in new[] { "yahei", "simsun", "simhei", "kaiti", "fangsong", "dengxian",
            "microsoft jhenghei", "mingliu", "meiryo", "yu gothic", "ms gothic", "noto sans cjk",
            "noto serif cjk", "noto sans sc", "noto serif sc", "source han", "pingfang" })
            if (lower.Contains(mark, StringComparison.Ordinal)) return true;
        return false;
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
            // 自检模式：只检查既有 pptx（不生成）
            var qaPath = ExtractActionPath(input, "qa");
            if (qaPath is not null) return QaDeck(qaPath);
            // 编辑模式：改既有 pptx 的结构（删页/复制/重排/替文字/追加），不重新生成
            if (ExtractActionPath(input, "edit") is { } editPath)
            {
                using var editDoc = JsonDocument.Parse(ExtractJson(input));
                var er = editDoc.RootElement;
                _warnings = new List<string>();
                return EditDeck(er, editPath, Str(er, "outputPath"));
            }

            var built = Build(input ?? "");
            // 出稿后立刻自检（占位符 / 空页 / 只有标题 / 越界）。
            // 这一步以前不存在——“模型声称写完了”与“文件里真有内容”是两件事。
            var qaJson = "null";
            try { qaJson = QaDeck(built.Path, built.Types); } catch { /* 自检失败不影响交付 */ }
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
                // 字体：回显实际写入的标题/正文/东亚字面，配了字体配对时一并报出
                + ",\"fonts\":{\"title\":" + Js(_currentTheme?.FontTitle ?? "")
                + ",\"body\":" + Js(_currentTheme?.FontBody ?? "")
                + ",\"cjk\":" + Js(_currentTheme?.FontCjk ?? "")
                + ",\"pair\":" + (_currentTheme?.FontPair is null ? "null" : Js(_currentTheme!.FontPair!)) + "}"
                // 降级/提示（如图片缺失改用色块）：不静默降级，调用方/用户能看见
                + ",\"warnings\":[" + string.Join(",", (_warnings ?? []).Select(Js)) + "]"
                + ",\"qa\":" + qaJson
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
    private static string? ExtractReadPath(string input) => ExtractActionPath(input, "read");

    /// <summary>若 <c>action</c> 等于给定值，返回其 <c>path</c>（或 <c>template</c>）；否则 null。</summary>
    private static string? ExtractActionPath(string input, string action)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(input));
            var root = doc.RootElement;
            if (!string.Equals((Str(root, "action") ?? "").Trim(), action, StringComparison.OrdinalIgnoreCase))
                return null;
            return Str(root, "path") ?? Str(root, "template") ?? "";
        }
        catch { return null; }
    }

    /// <summary>
    /// 出稿后的自检（QA）：验证“拿到的那份 pptx”本身没问题，而不只是“我们写好了 XML”。
    ///
    /// <para>
    /// 设计规范（pitfalls.md 的 QA Process）把「抽取文本 → 列问题 → 修 → 复验」定为必需步骤，
    /// 但以往这完全靠模型自觉——模型不自查，用户就会拿到“只有标题”“还留着占位符”的稿子。
    /// 这里把能机器判定的部分做成代码兜底：
    /// </para>
    /// <list type="number">
    /// <item>占位符/未填内容（xxxx / lorem / TODO / 占位 / 待补充…）；</item>
    /// <item>空页（除页码外没有任何文字）；</item>
    /// <item>只有标题页（内容页里只剩标题+页码，正文漏了）；</item>
    /// <item>越界（形状/文字框画到画布外）。</item>
    /// </list>
    /// </summary>
    private static string QaDeck(string? path, IReadOnlyList<string>? pageTypes = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return "{\"ok\":false,\"action\":\"qa\",\"message\":" + Js("找不到文件：" + (path ?? "（未提供 path）")) + "}";
        try
        {
            using var doc = PresentationDocument.Open(path, false);
            var presPart = doc.PresentationPart;
            if (presPart?.Presentation is null)
                return "{\"ok\":false,\"action\":\"qa\",\"message\":" + Js("不是有效的 .pptx（缺少 presentation.xml）") + "}";

            var issues = new StringBuilder();
            var issueCount = 0;
            var slideNo = 0;
            var emptyPages = 0;
            foreach (var id in presPart.Presentation.SlideIdList?.Elements<P.SlideId>() ?? [])
            {
                var relId = id.RelationshipId?.Value;
                if (relId is null || presPart.GetPartById(relId) is not SlidePart sp) continue;
                slideNo++;
                var type = pageTypes is not null && slideNo - 1 < pageTypes.Count ? pageTypes[slideNo - 1] : null;
                var texts = sp.Slide.Descendants<A.Text>()
                    .Select(x => (x.Text ?? "").Trim()).Where(s => s.Length > 0).ToList();

                void Issue(string kind, string detail)
                {
                    issueCount++;
                    if (issues.Length > 0) issues.Append(',');
                    issues.Append("{\"slide\":").Append(slideNo).Append(",\"kind\":").Append(Js(kind))
                        .Append(",\"detail\":").Append(Js(detail)).Append('}');
                }

                foreach (var tx in texts)
                    if (PlaceholderHit(tx) is { } why) Issue("placeholder", why + "：“" + Trim60(tx) + "”");

                // 自动填充的“空状态”文案（如要点为空时的“（本页暂无要点）”）说明模型没给内容，
                // 虽然它让文件“看上去有字”，但对用户等同于空白：单独报出来。
                foreach (var tx in texts)
                    if (tx.Contains("本页暂无", StringComparison.Ordinal) || tx.Contains("暂无内容", StringComparison.Ordinal))
                        Issue("emptyBody", "正文是自动填充的空状态文案，实际没有内容：“" + Trim60(tx) + "”");

                // 页码徽标形如 “01”/“12”，不算内容
                var meaningful = texts.Where(x => !(x.Length == 2 && x.All(char.IsDigit))).ToList();
                // 有图/图表/原图表的页，内容本就在图片里（图上文字抽不出来）——不能当“只有标题”。
                // 实测踩到：散点图/雷达图页被误报 titleOnly。
                var hasVisual = sp.Slide.Descendants<P.Picture>().Any()
                    || sp.Slide.Descendants<P.GraphicFrame>().Any();
                if (meaningful.Count == 0)
                {
                    emptyPages++;
                    Issue("empty", "该页除页码外没有任何文字");
                }
                // 只有在<b>知道页型</b>时才判“只有标题”：外部文件没有页型信息，
                // 封面/结束页天然只有一句话，误报会淹没真问题。
                else if (meaningful.Count == 1 && type is not null && IsBodyPage(type) && !hasVisual)
                {
                    Issue("titleOnly", "内容页只找到标题、没有正文：“" + Trim60(meaningful[0]) + "”");
                }

                foreach (var child in sp.Slide.CommonSlideData?.ShapeTree?.Elements() ?? [])
                {
                    var xfrm = child.Descendants<A.Transform2D>().FirstOrDefault();
                    if (xfrm is null) continue;
                    var ox = xfrm.Offset?.X?.Value ?? 0;
                    var oy = xfrm.Offset?.Y?.Value ?? 0;
                    var cx = xfrm.Extents?.Cx?.Value ?? 0;
                    var cy = xfrm.Extents?.Cy?.Value ?? 0;
                    if (cx <= 0 || cy <= 0) continue;
                    if (ox < 0 || oy < 0 || ox + cx > W || oy + cy > H)
                        Issue("overflow", $"形状画到画布外：x={ox} y={oy} w={cx} h={cy}（画布 {W}×{H}）");
                }
            }

            return "{\"ok\":true,\"action\":\"qa\",\"scene\":" + Js(SceneName)
                + ",\"source\":" + Js(path) + ",\"slides\":" + slideNo
                + ",\"emptySlides\":" + emptyPages
                + ",\"issueCount\":" + issueCount
                + ",\"issues\":[" + issues + "]"
                + ",\"message\":" + Js(issueCount == 0
                    ? "自检通过：未发现占位符 / 空页 / 越界（共 " + slideNo + " 页）"
                    : "自检发现 " + issueCount + " 个问题（共 " + slideNo + " 页），详见 issues") + "}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"action\":\"qa\",\"scene\":" + Js(SceneName)
                + ",\"message\":" + Js("自检失败：" + ex.GetType().Name + "：" + ex.Message) + "}";
        }
    }

    /// <summary>这页是不是“本该有正文”的页型（封面/分隔/结束/引言/整图这种只有一块文字的页不算）。</summary>
    private static bool IsBodyPage(string? type)
        => type is null || type is "content" or "twocol" or "table" or "kpi" or "stats" or "grid"
            or "cards" or "timeline" or "iconrows" or "chart" or "summary" or "toc" or "progress";

    /// <summary>占位符/未填内容的检出。命中返回原因，未命中返回 null。</summary>
    private static string? PlaceholderHit(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        var lower = t.ToLowerInvariant();
        foreach (var w in new[] { "lorem", "ipsum", "placeholder", "click to add", "xxxtitle", "yourtext" })
            if (lower.Contains(w, StringComparison.Ordinal)) return "疑似占位符（" + w + "）";
        foreach (var w in new[] { "占位", "待补充", "待填", "示例文本", "此处输入", "请填写" })
            if (t.Contains(w, StringComparison.Ordinal)) return "疑似占位符（" + w + "）";
        if (lower.Contains("todo", StringComparison.Ordinal) || lower.Contains("fixme", StringComparison.Ordinal))
            return "疑似未完成标记";
        // “xxxx” 或 “XXX”——但排除正常的罗马数字/型号（长度<=2 不算）
        if (t.Length >= 3 && t.All(c => c is 'x' or 'X' or '×')) return "疑似占位符（xxx）";
        return null;
    }

    private static string Trim60(string s) => s.Length <= 60 ? s : s.Substring(0, 60) + "…";

    // ===== 原地编辑既有 pptx（action:"edit"）=====

    /// <summary>
    /// 改既有 pptx 的<b>结构</b>：删页 / 复制页 / 重排 / 替文字 / 追加新页。
    ///
    /// <para>
    /// 以前只能“读文本”与“套模板重出一份”，用户拿着现成的稿子说“把第 5 页删了、第 2 页换到最前面”
    /// 是做不到的。这里按参考实现（editing.md 的 XML 工作流）把常用结构操作补齐。
    /// </para>
    ///
    /// <para>
    /// 铁律：<b>绝不改原件</b>——先把 <c>path</c> 复制到 <c>outputPath</c> 再在副本上动刀。
    /// 操作的页号一律是 1 起算的<b>当前顺序</b>，逐个 op 顺序执行。
    /// </para>
    /// </summary>
    private static string EditDeck(JsonElement root, string path, string? outPath)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return "{\"ok\":false,\"action\":\"edit\",\"message\":" + Js("找不到文件：" + (path ?? "（未提供 path）")) + "}";
        if (string.IsNullOrWhiteSpace(outPath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            outPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "_edited.pptx");
        }
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(outPath!), StringComparison.OrdinalIgnoreCase))
            return "{\"ok\":false,\"action\":\"edit\",\"message\":" + Js("输出路径与源文件相同，会覆盖原件；请指定不同的 outputPath。") + "}";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath!))!);
            File.Copy(path, outPath!, true);

            var applied = new List<string>();
            using (var doc = PresentationDocument.Open(outPath!, true))
            {
                var presPart = doc.PresentationPart
                    ?? throw new InvalidOperationException("不是有效的 .pptx（缺少 presentation.xml）。");
                _currentTheme = ResolveTheme(root);
                _currentMetrics = MetricsFor(Str(root, "style") ?? "soft");

                if (!root.TryGetProperty("ops", out var opsEl) || opsEl.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("缺少 ops：请给出要做的操作数组，如 [{\"op\":\"delete\",\"slides\":[3]}]。");

                foreach (var op in opsEl.EnumerateArray())
                {
                    var kind = (Str(op, "op") ?? "").Trim().ToLowerInvariant();
                    switch (kind)
                    {
                        case "delete":
                            applied.Add(DeleteSlides(presPart, IntList(op, "slides")));
                            break;
                        case "reorder":
                            applied.Add(ReorderSlides(presPart, IntList(op, "order")));
                            break;
                        case "duplicate":
                            applied.Add(DuplicateSlide(presPart, IntOf(op, "slide", 1),
                                Math.Max(1, IntOf(op, "count", 1))));
                            break;
                        case "replacetext":
                            applied.Add(ReplaceSlideText(presPart, op));
                            break;
                        case "append":
                            applied.Add(AppendSlides(presPart, op));
                            break;
                        default:
                            throw new InvalidOperationException("不支持的 op：" + kind
                                + "（可用：delete / reorder / duplicate / replaceText / append）");
                    }
                }
                presPart.Presentation.Save();
            }

            var qaJson = "null";
            try { qaJson = QaDeck(outPath!); } catch { /* 自检失败不影响交付 */ }

            var produce = "";
            try
            {
                var fi = new FileInfo(outPath!);
                produce = ",\"produce_file\":{\"path\":" + Js(fi.FullName) + ",\"name\":" + Js(fi.Name)
                    + ",\"bytes\":" + fi.Length + "}";
            }
            catch { /* 标记失败不影响主返回 */ }

            return "{\"ok\":true,\"action\":\"edit\",\"scene\":" + Js(SceneName)
                + ",\"source\":" + Js(path) + ",\"path\":" + Js(outPath)
                + ",\"applied\":[" + string.Join(",", applied.Select(Js)) + "]"
                + ",\"warnings\":[" + string.Join(",", (_warnings ?? []).Select(Js)) + "]"
                + ",\"qa\":" + qaJson + produce
                + ",\"message\":" + Js("已编辑：" + outPath + "（原文件未动）") + "}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"action\":\"edit\",\"scene\":" + Js(SceneName)
                + ",\"message\":" + Js("编辑失败：" + ex.GetType().Name + "：" + ex.Message) + "}";
        }
    }

    /// <summary>当前顺序的幻灯片（1 起算），返回 (SlideId, SlidePart) 列表。</summary>
    private static List<(P.SlideId Id, SlidePart Part)> OrderedSlides(PresentationPart presPart)
    {
        var list = new List<(P.SlideId, SlidePart)>();
        foreach (var id in presPart.Presentation.SlideIdList?.Elements<P.SlideId>() ?? [])
        {
            var relId = id.RelationshipId?.Value;
            if (relId is null || presPart.GetPartById(relId) is not SlidePart sp) continue;
            list.Add((id, sp));
        }
        return list;
    }

    private static List<int> IntList(JsonElement o, string name)
    {
        var result = new List<int>();
        if (o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var it in v.EnumerateArray())
            {
                if (it.ValueKind == JsonValueKind.Number && it.TryGetInt32(out var i)) result.Add(i);
                else if (it.ValueKind == JsonValueKind.String && int.TryParse(it.GetString(), out var s)) result.Add(s);
            }
        return result;
    }

    private static string DeleteSlides(PresentationPart presPart, List<int> slides)
    {
        var ordered = OrderedSlides(presPart);
        if (slides.Count == 0) throw new InvalidOperationException("delete 缺少 slides（1 起算的页号数组）。");
        var left = ordered.Count - slides.Distinct().Count();
        if (left < 1) throw new InvalidOperationException("不能删完整份演文稿（至少保留一页）。");
        // 从后往前删，避免前面的删除把后面的页号错位
        foreach (var no in slides.Distinct().OrderByDescending(x => x))
        {
            if (no < 1 || no > ordered.Count)
                throw new InvalidOperationException($"页号越界：{no}（当前共 {ordered.Count} 页）。");
            var (id, part) = ordered[no - 1];
            id.Remove();
            presPart.DeletePart(part);   // 连同 slideN.xml 一起删，不只解引用
        }
        return $"删除 {slides.Distinct().Count()} 页（共 {ordered.Count} 页 → {left} 页）";
    }

    private static string ReorderSlides(PresentationPart presPart, List<int> order)
    {
        var ordered = OrderedSlides(presPart);
        if (order.Count != ordered.Count)
            throw new InvalidOperationException($"reorder 需要给出全部页的新顺序（当前 {ordered.Count} 页，收到 {order.Count} 个）。");
        if (order.Distinct().Count() != order.Count || order.Any(x => x < 1 || x > ordered.Count))
            throw new InvalidOperationException("reorder 的 order 必须是 1..N 的一个排列。");
        var list = presPart.Presentation.SlideIdList!;
        foreach (var id in ordered.Select(o => o.Id)) id.Remove();
        foreach (var no in order) list.Append(ordered[no - 1].Id);
        return "重排为 [" + string.Join(",", order) + "]";
    }

    /// <summary>
    /// 复制一页（连同它引用的图片一起克隆，并重写关系 id）。
    ///
    /// <para>
    /// <b>只支持图片类关系</b>：图表页复制需要一并克隆 ChartPart 与它嵌入的数据工作簿、
    /// 并重写图表内部的 r:id——很容易产出“需要修复”的文件，因此宁可明确报错，也不静默破坏。
    /// 备注页不跟着复制（会报 warning）。
    /// </para>
    /// </summary>
    private static string DuplicateSlide(PresentationPart presPart, int slideNo, int count)
    {
        var ordered = OrderedSlides(presPart);
        if (slideNo < 1 || slideNo > ordered.Count)
            throw new InvalidOperationException($"页号越界：{slideNo}（当前共 {ordered.Count} 页）。");
        var source = ordered[slideNo - 1].Part;
        foreach (var child in source.Parts)
        {
            if (child.OpenXmlPart is ChartPart)
                throw new InvalidOperationException("不支持复制含图表页的页（图表与内嵌工作簿的克隆会破坏包结构）；"
                    + "请改为用 append 重新生成该页。");
            if (child.OpenXmlPart is not ImagePart && child.OpenXmlPart is not SlideLayoutPart)
                Warn("复制页时未带上部件：" + child.OpenXmlPart.GetType().Name);
        }

        var list = presPart.Presentation.SlideIdList!;
        var nextId = list.Elements<P.SlideId>().Select(x => x.Id?.Value ?? 0u).DefaultIfEmpty(255u).Max() + 1;
        var anchor = ordered[slideNo - 1].Id;
        for (var n = 0; n < count; n++)
        {
            var clone = presPart.AddNewPart<SlidePart>();
            if (source.SlideLayoutPart is { } layout) clone.AddPart(layout);
            clone.Slide = new P.Slide(source.Slide.OuterXml);
            foreach (var blip in clone.Slide.Descendants<A.Blip>().ToList())
            {
                var oldId = blip.Embed?.Value;
                if (oldId is null) continue;
                if (source.GetPartById(oldId) is not ImagePart img) continue;
                var newImg = clone.AddImagePart(img.ContentType);
                using (var s = img.GetStream()) newImg.FeedData(s);
                blip.Embed = clone.GetIdOfPart(newImg);
            }
            clone.Slide.Save();
            var newSlideId = new P.SlideId { Id = nextId++, RelationshipId = presPart.GetIdOfPart(clone) };
            anchor.InsertAfterSelf(newSlideId);
            anchor = newSlideId;
        }
        return $"复制第 {slideNo} 页 {count} 份";
    }

    /// <summary>逐页替换文本：<c>map</c> 是「旧→新」；<c>slides</c> 限定范围（缺省=全部页）。</summary>
    private static string ReplaceSlideText(PresentationPart presPart, JsonElement op)
    {
        if (!op.TryGetProperty("map", out var mapEl) || mapEl.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("replaceText 缺少 map，如 {\"旧文案\":\"新文案\"}。");
        var pairs = new List<(string From, string To)>();
        foreach (var p in mapEl.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String) pairs.Add((p.Name, p.Value.GetString() ?? ""));
        if (pairs.Count == 0) throw new InvalidOperationException("replaceText 的 map 为空。");

        var ordered = OrderedSlides(presPart);
        var scope = IntList(op, "slides");
        var targets = scope.Count == 0
            ? Enumerable.Range(1, ordered.Count).ToList()
            : scope;
        var hits = 0;
        foreach (var no in targets)
        {
            if (no < 1 || no > ordered.Count)
                throw new InvalidOperationException($"页号越界：{no}（当前共 {ordered.Count} 页）。");
            var sp = ordered[no - 1].Part;
            foreach (var t in sp.Slide.Descendants<A.Text>())
            {
                var text = t.Text ?? "";
                var replaced = text;
                foreach (var (from, to) in pairs) replaced = replaced.Replace(from, to, StringComparison.Ordinal);
                if (replaced != text) { t.Text = replaced; hits++; }
            }
            sp.Slide.Save();
        }
        return $"替换文本 {hits} 处（{pairs.Count} 组规则，{targets.Count} 页）";
    }

    /// <summary>把新页追加到末尾（用本技能的渲染器画，沿用既有稿的版式）。</summary>
    private static string AppendSlides(PresentationPart presPart, JsonElement op)
    {
        if (!op.TryGetProperty("slides", out var sv) || sv.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("append 缺少 slides。");
        var layout = presPart.SlideMasterParts.FirstOrDefault()?.SlideLayoutParts.FirstOrDefault()
            ?? throw new InvalidOperationException("该稿没有可用版式（slideLayout），无法追加新页。");
        var list = presPart.Presentation.SlideIdList ??= new P.SlideIdList();
        var nextId = list.Elements<P.SlideId>().Select(x => x.Id?.Value ?? 0u).DefaultIfEmpty(255u).Max() + 1;
        var added = 0;
        foreach (var el in sv.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var sp = presPart.AddNewPart<SlidePart>();
            sp.AddPart(layout);
            var ctx = new SlideCtx(sp, _currentTheme ?? new Theme(), added + 1, sv.GetArrayLength(),
                Str(el, "title") ?? "", null, null, null);
            sp.Slide = new P.Slide(RenderSlide(el, ctx));
            var notes = Str(el, "notes");
            if (!string.IsNullOrWhiteSpace(notes) && presPart.NotesMasterPart is { } nm) AttachNotes(sp, notes!, nm);
            sp.Slide.Save();
            list.Append(new P.SlideId { Id = nextId++, RelationshipId = presPart.GetIdOfPart(sp) });
            added++;
        }
        return $"追加 {added} 页";
    }

    // ===== 构建 =====
    private static (int Slides, string Path, List<string>? Types) Build(string input)
    {
        using var reqDoc = JsonDocument.Parse(ExtractJson(input));
        var root = reqDoc.RootElement;

        var theme = ResolveTheme(root);
        _currentTheme = theme;
        // 版式风格（sharp|soft|rounded|pill）：与主题正交，只影响页边距/间距/圆角
        _currentMetrics = MetricsFor(Str(root, "style") ?? "soft");
        _nativeCharts = 0;
        _nativeFallback = null;
        _titleRule = root.TryGetProperty("titleRule", out var trEl) && trEl.ValueKind == JsonValueKind.True;
        _warnings = new List<string>();
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
        // 自检时要知道每页本来的页型（才能判“内容页只剩标题”），这里同步记下来
        var pageTypes = pageJson.Select(PageTypeOf).ToList();

        var path = ResolveOutputPath(root, title);

        // 套模板：保留模板的母版/版式/主题，把我们的内容渲染进去（改副本，不动原件）
        var templatePath = Str(root, "template");
        if (!string.IsNullOrWhiteSpace(templatePath))
        {
            var fromTemplate = BuildFromTemplate(root, pageJson, path, theme, templatePath!.Trim());
            // 保留模板原有页时，序号与 types 对不上 → 不做依赖页型的自检（宁可少报，也不要错报）
            var keep = root.TryGetProperty("keepTemplateSlides", out var kv) && kv.ValueKind == JsonValueKind.True;
            return (fromTemplate.Slides, fromTemplate.Path, keep ? null : pageTypes);
        }

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

        return (count, path, pageTypes);
    }

    /// <summary>取一页的页型（已归一为小写）；缺失时当 content 处理。</summary>
    private static string PageTypeOf(string pageJson)
    {
        try
        {
            using var d = JsonDocument.Parse(pageJson);
            return (Str(d.RootElement, "type") ?? "content").Trim().ToLowerInvariant();
        }
        catch { return "content"; }
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
        // 模板若把东亚字体单独指定了，就用它（很多中文模板是 “Arial + 微软雅黑” 这种搭配）
        var templateEa = elements.FontScheme?.MinorFont?.EastAsianFont?.Typeface?.Value;
        if (templateEa is { Length: > 0 }) t.FontCjk = templateEa;
        else if (LooksCjkFont(t.FontBody)) t.FontCjk = t.FontBody;

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
                    theme.FontCjk = fromTemplate.FontCjk;
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
                // 半出血图文页自包含（标题叠在图上），不走通用“标题 + 正文”骨架
                if (VariantOf(el, "full") is "bleed" or "half") return ImageBleed(el, ctx);
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
            case "progress":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(ProgressBody(el, ctx));
                break;
            case "summary":
                shapes.Add(SlideTitle(Str(el, "title") ?? "小结", ctx));
                shapes.Add(SummaryBody(el, ctx));
                break;
            default: // content（可用 layout 指定子类型，省得模型记多个 type）
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add((Str(el, "layout") ?? "").Trim().ToLowerInvariant() switch
                {
                    "timeline" or "process" or "steps" => TimelineBody(el, ctx),
                    "grid" or "cards" => GridBody(el, ctx),
                    "stats" or "callouts" or "numbers" => StatsBody(el, ctx),
                    "progress" or "bars" or "gauge" => ProgressBody(el, ctx),
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
    /// <summary>
    /// 封面：按 <c>variant</c> 选版式（design-system/slide-types 里封面给了多种排法）。
    /// <list type="bullet">
    /// <item><c>left</c>（默认）：左色块 + 右侧标题，稳重。</item>
    /// <item><c>center</c>：居中标题，留白最大，适合演讲/发布会。</item>
    /// <item><c>image</c>：整页背景图 + 半透明蒙层 + 居中标题（需 <c>path</c>）。</item>
    /// <item><c>split</c>：左文右图，适合产品/企业介绍。</item>
    /// </list>
    /// </summary>
    private static string CoverSlide(JsonElement el, SlideCtx ctx)
    {
        return VariantOf(el, "left") switch
        {
            "center" => CoverCenter(el, ctx),
            "image" => CoverImageBg(el, ctx),
            "split" => CoverSplit(el, ctx),
            _ => CoverLeft(el, ctx),
        };
    }

    private static string CoverLeft(JsonElement el, SlideCtx ctx)
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

    /// <summary>居中封面：无图片依赖，靠留白与字号对比做焦点。</summary>
    private static string CoverCenter(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new List<string> { Rect(ctx.NextId(), 0, 0, W, H, t.Bg) };
        // 顶部与底部的细色带：不加标题下划线，但保留一点“设计感”"
        shapes.Add(Rect(ctx.NextId(), W - 2743200, 0, 2743200, 114300, t.Accent));
        shapes.Add(Rect(ctx.NextId(), 0, H - 114300, W, 114300, t.Primary));

        var title = Str(el, "title") ?? ctx.Title;
        var subtitle = Str(el, "subtitle") ?? ctx.Subtitle;
        var author = Str(el, "author") ?? ctx.Author;
        var date = Str(el, "date") ?? ctx.Date;

        var paras = new StringBuilder();
        paras.Append(ParaTitle(title, 4400, t.Primary, align: "ctr", lineSpacing: 105));
        if (!string.IsNullOrWhiteSpace(subtitle))
            paras.Append(Para(subtitle!, 1800, t.Secondary, align: "ctr", spaceBefore: 18, lineSpacing: 130));
        var meta = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(author)) meta.Append(Para(author!.Trim(), 1400, t.Text, align: "ctr"));
        if (!string.IsNullOrWhiteSpace(date)) meta.Append(Para(date!.Trim(), 1400, t.Secondary, align: "ctr", spaceBefore: 4));
        if (meta.Length > 0) paras.Append(meta.ToString());

        shapes.Add(TextBox(ctx.NextId(), MX, 1714500, CW, 3429000, paras.ToString(), anchor: "ctr"));
        return SlideXml(t.Bg, shapes);
    }

    /// <summary>背景图封面：整页图 + 半透明蒙层，保证标题在任何图上都读得清。</summary>
    private static string CoverImageBg(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var path = Str(el, "path");
        var shapes = new List<string>();
        var hasImg = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        if (hasImg)
        {
            shapes.Add(Picture(ctx.NextId(), AddCoverImage(ctx, path!, W, H), 0, 0, W, H));
            // 蒙层：主色 45% 透明 —— 文字对比度靠它守住，不能省
            shapes.Add(Rect(ctx.NextId(), 0, 0, W, H, t.Primary, alpha: 55));
        }
        else
        {
            shapes.Add(Rect(ctx.NextId(), 0, 0, W, H, t.Primary));
            if (!string.IsNullOrWhiteSpace(path)) Warn("封面背景图不存在，已改用主色底：" + path);
        }
        var onImg = hasImg ? t.OnPrimary : t.Bg;

        var title = Str(el, "title") ?? ctx.Title;
        var subtitle = Str(el, "subtitle") ?? ctx.Subtitle;
        var paras = new StringBuilder();
        paras.Append(ParaTitle(title, 4400, t.Bg, align: "ctr", lineSpacing: 105));
        if (!string.IsNullOrWhiteSpace(subtitle))
            paras.Append(Para(subtitle!, 1800, onImg, align: "ctr", spaceBefore: 18, lineSpacing: 130));
        var author = Str(el, "author") ?? ctx.Author;
        var date = Str(el, "date") ?? ctx.Date;
        if (!string.IsNullOrWhiteSpace(author) || !string.IsNullOrWhiteSpace(date))
            paras.Append(Para(string.Join("　·　", new[] { (author ?? "").Trim(), (date ?? "").Trim() }
                .Where(s => s.Length > 0)), 1400, onImg, align: "ctr", spaceBefore: 24));
        shapes.Add(TextBox(ctx.NextId(), MX, 1714500, CW, 3429000, paras.ToString(), anchor: "ctr"));
        return SlideXml(hasImg ? t.Bg : t.Primary, shapes);
    }

    /// <summary>左文右图封面（非对称布局）：右半页铺满图，左半页放标题与元信息。</summary>
    private static string CoverSplit(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new List<string> { Rect(ctx.NextId(), 0, 0, W, H, t.Bg) };
        var imgW = W * 5 / 12;
        var imgX = W - imgW;
        var path = Str(el, "path");
        var hasImg = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        if (hasImg)
            shapes.Add(Picture(ctx.NextId(), AddCoverImage(ctx, path!, imgW, H), imgX, 0, imgW, H));
        else
        {
            shapes.Add(Rect(ctx.NextId(), imgX, 0, imgW, H, t.Primary));
            shapes.Add(Rect(ctx.NextId(), imgX - 114300, 0, 114300, H, t.Accent));
            if (!string.IsNullOrWhiteSpace(path)) Warn("封面右图不存在，已改用主色块：" + path);
        }

        var textW = imgX - MX - 457200;
        var title = Str(el, "title") ?? ctx.Title;
        var subtitle = Str(el, "subtitle") ?? ctx.Subtitle;
        var paras = new StringBuilder();
        paras.Append(ParaTitle(title, 3600, t.Primary, lineSpacing: 105));
        if (!string.IsNullOrWhiteSpace(subtitle))
            paras.Append(Para(subtitle!, 1700, t.Secondary, spaceBefore: 14, lineSpacing: 130));
        shapes.Add(TextBox(ctx.NextId(), MX, 1900000, textW, 2743200, paras.ToString(), anchor: "t"));

        var author = Str(el, "author") ?? ctx.Author;
        var date = Str(el, "date") ?? ctx.Date;
        if (!string.IsNullOrWhiteSpace(author) || !string.IsNullOrWhiteSpace(date))
        {
            var meta = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(author)) meta.Append(Para(author!.Trim(), 1400, t.Text));
            if (!string.IsNullOrWhiteSpace(date)) meta.Append(Para(date!.Trim(), 1400, t.Secondary, spaceBefore: 4));
            shapes.Add(TextBox(ctx.NextId(), MX, H - 1371600, textW, 762000, meta.ToString(), anchor: "b"));
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
    /// <summary>
    /// 章节分隔：<c>variant</c> = <c>number</c>（默认，满页主色 + 大序号）|
    /// <c>bar</c>（浅底 + 左侧色块序号，克制）| <c>full</c>（满页主色 + 超大透明序号水印，强烈）。
    /// </summary>
    private static string SectionSlide(JsonElement el, SlideCtx ctx)
    {
        return VariantOf(el, "number") switch
        {
            "bar" => SectionBar(el, ctx),
            "full" => SectionFull(el, ctx),
            _ => SectionNumber(el, ctx),
        };
    }

    private static string SectionNumber(JsonElement el, SlideCtx ctx)
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

    /// <summary>左侧色块分隔：与内容页同底色，靠一块主色矩形与大序号做转场，比满页色简洁。</summary>
    private static string SectionBar(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var blockW = W / 4;
        var shapes = new List<string> { Rect(ctx.NextId(), 0, 0, W, H, t.Bg) };
        shapes.Add(Rect(ctx.NextId(), 0, 0, blockW, H, t.Primary));
        shapes.Add(Rect(ctx.NextId(), blockW, 0, 76200, H, t.Accent));
        shapes.Add(TextBox(ctx.NextId(), 0, 0, blockW, H,
            Para(ctx.Index.ToString("00"), 6000, t.Accent, bold: true, align: "ctr", font: t.FontTitle),
            anchor: "ctr"));

        var paras = new StringBuilder();
        paras.Append(ParaTitle(Str(el, "title") ?? "", 3400, t.Primary, lineSpacing: 110));
        var sub = Str(el, "subtitle");
        if (!string.IsNullOrWhiteSpace(sub))
            paras.Append(Para(sub!, 1600, t.Secondary, align: "l", spaceBefore: 14));
        shapes.Add(TextBox(ctx.NextId(), blockW + 76200 + MX, 2057400, W - blockW - 76200 - MX * 2, 2743200,
            paras.ToString(), anchor: "ctr"));
        shapes.Add(PageBadge(ctx));
        return SlideXml(t.Bg, shapes);
    }

    /// <summary>满页色 + 超大序号水印：序号用低透明度画在上部，标题压在下部（两者不重叠才能读）。</summary>
    private static string SectionFull(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new List<string> { Rect(ctx.NextId(), 0, 0, W, H, t.Primary) };
        // 水印序号：同底色的低透明度大字（用 a:alpha，不要用颜色编透明度）
        shapes.Add(TextBox(ctx.NextId(), MX, 0, CW, 4114800,
            Para(ctx.Index.ToString("00"), 20000, t.Bg, bold: true, align: "ctr",
                font: t.FontTitle, alpha: 22), anchor: "ctr"));
        var paras = new StringBuilder();
        paras.Append(ParaTitle(Str(el, "title") ?? "", 4000, t.Bg, align: "ctr", lineSpacing: 110));
        var sub = Str(el, "subtitle");
        if (!string.IsNullOrWhiteSpace(sub))
            paras.Append(Para(sub!, 1700, t.OnPrimary, align: "ctr", spaceBefore: 16));
        shapes.Add(TextBox(ctx.NextId(), MX, 4114800, CW, 2057400, paras.ToString(), anchor: "ctr"));
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
    /// <summary>
    /// 页标题：左对齐标题 + （可选）标题下短线 + 右下角页码徽标。
    ///
    /// <para>
    /// <b>默认不画“标题下强调线”</b>：设计规范（pptx-generator · pitfalls.md）把它列为
    /// 「AI 生成稿的典型特征」，要求用留白或背景色来做层级，而不是加一条线。
    /// 想要旧观感的调用方可以传 <c>"titleRule": true</c> 把线加回来。
    /// </para>
    /// </summary>
    private static string SlideTitle(string text, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new StringBuilder();
        shapes.Append(TextBox(ctx.NextId(), MX, TitleY, CW, TitleH,
            ParaTitle(text, 2800, t.Primary, lineSpacing: 100)));
        if (_titleRule)
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
    /// <summary>
    /// 目录：<c>variant</c> = <c>list</c>（默认，编号竖列）| <c>grid</c>（两列卡片）| <c>sidebar</c>（左侧色条 + 行）。
    /// 项数多时优选 grid（两列能多放一倍），项少时 list 更清晰。
    /// </summary>
    private static string TocBody(JsonElement el, SlideCtx ctx)
    {
        return VariantOf(el, "list") switch
        {
            "grid" or "cards" => TocGrid(el, ctx),
            "sidebar" or "nav" => TocSidebar(el, ctx),
            _ => TocList(el, ctx),
        };
    }

    private static string TocList(JsonElement el, SlideCtx ctx)
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

    /// <summary>两列卡片目录：每张卡 = 主色序号 + 标题，卡底用浅色，适合 4~8 个章节。</summary>
    private static string TocGrid(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var items = StringList(el, "items");
        if (items.Count == 0) return BulletBody(el, ctx, accent: false);

        var cols = items.Count <= 2 ? 1 : 2;
        var rows = (int)Math.Ceiling(items.Count / (double)cols);
        var gap = _m.Gap;
        var cardW = (CW - gap * (cols - 1)) / cols;
        var cardH = Math.Min((long)(BodyH - gap * (rows - 1)) / Math.Max(1, rows), BodyH);
        var plan = items.Select(it => (Text: it, Size: 1700, SpaceBefore: 0, LineSpacing: 1.20)).ToList();
        var scale = ScaleForHeight(plan, cardW - _m.Pad * 2 - 900000, cardH - _m.Pad);
        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var c = i % cols; var r = i / cols;
            var x = MX + c * (cardW + gap);
            var y = BodyY + r * (cardH + gap);
            sb.Append(Rect(ctx.NextId(), x, y, cardW, cardH, t.Light, radius: true));
            // 左侧序号块
            sb.Append(Rect(ctx.NextId(), x, y, 57150, cardH, t.Accent));
            sb.Append(TextBox(ctx.NextId(), x + _m.Pad, y, cardW - _m.Pad * 2, cardH,
                Para((i + 1).ToString("00"), Scaled(3000, scale), t.Accent, bold: true, align: "l", font: t.FontTitle)
                + Para(items[i], Scaled(1700, scale), t.Text, align: "l", spaceBefore: 6, lineSpacing: 120),
                anchor: "ctr"));
        }
        return sb.ToString();
    }

    /// <summary>侧栏目录：左侧一条主色竖条 + 逐行“序号 标题”，清爽、适合 3~5 个章节。</summary>
    private static string TocSidebar(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var items = StringList(el, "items");
        if (items.Count == 0) return BulletBody(el, ctx, accent: false);

        var barW = 76200;
        var sb = new StringBuilder();
        sb.Append(Rect(ctx.NextId(), MX, BodyY, barW, BodyH, t.Accent));
        var textX = MX + barW + _m.Gap;
        var textW = CW - barW - _m.Gap;
        var plan = items.Select(it => (Text: it, Size: 1800, SpaceBefore: 16, LineSpacing: 1.20)).ToList();
        var scale = ScaleForHeight(plan, textW - 800000, BodyH);
        var paras = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            paras.Append(Para((i + 1).ToString("00"), Scaled(1600, scale), t.Accent, bold: true, align: "l",
                spaceBefore: (int)Math.Round((i == 0 ? 0 : 16) * scale)));
            paras.Append(Para(items[i], Scaled(1800, scale), t.Text, align: "l", spaceBefore: 2,
                lineSpacing: (int)Math.Round(120 * scale)));
        }
        sb.Append(TextBox(ctx.NextId(), textX, BodyY, textW, BodyH, paras.ToString(), anchor: "t"));
        return sb.ToString();
    }

    // ---- 小结 / 收尾 ----
    /// <summary>
    /// 小结页：<c>variant</c> = <c>list</c>（默认，要点回顾）| <c>cta</c>（下一步行动，大号序号）|
    /// <c>split</c>（左回顾 / 右行动 + 联系方式）。
    /// 行动项与联系方式可用顶层 <c>items</c>（或 <c>bullets</c>）与 <c>contact</c> 字段。
    /// </summary>
    private static string SummaryBody(JsonElement el, SlideCtx ctx)
    {
        return VariantOf(el, "list") switch
        {
            "cta" or "next" or "actions" => SummaryCta(el, ctx),
            "split" or "recap" => SummarySplit(el, ctx),
            _ => BulletBody(el, ctx, accent: true),
        };
    }

    private static string SummaryCta(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var items = StringList(el, "items");
        if (items.Count == 0) items = StringList(el, "bullets");
        if (items.Count == 0) return BulletBody(el, ctx, accent: true);

        var contactH = string.IsNullOrWhiteSpace(Str(el, "contact")) ? 0 : 685800;
        var availH = BodyH - contactH;
        var rowH = Math.Min(availH / items.Count, 1143000);
        var totalH = rowH * items.Count;
        var y0 = BodyY + Math.Max(0, (availH - totalH) / 2);

        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var y = y0 + i * rowH;
            var numW = 914400;
            sb.Append(Rect(ctx.NextId(), MX, y + 68580, 45720, rowH - 137160, t.Accent));
            sb.Append(TextBox(ctx.NextId(), MX + 152400, y, numW, rowH,
                Para((i + 1).ToString("00"), 3200, t.Accent, bold: true, align: "l", font: t.FontTitle),
                anchor: "ctr"));
            sb.Append(TextBox(ctx.NextId(), MX + numW + 152400, y, CW - numW - 152400, rowH,
                Para(items[i], 1800, t.Primary, align: "l", lineSpacing: 120), anchor: "ctr"));
        }
        var contact = Str(el, "contact");
        if (!string.IsNullOrWhiteSpace(contact))
            sb.Append(TextBox(ctx.NextId(), MX, BodyY + availH, CW, 457200,
                Para(contact!.Trim(), 1400, t.Secondary, align: "l"), anchor: "b"));
        return sb.ToString();
    }

    private static string SummarySplit(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var gap = _m.Gap;
        var colW = (CW - gap) / 2;
        var sb = new StringBuilder();

        // 左：回顾要点（浅底卡片）。直接按目标色出段落，不做事后字符串替换。
        var bullets = StringList(el, "bullets");
        if (bullets.Count == 0) bullets = StringList(el, "items");
        var plan = bullets.Select(b => (Text: b, Size: 1650, SpaceBefore: 10, LineSpacing: 1.25)).ToList();
        var scale = ScaleForHeight(plan, colW - _m.Pad * 2 - _m.Gap, BodyH - _m.Pad * 2);
        var lp = new StringBuilder();
        if (bullets.Count == 0) lp.Append(Para("（本页暂无回顾要点）", 1500, t.Secondary));
        foreach (var b in bullets)
            lp.Append(Para(b, Scaled(1650, scale), t.Text, align: "l", bullet: "•",
                spaceBefore: (int)Math.Round(10 * scale), lineSpacing: (int)Math.Round(125 * scale), marL: (int)_m.Gap));
        sb.Append(Rect(ctx.NextId(), MX, BodyY, colW, BodyH, t.Light, radius: true));
        sb.Append(TextBox(ctx.NextId(), MX + _m.Pad, BodyY + _m.Pad, colW - _m.Pad * 2, BodyH - _m.Pad * 2,
            lp.ToString(), anchor: "t"));

        // 右：下一步 / 联系方式（主色卡片，文字一律用底色/反色，保证深底上可读）
        var rightX = MX + colW + gap;
        var actions = StringList(el, "actions");
        if (actions.Count == 0) actions = StringList(el, "next");
        var rp = new StringBuilder();
        rp.Append(ParaTitle(Str(el, "rightTitle") ?? "下一步", 2000, t.Bg));
        var rplan = actions.Select(a => (Text: a, Size: 1600, SpaceBefore: 12, LineSpacing: 1.25)).ToList();
        var rscale = ScaleForHeight(rplan, colW - _m.Pad * 2, BodyH - _m.Pad * 2 - 762000);
        for (var i = 0; i < actions.Count; i++)
            rp.Append(Para((i + 1) + ". " + actions[i], Scaled(1600, rscale), t.Bg, align: "l",
                spaceBefore: (int)Math.Round(12 * rscale), lineSpacing: (int)Math.Round(125 * rscale)));
        var contact = Str(el, "contact");
        if (!string.IsNullOrWhiteSpace(contact))
            rp.Append(Para(contact!.Trim(), 1400, t.OnPrimary, align: "l", spaceBefore: 20));
        sb.Append(Rect(ctx.NextId(), rightX, BodyY, colW, BodyH, t.Primary, radius: true));
        sb.Append(TextBox(ctx.NextId(), rightX + _m.Pad, BodyY + _m.Pad, colW - _m.Pad * 2, BodyH - _m.Pad * 2,
            rp.ToString(), anchor: "t"));
        return sb.ToString();
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

    /// <summary>取数值字段（接受数字与数字字符串），缺失/非法时用 fallback。</summary>
    private static double NumOf(JsonElement o, string name, double fallback)
    {
        if (!o.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d)) return d;
        return fallback;
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
        // 基础语义（最初 12 个）
        "check", "cross", "arrow", "star", "dot", "warn",
        "lock", "user", "chart", "clock", "gear", "bulb",
        // 业务语义扩充（归一化坐标绘制，见 RenderIcon）
        "money", "target", "rocket", "shield", "layers", "globe", "network", "cloud",
        "database", "mail", "phone", "calendar", "flag", "search", "edit", "file",
        "pie", "link", "eye", "heart", "key", "crown", "map", "cpu",
        "package", "award", "briefcase", "users", "code", "gauge", "filter", "refresh", "download",
    };

    /// <summary>两点半径的椭圆：<c>ImgEllipse(center, rx, ry)</c> 在 ImageSharp.Drawing 1.0 里不存在，得用 SizeF。</summary>
    private static ImgEllipse Ell(ImgPointF center, float rx, float ry)
        => new ImgEllipse(center, new SixLabors.ImageSharp.SizeF(rx, ry));

    private static bool IsIconName(string s) => IconNames.Contains(s.Trim().ToLowerInvariant());

    /// <summary>
    /// 把内置图标画成 PNG（透明底 + 指定颜色）。
    ///
    /// <para>
    /// 为什么自己画：技能是**单个编译单元**，既不能携带字体/素材文件，也不能假设宿主装了某个图标字体。
    /// ImageSharp 已经为图表引入了，画几个几何图形是顺手的事；颜色还能直接跟着主题走。
    /// </para>
    /// </summary>
    /// <summary>
    /// 画一个内置图标（透明底 PNG）。
    ///
    /// <para>
    /// 为什么自己画而不是用图标字体/react-icons：技能是<b>单个编译单元</b>，
    /// 既不能携带字体/素材文件，也不能假设宿主装了某个图标库；而 ImageSharp 已为图表引入，
    /// 画几何图形是顺手的事。
    /// </para>
    ///
    /// <para>
    /// 老图标（check/cross/... 12 个）用原始像素坐标手调过，保持原样；
    /// 新增图标统一用<b>归一化坐标</b>（<c>N(x,y)</c>，0~1）写，少算错、也好维护。
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

        // 归一化坐标下的辅助量（0~1）
        ImgPointF N(float nx, float ny) => new ImgPointF(px * nx, px * ny);
        SixLabors.ImageSharp.Drawing.IPath NP(params (float X, float Y)[] pts)
            => Poly(pts.Select(p => N(p.X, p.Y)).ToArray());

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
                case "bulb":
                    x.Draw(c, t, new ImgEllipse(new ImgPointF(mid, px * 0.42f), px * 0.26f));
                    x.Fill(c, new ImgRect(px * 0.40f, px * 0.72f, px * 0.20f, px * 0.14f));
                    break;

                // ===== 以下为归一化坐标新增（0~1），覆盖常见业务语义 =====
                case "money":
                    x.Draw(c, t, new ImgEllipse(N(0.5f, 0.5f), px * 0.33f));
                    x.DrawLine(c, t, N(0.36f, 0.38f), N(0.5f, 0.52f));
                    x.DrawLine(c, t, N(0.64f, 0.38f), N(0.5f, 0.52f));
                    x.DrawLine(c, t, N(0.5f, 0.52f), N(0.5f, 0.70f));
                    x.DrawLine(c, t, N(0.39f, 0.58f), N(0.61f, 0.58f));
                    break;
                case "target":
                    x.Draw(c, t, new ImgEllipse(N(0.5f, 0.5f), px * 0.34f));
                    x.Draw(c, t, new ImgEllipse(N(0.5f, 0.5f), px * 0.22f));
                    x.Fill(c, new ImgEllipse(N(0.5f, 0.5f), px * 0.09f));
                    break;
                case "rocket":
                    x.Fill(c, NP((0.50f, 0.16f), (0.66f, 0.54f), (0.34f, 0.54f)));
                    x.Fill(c, NP((0.34f, 0.54f), (0.22f, 0.72f), (0.34f, 0.70f)));
                    x.Fill(c, NP((0.66f, 0.54f), (0.78f, 0.72f), (0.66f, 0.70f)));
                    x.Fill(c, NP((0.46f, 0.58f), (0.54f, 0.58f), (0.50f, 0.84f)));
                    break;
                case "shield":
                    x.Fill(c, NP((0.5f, 0.16f), (0.80f, 0.28f), (0.80f, 0.52f), (0.5f, 0.84f), (0.20f, 0.52f), (0.20f, 0.28f)));
                    break;
                case "layers":
                    x.Fill(c, NP((0.50f, 0.18f), (0.86f, 0.36f), (0.50f, 0.54f), (0.14f, 0.36f)));
                    x.Draw(c, t, NP((0.14f, 0.52f), (0.50f, 0.70f), (0.86f, 0.52f)));
                    x.Draw(c, t, NP((0.14f, 0.68f), (0.50f, 0.86f), (0.86f, 0.68f)));
                    break;
                case "globe":
                    x.Draw(c, t, new ImgEllipse(N(0.5f, 0.5f), px * 0.34f));
                    x.DrawLine(c, t, N(0.16f, 0.5f), N(0.84f, 0.5f));
                    x.Draw(c, t, Ell(N(0.5f, 0.5f), px * 0.15f, px * 0.34f));
                    break;
                case "network":
                    x.Fill(c, new ImgEllipse(N(0.5f, 0.22f), px * 0.10f));
                    x.Fill(c, new ImgEllipse(N(0.24f, 0.74f), px * 0.10f));
                    x.Fill(c, new ImgEllipse(N(0.76f, 0.74f), px * 0.10f));
                    x.DrawLine(c, t, N(0.46f, 0.30f), N(0.28f, 0.66f));
                    x.DrawLine(c, t, N(0.54f, 0.30f), N(0.72f, 0.66f));
                    x.DrawLine(c, t, N(0.34f, 0.74f), N(0.66f, 0.74f));
                    break;
                case "cloud":
                    x.Fill(c, new ImgEllipse(N(0.36f, 0.52f), px * 0.16f));
                    x.Fill(c, new ImgEllipse(N(0.53f, 0.44f), px * 0.20f));
                    x.Fill(c, new ImgEllipse(N(0.70f, 0.54f), px * 0.15f));
                    x.Fill(c, new ImgRect(px * 0.28f, px * 0.52f, px * 0.46f, px * 0.17f));
                    break;
                case "database":
                    x.Fill(c, new ImgRect(px * 0.22f, px * 0.30f, px * 0.56f, px * 0.46f));
                    x.Draw(c, t, Ell(N(0.5f, 0.30f), px * 0.28f, px * 0.10f));
                    x.DrawLine(c, t, N(0.22f, 0.48f), N(0.78f, 0.48f));
                    x.DrawLine(c, t, N(0.22f, 0.62f), N(0.78f, 0.62f));
                    break;
                case "mail":
                    x.Draw(c, t, new ImgRect(px * 0.14f, px * 0.28f, px * 0.72f, px * 0.44f));
                    x.DrawLine(c, t, N(0.14f, 0.28f), N(0.50f, 0.55f));
                    x.DrawLine(c, t, N(0.50f, 0.55f), N(0.86f, 0.28f));
                    break;
                case "phone":
                    x.Draw(c, t, new ImgRect(px * 0.30f, px * 0.14f, px * 0.40f, px * 0.72f));
                    x.Fill(c, new ImgEllipse(N(0.50f, 0.78f), px * 0.05f));
                    break;
                case "calendar":
                    x.Draw(c, t, new ImgRect(px * 0.16f, px * 0.26f, px * 0.68f, px * 0.58f));
                    x.DrawLine(c, t, N(0.16f, 0.42f), N(0.84f, 0.42f));
                    x.DrawLine(c, t, N(0.34f, 0.14f), N(0.34f, 0.30f));
                    x.DrawLine(c, t, N(0.66f, 0.14f), N(0.66f, 0.30f));
                    break;
                case "flag":
                    x.DrawLine(c, t, N(0.28f, 0.14f), N(0.28f, 0.88f));
                    x.Fill(c, NP((0.30f, 0.16f), (0.80f, 0.30f), (0.30f, 0.46f)));
                    break;
                case "search":
                    x.Draw(c, t, new ImgEllipse(N(0.44f, 0.42f), px * 0.25f));
                    x.DrawLine(c, t, N(0.62f, 0.60f), N(0.84f, 0.82f));
                    break;
                case "edit":
                    x.Fill(c, NP((0.24f, 0.76f), (0.34f, 0.66f), (0.66f, 0.20f), (0.80f, 0.30f), (0.46f, 0.78f)));
                    x.DrawLine(c, t, N(0.24f, 0.76f), N(0.46f, 0.78f));
                    break;
                case "file":
                    x.Draw(c, t, NP((0.26f, 0.14f), (0.60f, 0.14f), (0.76f, 0.32f), (0.76f, 0.86f), (0.26f, 0.86f)));
                    x.DrawLine(c, t, N(0.58f, 0.14f), N(0.58f, 0.34f));
                    x.DrawLine(c, t, N(0.58f, 0.34f), N(0.76f, 0.34f));
                    break;
                case "pie":
                    x.Fill(c, Poly(N(0.5f, 0.5f), N(0.5f, 0.16f),
                        N(0.74f, 0.24f), N(0.84f, 0.5f), N(0.74f, 0.76f), N(0.5f, 0.84f)));
                    x.DrawLine(c, t, N(0.5f, 0.5f), N(0.5f, 0.16f));
                    x.DrawLine(c, t, N(0.5f, 0.5f), N(0.84f, 0.5f));
                    break;
                case "link":
                    x.Draw(c, t, Ell(N(0.36f, 0.50f), px * 0.16f, px * 0.10f));
                    x.Draw(c, t, Ell(N(0.64f, 0.50f), px * 0.16f, px * 0.10f));
                    x.DrawLine(c, t, N(0.46f, 0.5f), N(0.54f, 0.5f));
                    break;
                case "eye":
                    x.Draw(c, t, NP((0.14f, 0.50f), (0.34f, 0.28f), (0.66f, 0.28f), (0.86f, 0.50f),
                        (0.66f, 0.72f), (0.34f, 0.72f)));
                    x.Fill(c, new ImgEllipse(N(0.5f, 0.5f), px * 0.10f));
                    break;
                case "heart":
                    x.Fill(c, new ImgEllipse(N(0.37f, 0.38f), px * 0.16f));
                    x.Fill(c, new ImgEllipse(N(0.63f, 0.38f), px * 0.16f));
                    x.Fill(c, NP((0.21f, 0.42f), (0.79f, 0.42f), (0.50f, 0.84f)));
                    break;
                case "key":
                    x.Draw(c, t, new ImgEllipse(N(0.34f, 0.34f), px * 0.16f));
                    x.DrawLine(c, t, N(0.45f, 0.45f), N(0.82f, 0.82f));
                    x.DrawLine(c, t, N(0.70f, 0.70f), N(0.62f, 0.78f));
                    x.DrawLine(c, t, N(0.80f, 0.80f), N(0.72f, 0.88f));
                    break;
                case "crown":
                    x.Fill(c, NP((0.16f, 0.72f), (0.16f, 0.30f), (0.34f, 0.48f), (0.50f, 0.24f),
                        (0.66f, 0.48f), (0.84f, 0.30f), (0.84f, 0.72f)));
                    break;
                case "map":
                    x.Fill(c, new ImgEllipse(N(0.5f, 0.38f), px * 0.22f));
                    x.Fill(c, NP((0.31f, 0.47f), (0.69f, 0.47f), (0.50f, 0.86f)));
                    x.Fill(c, new ImgEllipse(N(0.5f, 0.38f), px * 0.09f));
                    break;
                case "cpu":
                    x.Draw(c, t, new ImgRect(px * 0.26f, px * 0.26f, px * 0.48f, px * 0.48f));
                    x.Fill(c, new ImgRect(px * 0.42f, px * 0.42f, px * 0.16f, px * 0.16f));
                    for (var i = 0; i < 3; i++)
                    {
                        var o = 0.34f + i * 0.16f;
                        x.DrawLine(c, t, N(o, 0.12f), N(o, 0.26f));
                        x.DrawLine(c, t, N(o, 0.74f), N(o, 0.88f));
                        x.DrawLine(c, t, N(0.12f, o), N(0.26f, o));
                        x.DrawLine(c, t, N(0.74f, o), N(0.88f, o));
                    }
                    break;
                case "package":
                    x.Draw(c, t, new ImgRect(px * 0.18f, px * 0.34f, px * 0.64f, px * 0.50f));
                    x.DrawLine(c, t, N(0.18f, 0.34f), N(0.50f, 0.46f));
                    x.DrawLine(c, t, N(0.82f, 0.34f), N(0.50f, 0.46f));
                    x.DrawLine(c, t, N(0.50f, 0.46f), N(0.50f, 0.84f));
                    break;
                case "award":
                    x.Draw(c, t, new ImgEllipse(N(0.5f, 0.40f), px * 0.24f));
                    x.Fill(c, NP((0.36f, 0.58f), (0.50f, 0.58f), (0.42f, 0.88f)));
                    x.Fill(c, NP((0.50f, 0.58f), (0.64f, 0.58f), (0.58f, 0.88f)));
                    break;
                case "briefcase":
                    x.Draw(c, t, new ImgRect(px * 0.14f, px * 0.36f, px * 0.72f, px * 0.44f));
                    x.Draw(c, t, new ImgRect(px * 0.38f, px * 0.22f, px * 0.24f, px * 0.14f));
                    x.DrawLine(c, t, N(0.14f, 0.52f), N(0.86f, 0.52f));
                    break;
                case "users":
                    x.Fill(c, new ImgEllipse(N(0.38f, 0.34f), px * 0.13f));
                    x.Fill(c, new ImgEllipse(N(0.38f, 0.78f), px * 0.24f));
                    x.Draw(c, t, new ImgEllipse(N(0.70f, 0.36f), px * 0.11f));
                    x.Draw(c, t, new ImgEllipse(N(0.74f, 0.78f), px * 0.20f));
                    break;
                case "code":
                    x.DrawLine(c, t, N(0.36f, 0.26f), N(0.16f, 0.50f));
                    x.DrawLine(c, t, N(0.16f, 0.50f), N(0.36f, 0.74f));
                    x.DrawLine(c, t, N(0.64f, 0.26f), N(0.84f, 0.50f));
                    x.DrawLine(c, t, N(0.84f, 0.50f), N(0.64f, 0.74f));
                    break;
                case "gauge":
                    x.Draw(c, t, new ImgEllipse(N(0.5f, 0.56f), px * 0.32f));
                    x.Fill(c, new ImgRect(px * 0.24f, px * 0.52f, px * 0.52f, px * 0.30f));
                    x.DrawLine(c, t, N(0.5f, 0.56f), N(0.70f, 0.34f));
                    break;
                case "filter":
                    x.Fill(c, NP((0.14f, 0.18f), (0.86f, 0.18f), (0.58f, 0.52f), (0.58f, 0.86f),
                        (0.42f, 0.78f), (0.42f, 0.52f)));
                    break;
                case "refresh":
                    x.Draw(c, t, new ImgEllipse(N(0.5f, 0.5f), px * 0.30f));
                    x.Fill(c, NP((0.62f, 0.12f), (0.92f, 0.26f), (0.66f, 0.40f)));
                    break;
                case "download":
                    x.DrawLine(c, t, N(0.5f, 0.14f), N(0.5f, 0.62f));
                    x.DrawLine(c, t, N(0.30f, 0.44f), N(0.5f, 0.62f));
                    x.DrawLine(c, t, N(0.70f, 0.44f), N(0.5f, 0.62f));
                    x.DrawLine(c, t, N(0.20f, 0.82f), N(0.80f, 0.82f));
                    break;
                default: // 其它名字 → 当作短标记（1~2 字），不画图标
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

    /// <summary>
    /// 开放折线路径（<b>不</b>闭合）。描边它就能得到一段“弧”——
    /// 环形进度必须用这个：若用“中心→弧→中心”的封闭扇形再拿底色挖内圆，
    /// 内圆会被填成<b>不透明底色</b>，在非该色的页面上就是一个白饼（实测踩到：
    /// 墨迹占比 0.65，看着是实心饼；而 PNG 本该中心透明）。
    /// </summary>
    private static SixLabors.ImageSharp.Drawing.IPath PolyOpen(params ImgPointF[] pts)
    {
        var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
        pb.MoveTo(pts[0]);
        for (var i = 1; i < pts.Length; i++) pb.LineTo(pts[i]);
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
    /// <summary>
    /// 配图页：<c>variant</c> = <c>full</c>（默认，整块图居中）| <c>left</c>（图左文右）|
    /// <c>right</c>（文左图右）| <c>bleed</c>（半出血图 + 文字叠在上面）|
    /// <c>gallery</c>（图廊，2~4 张）。
    ///
    /// <para>
    /// 参考 slide-types.md 的 Mixed Media / Image Showcase：
    /// 只有“整页一张图”不够——报告类幻灯片大量需要“左文右图”这种图文混排。
    /// </para>
    /// </summary>
    private static string ImageBody(JsonElement el, SlideCtx ctx)
    {
        return VariantOf(el, "full") switch
        {
            "left" => ImageSide(el, ctx, imageFirst: true),
            "right" => ImageSide(el, ctx, imageFirst: false),
            "gallery" or "grid" => ImageGallery(el, ctx),
            _ => ImageFull(el, ctx),
        };
    }

    /// <summary>图片缺失时的占位（不静默略过：报 warning，页面上也画出可见提示）。</summary>
    private static string ImageMissing(JsonElement el, SlideCtx ctx, string? path, long x, long y, long cx, long cy, bool radius)
    {
        var t = ctx.Theme;
        var msg = string.IsNullOrWhiteSpace(path) ? "（未提供 path，无法插入图片）" : "（图片不存在：" + path + "）";
        if (!string.IsNullOrWhiteSpace(path)) Warn("图片不存在，已改用占位块：" + path);
        return Rect(ctx.NextId(), x, y, cx, cy, t.Light, radius: radius)
             + TextBox(ctx.NextId(), x, y, cx, cy, Para(msg, 1300, t.Secondary, align: "ctr"), anchor: "ctr");
    }

    private static string ImageFull(JsonElement el, SlideCtx ctx)
    {
        var path = Str(el, "path");
        var caption = Str(el, "caption");
        var availH = BodyH - (string.IsNullOrWhiteSpace(caption) ? 0 : 457200);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return ImageMissing(el, ctx, path, MX, BodyY, CW, availH, radius: true);

        byte[] bytes; int pxW, pxH;
        try
        {
            using var img = Image.Load(path!);
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
        shapes.Append(Picture(ctx.NextId(), rel, x, y, cx, cy, radius: true));
        if (!string.IsNullOrWhiteSpace(caption))
            shapes.Append(TextBox(ctx.NextId(), MX, BodyY + availH, CW, 365760,
                Para(caption!, 1200, ctx.Theme.Secondary, align: "ctr")));
        return shapes.ToString();
    }

    /// <summary>图文混排：一半图（按框裁切，不变形）+ 一半文字要点。</summary>
    private static string ImageSide(JsonElement el, SlideCtx ctx, bool imageFirst)
    {
        var t = ctx.Theme;
        var gap = _m.Gap;
        var imgW = CW * 52 / 100;
        var textW = CW - imgW - gap;
        var imgX = imageFirst ? MX : MX + textW + gap;
        var textX = imageFirst ? MX + imgW + gap : MX;

        var sb = new StringBuilder();
        var path = Str(el, "path");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            sb.Append(Picture(ctx.NextId(), AddCoverImage(ctx, path!, imgW, BodyH), imgX, BodyY, imgW, BodyH, radius: true));
        else
            sb.Append(ImageMissing(el, ctx, path, imgX, BodyY, imgW, BodyH, radius: true));

        // 文字侧：可选小标题 + 要点
        var paras = new StringBuilder();
        var heading = Str(el, "heading");
        if (!string.IsNullOrWhiteSpace(heading))
            paras.Append(Para(heading!.Trim(), 1900, t.Primary, bold: true, align: "l", font: t.FontTitle));
        var items = StringList(el, "bullets");
        if (items.Count == 0) items = StringList(el, "items");
        var text = Str(el, "text");
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(text)) items.Add(text!);
        var caption = Str(el, "caption");
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(caption)) items.Add(caption!);

        var plan = items.Select(b => (Text: b, Size: 1600, SpaceBefore: 12, LineSpacing: 1.25)).ToList();
        var availH = BodyH - (string.IsNullOrWhiteSpace(heading) ? 0 : 762000);
        var scale = ScaleForHeight(plan, textW - _m.Gap, availH);
        if (items.Count == 0) paras.Append(Para("（未提供文字内容）", 1500, t.Secondary));
        foreach (var b in items)
            paras.Append(Para(b, Scaled(1600, scale), t.Text, align: "l", bullet: "•",
                spaceBefore: (int)Math.Round(12 * scale), lineSpacing: (int)Math.Round(125 * scale), marL: (int)_m.Gap));
        sb.Append(TextBox(ctx.NextId(), textX, BodyY, textW, BodyH, paras.ToString(), anchor: "t"));

        if (!string.IsNullOrWhiteSpace(caption) && items.Count > 0)
            sb.Append(Footnote(caption!, ctx));
        return sb.ToString();
    }

    /// <summary>图廊：2~4 张图平铺（每张按单元格裁切），可带逐张说明。</summary>
    private static string ImageGallery(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shots = new List<(string Path, string Caption)>();
        if (el.TryGetProperty("images", out var arrEl) && arrEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arrEl.EnumerateArray())
            {
                if (it.ValueKind == JsonValueKind.String) shots.Add((it.GetString() ?? "", ""));
                else if (it.ValueKind == JsonValueKind.Object)
                    shots.Add((Str(it, "path") ?? "", Str(it, "caption") ?? ""));
            }
        }
        if (shots.Count == 0 && Str(el, "path") is { Length: > 0 } single)
        {
            shots.Add((single, Str(el, "caption") ?? ""));
        }
        if (shots.Count == 0)
            return ImageMissing(el, ctx, null, MX, BodyY, CW, BodyH, radius: true);
        shots = shots.Take(4).ToList();

        var cols = shots.Count == 1 ? 1 : 2;
        var rows = (int)Math.Ceiling(shots.Count / (double)cols);
        var gap = _m.Gap;
        var cellW = (CW - gap * (cols - 1)) / cols;
        var cellH = (BodyH - gap * (rows - 1)) / rows;
        var capH = shots.Any(s => s.Caption.Length > 0) ? 365760 : 0;
        var imgH = cellH - capH;

        var sb = new StringBuilder();
        for (var i = 0; i < shots.Count; i++)
        {
            var c = i % cols; var r = i / cols;
            var x = MX + c * (cellW + gap);
            var y = BodyY + r * (cellH + gap);
            var (p, cap) = shots[i];
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                sb.Append(Picture(ctx.NextId(), AddCoverImage(ctx, p, cellW, imgH), x, y, cellW, imgH, radius: true));
            else
                sb.Append(ImageMissing(el, ctx, p, x, y, cellW, imgH, radius: true));
            if (cap.Length > 0)
                sb.Append(TextBox(ctx.NextId(), x, y + imgH, cellW, capH,
                    Para(cap, 1200, t.Secondary, align: "ctr"), anchor: "ctr"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 半出血图文页（自包含：不经通用标题）。右半页铺满整高图，左侧深色蒙层上放标题与要点。
    /// 适合产品截图、场景图配说明。
    /// </summary>
    private static string ImageBleed(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var imgW = W * 6 / 12;
        var imgX = W - imgW;
        var shapes = new List<string> { Rect(ctx.NextId(), 0, 0, W, H, t.Primary) };
        var path = Str(el, "path");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            shapes.Add(Picture(ctx.NextId(), AddCoverImage(ctx, path!, imgW, H), imgX, 0, imgW, H));
        else
            shapes.Add(ImageMissing(el, ctx, path, imgX, 0, imgW, H, radius: false));

        var textW = imgX - MX * 2;
        var paras = new StringBuilder();
        paras.Append(ParaTitle(Str(el, "title") ?? "", 3400, t.Bg, lineSpacing: 108));
        var items = StringList(el, "bullets");
        if (items.Count == 0) items = StringList(el, "items");
        var text = Str(el, "text");
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(text)) items.Add(text!);
        var plan = items.Select(b => (Text: b, Size: 1600, SpaceBefore: 12, LineSpacing: 1.25)).ToList();
        var scale = ScaleForHeight(plan, textW - _m.Gap, 2743200);
        foreach (var b in items)
            paras.Append(Para(b, Scaled(1600, scale), t.OnPrimary, align: "l", bullet: "•",
                spaceBefore: (int)Math.Round(12 * scale), lineSpacing: (int)Math.Round(125 * scale), marL: (int)_m.Gap));
        shapes.Add(TextBox(ctx.NextId(), MX, 1371600, textW, 4114800, paras.ToString(), anchor: "ctr"));
        shapes.Add(PageBadge(ctx));
        return SlideXml(t.Primary, shapes);
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
        var isScatter = kind == "scatter";
        // 散点图的数据在 series[].points 里，不走 categories+values；不能拿“values 为空”把它拦下去
        if (!isScatter && series.All(s => s.Values.Length == 0))
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
        const int pxW = 1400, pxH = 800;

        // 散点图：入参是 series[].points:[[x,y],…]（或 [{x,y}]），与柱/折线的 categories+values 不同构，
        // 所以在上面的“缺数据”校验之前就先分流出去。
        if (isScatter)
        {
            var pts = ReadPointSeries(el);
            if (pts.Count == 0 || pts.All(p => p.Ys.Length == 0))
                throw new InvalidOperationException("散点图缺少数据：请提供 series[].points，如 [[1,2],[3,4]]。");
            if (wantNative) _nativeFallback = kind;   // 原生散点图的 schema 另有一套，先只出图
            var sparkPng = RenderScatter(pxW, pxH, title, yLabel, Str(el, "xLabel"), pts, dark, t);
            return ImageChartFrame(ctx, sparkPng, caption, availHn);
        }

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

        byte[] png;
        try
        {
            png = kind switch
            {
                "pie" => RenderPie(pxW, pxH, title, categories, series[0].Values, dark, t),
                "doughnut" => RenderPie(pxW, pxH, title, categories, series[0].Values, dark, t, doughnut: true),
                "line" => RenderLine(pxW, pxH, title, yLabel, categories, series, dark, t),
                "radar" => RenderRadar(pxW, pxH, title, categories, series, dark, t),
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
        return ImageChartFrame(ctx, png, caption, availH);
    }

    /// <summary>把渲好的图表 PNG 按可用区域等比居中嵌入，可选下方图注。图片型图表的公共收尾。</summary>
    private static string ImageChartFrame(SlideCtx ctx, byte[] png, string? caption, long availH)
    {
        const int pxW = 1400, pxH = 800;
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
                Para(caption!, 1200, ctx.Theme.Secondary, align: "ctr")));
        return shapes.ToString();
    }

    /// <summary>散点图的数据源：<c>series[].points</c>，支持 <c>[[x,y],…]</c> 与 <c>[{x,y},…]</c> 两种写法。</summary>
    private static List<(string Name, double[] Xs, double[] Ys)> ReadPointSeries(JsonElement el)
    {
        var result = new List<(string Name, double[] Xs, double[] Ys)>();
        if (!el.TryGetProperty("series", out var sv) || sv.ValueKind != JsonValueKind.Array) return result;
        foreach (var one in sv.EnumerateArray())
        {
            if (one.ValueKind != JsonValueKind.Object) continue;
            var name = Str(one, "name") ?? "";
            var xs = new List<double>();
            var ys = new List<double>();
            if (one.TryGetProperty("points", out var pv) && pv.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pv.EnumerateArray())
                {
                    if (p.ValueKind == JsonValueKind.Array)
                    {
                        var pair = p.EnumerateArray().ToList();
                        if (pair.Count >= 2 && pair[0].ValueKind == JsonValueKind.Number && pair[1].ValueKind == JsonValueKind.Number)
                        {
                            xs.Add(pair[0].GetDouble()); ys.Add(pair[1].GetDouble());
                        }
                    }
                    else if (p.ValueKind == JsonValueKind.Object
                        && p.TryGetProperty("x", out var pxe) && pxe.ValueKind == JsonValueKind.Number
                        && p.TryGetProperty("y", out var pye) && pye.ValueKind == JsonValueKind.Number)
                    {
                        xs.Add(pxe.GetDouble()); ys.Add(pye.GetDouble());
                    }
                }
            }
            if (xs.Count > 0) result.Add((name, xs.ToArray(), ys.ToArray()));
        }
        return result;
    }

    /// <summary>
    /// 散点图：X/Y 均为数值轴，直接按数据范围线性映射，不做分类分槽。
    /// 适合“相关性 / 分布”这类表达（分类轴折线图讲不了这个）。
    /// </summary>
    private static byte[] RenderScatter(int w, int h, string? title, string? yLabel, string? xLabel,
        List<(string Name, double[] Xs, double[] Ys)> series, bool dark, Theme t)
    {
        using var img = new Image<Rgba32>(w, h);
        var fg = dark ? ImgColor.White : ImgColor.Black;
        var muted = dark ? ImgColor.FromRgba(170, 178, 190, 255) : ImgColor.FromRgba(90, 90, 90, 255);

        var minX = series.SelectMany(s => s.Xs).DefaultIfEmpty(0).Min();
        var maxX = series.SelectMany(s => s.Xs).DefaultIfEmpty(1).Max();
        var minY = series.SelectMany(s => s.Ys).DefaultIfEmpty(0).Min();
        var maxY = series.SelectMany(s => s.Ys).DefaultIfEmpty(1).Max();
        if (maxX - minX < 1e-9) { maxX = minX + 1; }
        if (maxY - minY < 1e-9) { maxY = minY + 1; }

        const int padR = 36, padB = 76, ticks = 4;
        var tickTexts = new string[ticks + 1];
        for (var i = 0; i <= ticks; i++) tickTexts[i] = (minY + (maxY - minY) * i / ticks).ToString("0.##");
        var tickW = tickTexts.Max(s => MeasureText(s, 13f));
        var padL = (int)Math.Min(200, Math.Max(72, tickW + 30));
        var legendSeries = series.Select(s => (s.Name, s.Ys)).ToList();
        var legend = LayoutLegend(legendSeries, w - padL - padR, 14f, 3);
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

            for (var s = 0; s < series.Count; s++)
            {
                var color = ImgColor.ParseHex(Palette[s % Palette.Length]);
                var xs = series[s].Xs; var ys = series[s].Ys;
                for (var i = 0; i < xs.Length && i < ys.Length; i++)
                {
                    var px = (float)(padL + (w - padL - padR) * ((xs[i] - minX) / (maxX - minX)));
                    var py = (float)(h - padB - (h - padT - padB) * ((ys[i] - minY) / (maxY - minY)));
                    x.Fill(color, new ImgEllipse(new ImgPointF(px, py), 5.5f));
                }
            }
            // X 轴刻度只给首/中/末三个，避免与 Y 轴文案撞车
            if (!string.IsNullOrWhiteSpace(xLabel))
                x.DrawText(FitText(xLabel, 13f, w - padL - padR), Family(13f), muted,
                    new ImgPointF(padL, h - padB + 34));
            for (var i = 0; i <= 2; i++)
            {
                var v = minX + (maxX - minX) * i / 2.0;
                var lab = v.ToString("0.##");
                var px = (float)(padL + (w - padL - padR) * i / 2.0 - MeasureText(lab, 13f) / 2);
                x.DrawText(lab, Family(13f), muted,
                    new ImgPointF(ClampX(px, padL, w - padR - (float)MeasureText(lab, 13f)), h - padB + 10));
            }
        });
        return ToPng(img);
    }

    /// <summary>
    /// 雷达图（蜘蛛图）：<c>categories</c> 是各条轴，每条 series 给一组值。
    /// 适合“多维能力对比”（如几套方案在 5 个维度上的得分）。
    /// </summary>
    private static byte[] RenderRadar(int w, int h, string? title, List<string> cats,
        List<(string Name, double[] Values)> series, bool dark, Theme t)
    {
        using var img = new Image<Rgba32>(w, h);
        var fg = dark ? ImgColor.White : ImgColor.Black;
        var muted = dark ? ImgColor.FromRgba(170, 178, 190, 255) : ImgColor.FromRgba(90, 90, 90, 255);
        var n = Math.Max(3, cats.Count);
        var maxV = Math.Max(0.0001, series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max());

        var legend = LayoutLegend(series, w - 80, 14f, 3);
        var padT = (title is null ? 30 : 66) + legend.Rows * 24;
        var cx = w / 2f;
        var cy = padT + (h - padT - 44) / 2f;
        // 半径同时受宽高限制，并给轴标签留出文字余地
        var radius = Math.Min((w - 120) / 2f, (h - padT - 44) / 2f) * 0.86f;

        ImgPointF At(int i, double frac)
        {
            var ang = -Math.PI / 2 + i * 2 * Math.PI / n;
            return new ImgPointF(cx + (float)(radius * frac * Math.Cos(ang)),
                cy + (float)(radius * frac * Math.Sin(ang)));
        }

        img.Mutate(x =>
        {
            x.Fill(dark ? ImgColor.FromRgba(13, 17, 23, 255) : ImgColor.White);
            if (!string.IsNullOrWhiteSpace(title))
                x.DrawText(FitText(title, 26f, w - 80), Family(26f), fg, new ImgPointF(40, 22));
            // 同心网格 + 轴线
            for (var ring = 1; ring <= 4; ring++)
            {
                var pts = Enumerable.Range(0, n).Select(i => At(i, ring / 4.0)).ToArray();
                x.Draw(muted, 1f, Poly(pts));
            }
            for (var i = 0; i < n; i++) x.DrawLine(muted, 1f, new ImgPointF(cx, cy), At(i, 1.0));

            for (var s = 0; s < series.Count; s++)
            {
                var color = ImgColor.ParseHex(Palette[s % Palette.Length]);
                var vals = series[s].Values;
                var pts = Enumerable.Range(0, n)
                    .Select(i => At(i, Math.Clamp(i < vals.Length ? vals[i] / maxV : 0, 0, 1))).ToArray();
                x.Draw(color, 2.6f, Poly(pts));
                foreach (var p in pts) x.Fill(color, new ImgEllipse(p, 4.5f));
            }

            // 轴标签：放在轴的延长线上，再按边界夹住，避免画到画布外
            for (var i = 0; i < n; i++)
            {
                var lab = FitText(cats[i], 14f, (float)(radius * 0.7));
                if (lab.Length == 0) continue;
                var p = At(i, 1.16);
                var tw = (float)MeasureText(lab, 14f);
                var lx = ClampX(p.X - tw / 2, 8, w - 8 - tw);
                var ly = Math.Clamp(p.Y - 9, 4, h - 24);
                x.DrawText(lab, Family(14f), fg, new ImgPointF(lx, ly));
            }
            if (legend.Rows > 0) DrawLegend(x, legend, 40, padT - legend.Rows * 24f, w - 80, Family(14f), fg, muted);
        });
        return ToPng(img);
    }

    // ---- 进度 / 仪表 ----
    /// <summary>
    /// 进度页：<c>variant</c> = <c>bar</c>（默认，横向进度条）| <c>ring</c>（环形仪表）。
    /// <c>items:[{label, value}]</c>，<c>max</c> 默认为 100（可直接给百分数）。
    ///
    /// <para>
    /// 对应设计文档里的 “SVG bar / progress / ring” —— 进度、完成度、占比这类表达，
    /// 用数字卡片（kpi）说不清“已走到哪”，用条形/环形才直观。
    /// </para>
    /// </summary>
    private static string ProgressBody(JsonElement el, SlideCtx ctx)
    {
        return VariantOf(el, "bar") is "ring" or "donut" or "gauge"
            ? ProgressRing(el, ctx)
            : ProgressBar(el, ctx);
    }

    private static List<(string Label, double Value)> ProgressItems(JsonElement el)
    {
        var items = new List<(string Label, double Value)>();
        if (el.TryGetProperty("items", out var iv) && iv.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in iv.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                var label = Str(it, "label") ?? "";
                var value = 0.0;
                if (it.TryGetProperty("value", out var vv))
                {
                    if (vv.ValueKind == JsonValueKind.Number) value = vv.GetDouble();
                    else if (vv.ValueKind == JsonValueKind.String
                        && double.TryParse(vv.GetString()?.Trim().TrimEnd('%'), out var parsed)) value = parsed;
                }
                if (label.Length == 0 && value == 0) continue;
                items.Add((label, value));
            }
        }
        return items;
    }

    private static string ProgressBar(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var items = ProgressItems(el);
        if (items.Count == 0) return BulletBody(el, ctx, accent: false);
        var max = Math.Max(1e-6, NumOf(el, "max", 100));

        var rowH = Math.Min(BodyH / items.Count, 1143000);
        var totalH = rowH * items.Count;
        var y0 = BodyY + Math.Max(0, (BodyH - totalH) / 2);
        var labelW = CW * 30 / 100;
        var valueW = 1000000L;
        var barX = MX + labelW + _m.Gap;
        var barW = CW - labelW - valueW - _m.Gap * 2;
        var barH = Sz(190500);

        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var y = y0 + i * rowH;
            var frac = Math.Clamp(items[i].Value / max, 0, 1);
            var trackY = y + (rowH - barH) / 2;
            sb.Append(TextBox(ctx.NextId(), MX, y, labelW, rowH,
                Para(items[i].Label, 1600, t.Text, align: "l", lineSpacing: 120), anchor: "ctr"));
            sb.Append(Rect(ctx.NextId(), barX, trackY, barW, barH, t.Light, radius: true));
            var fillW = (long)(barW * frac);
            if (fillW > 0) sb.Append(Rect(ctx.NextId(), barX, trackY, Math.Max(fillW, barH), barH, t.Accent, radius: true));
            sb.Append(TextBox(ctx.NextId(), barX + barW + _m.Gap, y, valueW, rowH,
                Para(FormatProgress(items[i].Value), 1600, t.Primary, bold: true, align: "r"), anchor: "ctr"));
        }
        var caption = Str(el, "caption");
        if (!string.IsNullOrWhiteSpace(caption)) sb.Append(Footnote(caption!, ctx));
        return sb.ToString();
    }

    private static string ProgressRing(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var items = ProgressItems(el).Take(4).ToList();
        if (items.Count == 0) return BulletBody(el, ctx, accent: false);
        var max = Math.Max(1e-6, NumOf(el, "max", 100));

        // 环形：内容区居中排一行，每个环下面跟一个标签
        var labelH = 685800L;
        var d = Math.Min((BodyH - labelH), CW / items.Count - _m.Gap);
        var cellW = CW / items.Count;
        var ringPx = 320;
        var dark = IsDarkBg(t);
        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var frac = Math.Clamp(items[i].Value / max, 0, 1);
            var x = MX + cellW * i + (cellW - d) / 2;
            var y = BodyY + Math.Max(0, (BodyH - labelH - d) / 2);
            var png = RenderRing(ringPx, frac, dark, t);
            sb.Append(Picture(ctx.NextId(), ctx.AddImage(png), x, y, d, d));
            sb.Append(TextBox(ctx.NextId(), MX + cellW * i, y + d + Sz(68580), cellW, labelH,
                Para(FormatProgress(items[i].Value), 1800, t.Primary, bold: true, align: "ctr", lineSpacing: 110)
                + Para(items[i].Label, 1400, t.Secondary, align: "ctr", spaceBefore: 4, lineSpacing: 115),
                anchor: "t"));
        }
        var caption = Str(el, "caption");
        if (!string.IsNullOrWhiteSpace(caption)) sb.Append(Footnote(caption!, ctx));
        return sb.ToString();
    }

    private static string FormatProgress(double v)
        => Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0") + "%" : v.ToString("0.#") + "%";

    /// <summary>
    /// 画一个环形进度（透明底 PNG）：底环 + 按比例的前景弧。
    /// 弧用多边形逼近（每 3° 一段），与饼图同一个理由——ImageSharp.Drawing 没有 DrawArc，
    /// 而 PathBuilder.AddArc 的语义很容易用错（详见 README 的“几何坑”）。
    /// </summary>
    private static byte[] RenderRing(int px, double frac, bool dark, Theme t)
    {
        using var img = new Image<Rgba32>(px, px);
        var track = ImgColor.ParseHex(BareHex(dark ? "2A3242" : t.Light));
        // 前景色用主题强调色（调色板已过可读性守卫，深色底上也是亮的）
        var fg = ImgColor.ParseHex(BareHex(t.Accent));
        var thick = px * 0.11f;
        var r = px * 0.40f;
        var mid = px / 2f;

        img.Mutate(x =>
        {
            x.Draw(track, thick, new ImgEllipse(new ImgPointF(mid, mid), r));
            if (frac <= 0.001) return;
            if (frac >= 0.999)
            {
                x.Draw(fg, thick, new ImgEllipse(new ImgPointF(mid, mid), r));
                return;
            }
            // 按比例描出一段弧（保持中心透明）
            var steps = Math.Max(6, (int)(frac * 120));
            var pts = new ImgPointF[steps + 1];
            for (var i = 0; i <= steps; i++)
            {
                var ang = -Math.PI / 2 + 2 * Math.PI * frac * i / steps;
                pts[i] = new ImgPointF(mid + (float)(r * Math.Cos(ang)), mid + (float)(r * Math.Sin(ang)));
            }
            x.Draw(fg, thick, PolyOpen(pts));
        });
        return ToPng(img);
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

    private static string Picture(int id, string relId, long x, long y, long cx, long cy, bool radius = false)
    {
        var sb = new StringBuilder();
        sb.Append("<p:pic><p:nvPicPr><p:cNvPr id=\"").Append(id).Append("\" name=\"Picture ").Append(id).Append("\"/>").Append("<p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>")
          .Append("<p:blipFill><a:blip r:embed=\"").Append(relId).Append("\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>")
          .Append("<p:spPr><a:xfrm><a:off x=\"").Append(x).Append("\" y=\"").Append(y)
          .Append("\"/><a:ext cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm>");
        if (radius)
            sb.Append("<a:prstGeom prst=\"roundRect\"><a:avLst><a:gd name=\"adj\" fmla=\"val ").Append(_m.Radius)
              .Append("\"/></a:avLst></a:prstGeom>");
        else
            sb.Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>");
        sb.Append("</p:spPr></p:pic>");
        return sb.ToString();
    }

    /// <summary>
    /// 把图片裁成目标框比例（cover 语义）后嵌为 PNG，返回关系 id。
    ///
    /// <para>
    /// 为何不在 PPT 里拉伸：<c>blipFill/a:stretch</c> 会把图拉满给定矩形，
    /// 目标比例与原图不同时就会<b>变形</b>（半出血图、封面背景图都是非等比框）。
    /// 服务端先按 cover 裁一刀，观感才对，也避免在 PPT 里配 <c>srcRect</c> 那套繁琐计算。
    /// </para>
    /// </summary>
    private static string AddCoverImage(SlideCtx ctx, string path, long boxW, long boxH)
    {
        using var img = Image.Load(path);
        var target = (double)boxW / Math.Max(1, boxH);
        var w = img.Width; var h = img.Height;
        var cur = (double)w / h;
        var crop = cur > target
            ? new Rectangle((w - (int)Math.Round(h * target)) / 2, 0, Math.Max(1, (int)Math.Round(h * target)), h)
            : new Rectangle(0, (h - (int)Math.Round(w / target)) / 2, w, Math.Max(1, (int)Math.Round(w / target)));
        // 限像素规模：超高分辨率原图会让 pptx 变得很大，而幻灯片并不需要那么多像素
        const int maxW = 1920;
        var outW = Math.Min(maxW, crop.Width);
        var outH = Math.Max(1, (int)Math.Round(outW * (double)crop.Height / crop.Width));
        using var ms = new MemoryStream();
        img.Clone(x => x.Crop(crop).Resize(outW, outH)).SaveAsPng(ms);
        return ctx.AddImage(ms.ToArray());
    }

    /// <summary>标题段落：走主题的标题字体（fontTitle）。</summary>
    private static string ParaTitle(string text, int sz, string color, string align = "l", int lineSpacing = 0, int alpha = 100)
        => Para(text, sz, color, bold: true, align: align, font: _currentTheme?.FontTitle, lineSpacing: lineSpacing, alpha: alpha);

    private static string Para(string text, int sz, string color, bool bold = false, string align = "l",
        string? bullet = null, string? font = null, int spaceBefore = 0,
        int lineSpacing = 0, int marL = 0, int alpha = 100)
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
        sb.Append(Run(text, sz, color, bold, font, alpha));
        sb.Append("</a:p>");
        return sb.ToString();
    }

    private static string Run(string text, int sz, string color, bool bold, string? font, int alpha = 100)
    {
        // font 未显式指定时，落到“当前主题的正文字体”——
        // 这样 json 里的 fontBody 覆盖才会真正生效（实测踩到：解析了主题却没用到）。
        var latin = Xml(font ?? _currentTheme?.FontBody ?? "微软雅黑");
        // 东亚字体单独给：否则配了 Georgia 这类拉丁字体时，汉字会整段落到 fallback（甚至缺字）。
        var ea = Xml(_currentTheme?.FontCjk ?? "微软雅黑");
        // 透明度只能用 a:alpha 子元素，不能在色值里编（与 pitfalls.md 的约束一致）
        var fill = alpha >= 100
            ? Rgb(color)
            : "<a:srgbClr val=\"" + BareHex(color) + "\"><a:alpha val=\"" + alpha * 1000 + "\"/></a:srgbClr>";
        return "<a:r><a:rPr lang=\"zh-CN\" altLang=\"en-US\" sz=\"" + sz + "\" b=\"" + (bold ? 1 : 0) + "\" dirty=\"0\">"
             + "<a:solidFill>" + fill + "</a:solidFill>"
             + "<a:latin typeface=\"" + latin + "\"/><a:ea typeface=\"" + ea + "\"/>"
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
          .Append("<a:ea typeface=\"").Append(Xml(t.FontCjk)).Append("\"/><a:cs typeface=\"\"/></a:majorFont>")
          .Append("<a:minorFont><a:latin typeface=\"").Append(Xml(t.FontBody)).Append("\"/>")
          .Append("<a:ea typeface=\"").Append(Xml(t.FontCjk)).Append("\"/><a:cs typeface=\"\"/></a:minorFont>")
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
