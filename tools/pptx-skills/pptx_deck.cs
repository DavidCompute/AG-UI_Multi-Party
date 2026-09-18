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
//     "theme": "business|tech|warm|minimal|dark|vivid",   // 可选，历史主题（老面孔，样式弱）
//              // 或 18 套命名调色板（推荐，按场景挑），详见下方 PALETTES
//              // 不写 = business-authority（命名调色板里最稳的一套）
//     "style": "sharp|soft|rounded|pill",                  // 可选，版式风格，默认 soft
//     "fontPair": "georgia-calibri",                       // 可选，命名字体配对（只换拉丁字面）
//     "fontCjk": "微软雅黑",                                // 可选，东亚字体（汉字走它）
//     "titleRule": true,                                   // 可选，加上“标题下强调线”（默认不加）
//     "action": "read", "path": "…pptx",                  // 可选：只读取既有 pptx 的文本，不生成文件
//     "action": "qa", "path": "…pptx",                    // 可选：只自检（占位符/空页/只有标题/越界/文字放不进框）
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
//     content  要点页    title, bullets:[ "…" ], note（要点过多自动分页，不丢条）
//     twoCol   两栏      title, left:{heading,bullets:[…]}, right:{heading,bullets:[…]}
//     table    表格      title, headers:[…], rows:[[…]]（过长自动分页，不丢行）
//     kpi      指标卡    title, items:[ {value,label} ]
//     stats    大数字    title, items:[ {value,label} ], cols
//     progress 进度/仪表 title, items:[ {label,value} ], max(默认 100),
//                        variant: bar(默认，横向进度条) | ring(环形仪表)
//     pyramid  金字塔    title, items:[ {title,text} ]（3~6 层，顶层最窄）
//                        适合：分层策略 / 成熟度模型 / 价值层级
//     funnel   漏斗      title, items:[ {title,text} ]（3~6 层，顶层最宽；text 放数值）
//                        适合：转化率 / 逐步筛选
//     matrix   四象限    title, xTitle, yTitle, xLeft, xRight, items:[左上,右上,左下,右下]
//                        适合：优先级 / 取舍 / 分类
//     cycle    循环闭环  title, center, items:[ {title,text} ]（3~6 步）
//                        适合：迭代 / PDCA / 闭环流程
//     stack    层叠架构  title, items:[ {title,text} ]
//                        适合：技术架构 / 能力分层
//     hero     自动题图  title, subtitle（整页程序生成的抽象图）
//     credits  图片来源  title, items:[…]（用了联网照片时自动追加；见上方「照片」一节）
//     grid     网格卡    title, items:[ {title,text} ], cols:2|3
//     timeline 时间轴    title, items:[ {title,detail} ]（最多 6 步）
//     iconRows 图标行    title, items:[ {icon,title,text} ]（最多 6 行）
//     quote    引言      text, cite
//     image    配图      title, path（本地文件）或 imageQuery（联网检索）, caption
//                        variant: full(默认) | left(图左文右) | right(文左图右) |
//                                 bleed(半出血+叠字，自包含标题) | gallery(images:[{path,caption}] 2~4 张)
//                        left/right/bleed 可配 heading/bullets 写文字侧
//                        **图片缺失/未提供时自动生成题图，并报 warnings**
//     chart    图表      title, chartType:"bar|line|pie|doughnut|scatter|radar", categories:[…],
//                        series:[ {name,values:[…]} ], yLabel, xLabel, caption
//                        散点图：series:[ {name,points:[[x,y],…]} ]
//                        chartType 加 "-native" 后缀 → 生成原生可编辑图表（DrawingML ChartPart）
//                        支持 bar/line/pie（其余会自动降级为图片并在返回里说明）
//     summary  小结      title, bullets:[…]
//                        variant: list(默认) | cta(items:[行动项], contact) | split(bullets+actions+contact)
//     end      结束页    title, subtitle
//   content 还可用 "layout":"timeline|grid|stats|iconRows|progress|pyramid|funnel|matrix|cycle|stack" 指定子类型。
//   任何页都可带 "notes"（备注文字），写入演讲者备注。
//
//   【插图：不需要用户提供任何素材】
//     一类是**示意图**（pyramid / funnel / matrix / cycle / stack）：用 DrawingML 预设几何
//       （trapezoid / rect / ellipse / triangle / parallelogram）把“分层·收敛·取舍·闭环·架构”
//       这些**关系**画出来 —— 商务稿里最常被叫做“插图”的其实是这个；
//     另一类是**程序化题图**（hero 页型，以及 image/cover 缺图时的降级）：按主题配色生成一张
//       抽象图，零素材、零联网、无版权问题，且**确定性**（同一标题每次生成的图一样）。
//     设计约束：只用实色（无渐变）、透明度只用 a:alpha，颜色全部取自动调色板。
//
//   【照片：用 imageQuery 联网取图（Wikimedia Commons）】
//     要用**真照片**（而不是示意图/题图）时，在需要图的位置写：
//       "imageQuery": "modern office meeting room"      // 检索关键词
//     凡是有图的位置都支持它：cover(variant image/split) / section(variant full) /
//       image(全部版式，含 gallery 的每张) / content。给了 path 就用 path（本地文件优先），
//       没有 path 才去检索。
//     content 页带 imageQuery（或 path）时**自动变成“文左图右”**，所以不必为了配图改用别的页型。
//
//   【配图顺序：先团队图库，再网络（不依赖外网的路子）】
//     平台有「图库」时，会在调用本技能前注入一个检索范围句柄（入参 imageScopeId），
//     技能据此回调 /ag-ui/images/search 做语义检索：
//       ① 命中 → 直接用平台返回的**本地文件路径**嵌入（自有素材，**不需要 CC 署名**）；
//       ② 未命中 → 回落 Wikimedia Commons（CC 素材，会附「图片来源」页）；
//       ③ 都没有 → 退回自动生成的题图（不假称有图）。
//     imageSource 控制走哪条路（入参优先，其次环境变量 AGUI_IMAGE_SOURCE）：
//       auto（默认）/ library（仅图库，**彻底不出网**，内网部署用这个）/ network（仅网络）。
//
//   【照片：关键词怎么写（实测结论，不是猜的）】
//     Commons 是**档案库**而不是商业图库，检索质量几乎全看关键词：
//       · 用 **2~4 个能看得见的具体名词**：“modern office meeting room” / “glass office building” /
//         “handshake business” / “city skyline sunset” —— 实测前两个都直接顶到 Unsplash 导入的 CC0 会议室、
//         以及真正的现代玻璃幕墙楼；
//       · **不要用抽象词/动词**（teamwork / collaboration / together / growth）：
//         实测 “teamwork” 的头两条是 Teamwork-icon.jpg 与 Teamwork.com-Logo-200.png；
//         “business people working together” 的第一条是 1920 年书里的插图；
//       · 词太多会**搜不到结果**：五个词以上的 “office desk laptop notebook business” 返回空。
//     代码侧的兵庖（都要有，因为关键词拦不住所有坏命中）：
//       ① 宽 < 1200px 跳过（800px 铺满一页明显发虚）；
//       ② 长宽比超出 0.55~2.2 跳过 —— 定框裁切后全景图只剩中间一条；
//       ③ 标题含 logo / icon / wordmark / flag of 的跳过；
//       ④ 分类含书刊扫描（Internet Archive Book Images 等）的跳过；
//       ⑤ 许可含 non-free / fair use 的跳过；
//       ⑥ 限流（HTTP 429 / 服务不可用）退避 2 秒重试一次。
//
//   【出稿后自检】生成/编辑完会自动跑一遍 QA（占位符、空页、只有标题、形状越界、
//   **文字放不进自己的框**），结果在返回 JSON 的 qa 字段；降级行为（如图片缺失改用占位块、
//   文字过多已截断）在 warnings 里。两者都是“不静默降级”的产物：调用方应看它们，
//   别直接把有问题的稿子交出去。
//
//   【文字溢出：为什么这件事要自己算】
//     每页的标题与正文都按**真实字形量宽 + 真实行高**估行数，装不下就缩字号；
//     缩到下限（12pt）仍装不下时：要点页（content / summary·list·split / toc）**自动分页**，
//     表格过长按行切页，结构固定的框（卡片 / 示意图层 / 封面 / 图注）**截断 + Warn**。
//     不依赖 <a:normAutofit/>：LibreOffice 会替我们缩、PowerPoint 打开时却不会重算，
//     依赖它就会出现“我方看着正常、用户那边溢出”。详见 README 的「文字溢出」一节。
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
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// 未指定 <c>theme</c> 时的默认调色板。
    ///
    /// <para>
    /// 以前默认落到历史主题 business（白底 + 金强调）：那是改造前的老面孔，
    /// 而且金色在白底上对比度只有 2.41——强调线/徽标本来就弱。改成一整套命名调色板后，
    /// 默认出稿的层级（近白底 + 深藏青标题 + 鲜明红强调 + 石板蓝卡片）明显更好。
    /// <b>显式写 theme:"business" 仍然得到原来那套</b>（历史主题的观感不变）。
    /// </para>
    /// </summary>
    private const string DefaultTheme = "business-authority";

    private static Theme ResolveTheme(JsonElement root)
    {
        var requested = (Str(root, "theme") ?? DefaultTheme).Trim().ToLowerInvariant();
        var t = LegacyTheme(requested)
            ?? FromPalette(requested)
            ?? FromPalette(DefaultTheme)!;
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
    /// 最花=accent（强调线/徽标），light=卡片面（见 <see cref="CardSurface"/>），
    /// secondary 取 primary 的中间调以形成层级。
    ///
    /// <para>
    /// 最后统一过一道<b>可读性兜底</b>：调色板里难免有“很浅的次要色”或“很亮的黄”，
    /// 直接用会出现看不清的字。宁可把颜色调暗/调亮，也不能出现看不清——这条由
    /// 单测 <c>PptxDeckSkillTests.AllPalettes_KeepTextReadable</c> 对 18 套逐对断言。
    /// </para>
    ///
    /// <para>
    /// <b>为何调色都在 HSL 空间做</b>：原先的可读性兜底是“往黑/白里混”（sRGB 线性混色），
    /// 会把鲜艳色洗成土色 —— 实测 vibrant-orange-mint 的 FF9F1C（鲜明橙）被洗成 CC7F16（脏芥末），
    /// modern-wellness 的 E29578 被洗成 B57760（灰褐）。现在只改 HSL 的亮度、保色相与饱和（必要时抬饱和），
    /// 调出来是“同色相更深/更浅的一版”，而不是另一种脏颜色。
    /// </para>
    /// </summary>
    private static Theme DeriveTheme(string c1, string c2, string c3, string c4, string c5, bool dark)
    {
        var all = new[] { c1, c2, c3, c4, c5 }.Select(Normalize).ToArray();
        var byLum = all.OrderBy(RelLum).ToArray(); // 暗 → 亮
        var t = new Theme();
        if (dark)
        {
            t.Bg = byLum[0];
            t.Primary = byLum[4];
            t.Text = "E8ECF1";
            // 深色主题的“面”：调色板里比底色亮一点、又还在暗部的那个（如 tech-night 的 003566），
            // 没有就按主色合成一层暗调。
            // 注：这里不能用 Chroma 筛（深色算出来的彩度几乎都是 1.0，筛不出东西）。
            t.Light = all.Where(c => c != t.Bg && c != t.Primary && RelLum(c) <= 0.20)
                         .OrderByDescending(RelLum).FirstOrDefault()
                      ?? FromHsl(Hue(t.Primary), Math.Clamp(ToHsl(t.Primary).S * 0.35, 0.06, 0.30), 0.15);
        }
        else
        {
            // 底色必须是“面”，不能是个饱和色：实测 education-charts 的最亮色是
            // E9C46A（亮黄），直接当底色会做出一张黄底幻灯片。这里把彩度压下来。
            t.Bg = Surface(byLum[4]);
            // 标题色宁深勿浅（7:1 是正文级 AAA），但不把鲜艳色洗成灰
            t.Primary = EnsureContrastHsl(byLum[0], t.Bg, 7.0, minSat: 0.22);
        }

        // 强调色：在底色上看得见 + 能承载小字 + 与主色分得开（三条底线，见 PickAccent）
        t.Accent = PickAccent(all, t.Bg, t.Primary);
        // 卡片 / 面板底：与底色同族的一层“面”，不能拿调色板里随便一个中间调
        if (!dark) t.Light = CardSurface(all, t);
        // 副标题 / 次要标签：主色的干净中间调
        t.Secondary = SecondaryOf(t);
        // 正文色：跟主色同一色相的近黑（不是死灰），再保证 7:1
        t.Text = EnsureContrastHsl(BodyInk(t), t.Bg, 7.0);

        t.OnAccent = OnColor(t.Accent, t.Bg);
        t.OnPrimary = EnsureContrast(t.Light, t.Primary, 3.0);
        return t;
    }

    /// <summary>
    /// 卡片 / 面板底：**不能是调色板里随便一个中间调**。
    ///
    /// <para>
    /// 实测：education-charts 的次亮色是 F4A261（橙）、art-food 是 E09F3E（琥珀）、
    /// nature-outdoors 是 DDA15E（茶色）—— 直接拿去铺卡片，整份稿子就是一片橙/茶色色块，观感很廉价。
    /// 所以只收“像个面”的候选：彩度够低（≤0.30）且与底色能看出层次（≥1.08）；
    /// 一个都没有（或层次太弱）就按主色合成一层淡调（保住色相、压到 5~14% 饱和）。
    /// </para>
    /// </summary>
    private static string CardSurface(string[] all, Theme t)
    {
        var picked = all.Where(c => c != t.Bg && c != t.Primary && c != t.Accent)
            .Where(c => Chroma(c) <= 0.30 && Contrast(c, t.Bg) >= 1.08)
            .OrderByDescending(RelLum).FirstOrDefault();
        if (picked is { } p) return p;
        var (h, s, _) = ToHsl(t.Primary);
        var tint = s <= 0.06
            ? FromHsl(0, 0, 0.95)
            : FromHsl(h, Math.Clamp(s * 0.26, 0.05, 0.14), 0.95);
        return Contrast(tint, t.Bg) >= 1.08 ? tint : Mix(t.Bg, t.Primary, 0.10);
    }

    /// <summary>副标题 / 次要标签色：主色的“干净中间调”（保色相、亮度提到中间），并保证在底色上 ≥3。</summary>
    private static string SecondaryOf(Theme t)
    {
        var (h, s, _) = ToHsl(t.Primary);
        if (s <= 0.06) return EnsureContrastHsl(Mix(t.Primary, t.Bg, 0.35), t.Bg, 3.0);
        return EnsureContrastHsl(FromHsl(h, Math.Clamp(s * 0.90, 0.20, 0.65), 0.48), t.Bg, 3.0, minSat: 0.20);
    }

    /// <summary>正文色：跟主色同一色相的近黑（H 不动、饱和压到 5~12%）。比死板的 333333 更有主题感，又不抢标题。</summary>
    private static string BodyInk(Theme t)
    {
        var (h, s, _) = ToHsl(t.Primary);
        return s <= 0.06 ? "1F1F1F" : FromHsl(h, Math.Clamp(s * 0.35, 0.05, 0.12), 0.16);
    }

    /// <summary>
    /// 挑强调色：必须同时满足三条 —— ① 在底色上看得见（≥3）；② 能承载小字（黑或白 ≥4.5，页码徽标就是小字）；
    /// ③ 与主色分得开（≥2.2，否则强调线/序号圆就糊在主色里）。
    ///
    /// <para>
    /// 优先用调色板里“色相与主色拉开 40° 以上”的颜色（按艳度排序）；都不合格就退到主色的补色，
    /// 保证任意一套调色板都能得到一条看得见的强调线。
    /// </para>
    ///
    /// <para>
    /// 为何不分开做“先保证可见、再保证承载字”：实测踩到 —— 只为了“黑字能读”把强调色提亮，
    /// 它在浅底色上直接消失了（nature-outdoors 的琥珀被提成 E9BA90，对底色对比只剩 1.68）。
    /// 三条必须<b>同时</b>成立，且只做最小改动。
    /// </para>
    /// </summary>
    private static string PickAccent(string[] all, string bg, string primary)
    {
        var pool = all.Where(c => c != bg && c != primary).ToList();
        if (pool.Count == 0) pool = all.ToList();
        var ph = Hue(primary);
        // 先看“色相与主色拉开”再看艳度，但**不是第一个合格就用**：
        // 实测踩到 vintage-academic：最深的 780000 稍徽改一点就合格（990000），
        // 于是拿到一个与深蓝主色几乎一样暗的暗红斑；而稍不艳一点的 C1121F 明显更好看。
        // 所以把所有“能修合格”的候选都评一遍，取艳度高、与主色分得开的那个。
        var ordered = pool
            .OrderByDescending(c => (HueDistance(Hue(c), ph) >= 40 ? 1 : 0) * 100 + Chroma(c) * 60)
            .ToList();
        string? bestAccent = null;
        var bestScore = double.MinValue;
        foreach (var c in ordered)
        {
            var fixedAccent = FixAccent(c, bg, primary);
            if (!IsUsableAccent(fixedAccent, bg, primary)) continue;
            var score = Vividness(fixedAccent)
                + Math.Min(Contrast(fixedAccent, primary), 6.0) * 0.10
                // 同色相但明暗不同也算“看得出区别”，但不如色相拉开：所以给“色相拉开”更高的权重，
                // 否则会出现“青底青强调色”这种把调色板的点色丢掉的结果（modern-wellness / coastal-coral）。
                + (HueDistance(Hue(fixedAccent), ph) >= 40 ? 0.25 : 0);
            if (score > bestScore) { bestScore = score; bestAccent = fixedAccent; }
        }
        if (bestAccent is not null) return bestAccent;
        // 兜底：主色补色（色相 +150°、高饱和），保证与主色一定分得开
        var l = ToHsl(primary).L < 0.5 ? 0.58 : 0.42;
        return FixAccent(FromHsl(ph + 150, 0.72, l), bg, primary);
    }

    /// <summary>艳度（0~1）：既远离灰、也远离纯黑/纯白 —— 中亮度的饱和色最像“点色”。</summary>
    private static double Vividness(string hex)
    {
        var (_, s, l) = ToHsl(hex);
        return s * (1 - Math.Abs(2 * l - 1));
    }

    /// <summary>强调色的三条底线是否同时成立。</summary>
    private static bool IsUsableAccent(string accent, string bg, string primary)
        => Contrast(accent, bg) >= 3.0
        && Math.Max(Contrast(accent, "FFFFFF"), Contrast(accent, "1A1A1A")) >= 4.5
        && AccentSeparated(accent, primary);

    /// <summary>
    /// 强调色与主色是否“看得出不是同一个色”：明暗拉开 2.2 以上；
    /// 色相已经拉开 40° 以上时 1.5 就够（一条蓝强调线在深橄榄主色上，本来就认得出来）。
    /// </summary>
    private static bool AccentSeparated(string accent, string primary)
    {
        var c = Contrast(accent, primary);
        return c >= 2.2 || (HueDistance(Hue(accent), Hue(primary)) >= 40 && c >= 1.5);
    }

    /// <summary>
    /// 把颜色微调成合格强调色：保色相、只动亮度（必要时抬饱和）。
    ///
    /// <para>
    /// 亮度按 <b>1% 细扫</b>、取“改动最小”的合格解：实测粗步长（4%）会直接跨过那唯一的可行区间，
    /// 于是退到补色，出现“深蓝底上的荧光绿”这种离谱强调色（soft-creative：底色偏浅又压着极深的主色，
    /// 可行区间只剩很窄一段）。一个合格解都没有时，取“违规最小”的那个，而不是原样返回一个
    /// 对底色几乎不可见的颜色。
    /// </para>
    /// </summary>
    private static string FixAccent(string color, string bg, string primary)
    {
        if (IsUsableAccent(color, bg, primary)) return color;
        var (h, _, l) = ToHsl(color);
        // 目标饱和度从**感知彩度**（max-min/max）推，而不是直接用 HSL 饱和度：
        // 浅色（如 FFDDD2）的 HSL 饱和度是 1.0，照它降亮度会把一个柔和色调变成荧光橙。
        // 这样鲜艳的源色仍然鲜艳、柔和的源色仍然柔和（各套调色板的“性格”得以保留）。
        var sat = Math.Clamp(Chroma(color) * 1.15, 0.35, 0.95);
        string? exact = null;    // 完全合格、且改动最小的解
        string? best = null;     // 没有合格解时“违规最小”的退而求其次
        var exactShift = double.MaxValue;
        var bestShift = double.MaxValue;
        var bestViolation = double.MaxValue;
        for (var tgt = 0.08; tgt <= 0.921; tgt += 0.01)
        {
            var cand = FromHsl(h, sat, tgt);
            var shift = Math.Abs(tgt - l);
            if (IsUsableAccent(cand, bg, primary))
            {
                if (shift < exactShift) { exactShift = shift; exact = cand; }
                continue;
            }
            var v = AccentViolation(cand, bg, primary);
            if (v < bestViolation - 1e-9 || (Math.Abs(v - bestViolation) <= 1e-9 && shift < bestShift))
            { bestViolation = v; bestShift = shift; best = cand; }
        }
        return exact ?? best ?? color;
    }

    /// <summary>强调色违规量（三条底线差多少）：用于“找不到完全合格解时选最接近的那个”。</summary>
    private static double AccentViolation(string accent, string bg, string primary)
    {
        var v = Math.Max(0, 3.0 - Contrast(accent, bg));
        v += Math.Max(0, 4.5 - Math.Max(Contrast(accent, "FFFFFF"), Contrast(accent, "1A1A1A")));
        var sep = Contrast(accent, primary);
        var sepOk = AccentSeparated(accent, primary);
        if (!sepOk) v += Math.Max(0, (HueDistance(Hue(accent), Hue(primary)) >= 40 ? 1.5 : 2.2) - sep);
        return v;
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

    /// <summary>把 fg 朝黑/白方向调，直到与 bg 的对比度达标（WCAG）。
    /// 注：这是 sRGB 线性混色，会把鲜艳色洗淡；<b>调色板路径一律用 <see cref="EnsureContrastHsl"/></b>，
    /// 这个只保留给历史主题与 themeColors 覆盖用（观感要与改造前一致）。</summary>
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

    // ---- 颜色工具（HSL：改亮度但保色相/饱和 —— sRGB 混色会把鲜艳色洗成土色）----

    /// <summary>HEX → HSL（H 0~360，S/L 0~1）。灰阶 H 返回 0。</summary>
    private static (double H, double S, double L) ToHsl(string hex)
    {
        var c = RgbOf(hex);
        double r = c[0] / 255.0, g = c[1] / 255.0, b = c[2] / 255.0;
        var mx = Math.Max(r, Math.Max(g, b));
        var mn = Math.Min(r, Math.Min(g, b));
        var l = (mx + mn) / 2.0;
        var d = mx - mn;
        if (d < 1e-9) return (0, 0, l);
        var s = l > 0.5 ? d / (2.0 - mx - mn) : d / (mx + mn);
        double h;
        if (mx == r) h = ((g - b) / d) % 6;
        else if (mx == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
        return (h, s, l);
    }

    /// <summary>HSL → HEX（各分量自动夹到合法区间）。</summary>
    private static string FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        var m = l - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        string B(double v) => ((int)Math.Round(Math.Clamp(v + m, 0, 1) * 255)).ToString("X2");
        return B(r) + B(g) + B(b);
    }

    /// <summary>
    /// 只调亮度（保色相/饱和）直到与底色的对比度达标 —— 调出来是“同色相更深/更浅的一版”。
    ///
    /// <para>
    /// <paramref name="minSat"/> 只在原色<b>本来就带色</b>时抬高饱和：否则给纯黑/纯灰（如
    /// platinum-white-gold 的 0A0A0A）硬加饱和色相，会把“黑白金”那套的标题染成暗红。
    /// </para>
    /// </summary>
    private static string EnsureContrastHsl(string fg, string bg, double min, double minSat = 0)
    {
        if (Contrast(fg, bg) >= min) return fg;
        var (h, s, l) = ToHsl(fg);
        if (s > 0.06 && s < minSat) s = minSat;
        var dir = RelLum(bg) > 0.5 ? -1 : 1;   // 底色亮 → 把前景压暗；底色暗 → 往前景抬亮
        for (var step = 1; step <= 24; step++)
        {
            var nl = l + dir * step * 0.035;
            if (nl < 0 || nl > 1) break;
            var cand = FromHsl(h, s, nl);
            if (Contrast(cand, bg) >= min) return cand;
        }
        return dir < 0 ? "000000" : "FFFFFF";
    }

    /// <summary>
    /// 强调色要承载小字（页码徽标）：黑或白至少一个达到 min。
    /// 在 HSL 里试几个亮度（先试“变浅”——深色字压上去更清楚），而不是 sRGB 混黑。
    /// </summary>
    private static string EnsureReadableUnderHsl(string color, double min)
    {
        double Best(string c) => Math.Max(Contrast(c, "FFFFFF"), Contrast(c, "1A1A1A"));
        if (Best(color) >= min) return color;
        var (h, s, _) = ToHsl(color);
        foreach (var tgt in new[] { 0.74, 0.68, 0.80, 0.60, 0.54, 0.48, 0.42, 0.36, 0.30 })
        {
            var c = FromHsl(h, Math.Max(s, 0.55), tgt);
            if (Best(c) >= min) return c;
        }
        return EnsureReadableUnder(color, min);
    }

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
                // 用到的联网照片：署名依据（也可让调用方自行在正文里再标一次）。
                // 只要有照片，稿末就会多一页「图片来源」——那是许可要求，不是可选渲染。
                + ",\"images\":[" + string.Join(",", Photos.Select(p =>
                    "{\"query\":" + Js(p.Query) + ",\"source\":" + Js(p.Source) + ",\"title\":" + Js(p.FileName)
                    + ",\"library\":" + Js(p.LibraryName) + ",\"caption\":" + Js(p.Caption)
                    + ",\"score\":" + p.Score.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"author\":" + Js(p.Author) + ",\"license\":" + Js(p.License)
                    + ",\"page\":" + Js(p.PageUrl) + "}")) + "]"
                // 原生图表用量与降级原因：调了却没用上必须说清楚，不能静默降级
                + ",\"nativeCharts\":" + _nativeCharts
                + ",\"nativeChartFallback\":" + (_nativeFallback is null ? "null" : Js(_nativeFallback))
                + ",\"message\":" + Js("已生成演示文稿：" + built.Path
                    + (LibraryPhotoCount > 0 ? "（其中 " + LibraryPhotoCount + " 张来自团队图库）" : "")
                    + (NetworkPhotoCount > 0
                        ? "（已按关键词从 Wikimedia Commons 取回 " + NetworkPhotoCount + " 张照片，并在稿末附上「图片来源」页；"
                          + "这是 CC 许可的署名要求，请随稿一起保留）"
                        : "")
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

                // 文字是否装得进**自己的框**（卡片 / 示意图层 / 叠字最容易出这种问题）。
                // 只在我们自己排的稿子上判：外部文件既不知道页型，也不知道我们的排版规则。
                if (type is not null)
                    foreach (var tb in TextBoxesOf(sp.Slide))
                    {
                        if (tb.W <= 0 || tb.H <= 0) continue;
                        var need = BlockHeightEmu(tb.Plan, tb.W);
                        // 留 2% 或 2pt 的余量：四舍五入与渲染器的细微差别不该报成问题
                        if (need > tb.H + Math.Max(25400, tb.H / 50))
                            Issue("textOverflow", "文字放不进自己的框：按真实字形需要 "
                                + Math.Round(need / 12700.0) + "pt，框高只有 " + Math.Round(tb.H / 12700.0)
                                + "pt：“" + Trim60(tb.Text) + "”");
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

    /// <summary>一个文本框的自检信息：框大小 + 里面文字的排版参数（由 XML 反推，不是跟着渲染时那份数据）。</summary>
    private readonly struct BoxAudit
    {
        public readonly long W, H;
        public readonly List<ParaPlan> Plan;
        public readonly string Text;
        public BoxAudit(long w, long h, List<ParaPlan> plan, string text) { W = w; H = h; Plan = plan; Text = text; }
    }

    /// <summary>
    /// 把一页里所有文本框的「框大小 + 文字排版参数」读出来（用于自检）。
    ///
    /// <para>
    /// 刻意从 <b>写出来的 XML</b> 反推，而不是复用渲染时手里的那个 plan：
    /// 这样它验的是“文件里真的写了什么”，能拦住“排版算对了但没写进 XML”这类错位
    /// （与表格自检同一个道理）。
    /// </para>
    /// </summary>
    private static List<BoxAudit> TextBoxesOf(P.Slide slide)
    {
        var list = new List<BoxAudit>();
        foreach (var sp in slide.Descendants<P.Shape>())
        {
            var body = sp.TextBody;
            if (body is null) continue;
            var xfrm = sp.ShapeProperties?.Transform2D;
            if (xfrm?.Extents is null) continue;
            var plan = new List<ParaPlan>();
            var all = new StringBuilder();
            foreach (var p in body.Elements<A.Paragraph>())
            {
                var props = p.ParagraphProperties;
                var marL = props?.LeftMargin?.Value is { } ml ? (long)ml : 0L;
                var spacing = props?.LineSpacing?.GetFirstChild<A.SpacingPercent>()?.Val?.Value is { } pct
                    ? (int)Math.Round(pct / 1000.0) : 100;
                var spaceBefore = props?.SpaceBefore?.GetFirstChild<A.SpacingPoints>()?.Val?.Value is { } sb
                    ? (int)Math.Round(sb / 100.0) : 0;
                var text = new StringBuilder();
                var size = 0;
                var bold = false;
                foreach (var r in p.Elements<A.Run>())
                {
                    text.Append(r.Text?.Text ?? "");
                    var sz = r.RunProperties?.FontSize?.Value;
                    if (sz is { } v && v > size) { size = v; bold = r.RunProperties?.Bold?.Value ?? false; }
                }
                all.Append(text);
                if (text.Length > 0 && size > 0) plan.Add(P(text.ToString(), size, spaceBefore, spacing, marL, bold));
            }
            if (plan.Count == 0) continue;
            list.Add(new BoxAudit(xfrm.Extents.Cx?.Value ?? 0, xfrm.Extents.Cy?.Value ?? 0, plan, all.ToString()));
        }
        return list;
    }

    /// <summary>这页是不是“本该有正文”的页型（封面/分隔/结束/引言/整图这种只有一块文字的页不算）。</summary>
    private static bool IsBodyPage(string? type)
        => type is null || type is "content" or "twocol" or "table" or "kpi" or "stats" or "grid"
            or "cards" or "timeline" or "iconrows" or "chart" or "summary" or "toc" or "progress"
            or "pyramid" or "funnel" or "matrix" or "cycle" or "stack";

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
            // 换上去的文字可能比原来长得多 → 复核一遍「还放不放得进原文本框」。
            // 不自动改版式（替文字本就是不重排的轻量操作），但**不能静默**：如实报出来，
            // 否则用户会得到一份“文字压在别的元素上”的稿子而不知道原因。
            foreach (var tb in TextBoxesOf(sp.Slide))
            {
                if (tb.W <= 0 || tb.H <= 0) continue;
                var need = BlockHeightEmu(tb.Plan, tb.W);
                if (need > tb.H + Math.Max(25400, tb.H / 50))
                    Warn($"第 {no} 页替换文字后放不进原文本框（需要 {Math.Round(need / 12700.0)}pt，"
                        + $"框高 {Math.Round(tb.H / 12700.0)}pt）：“{Trim60(tb.Text)}”");
            }
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
        // 联网配图的会话状态：缓存 / 署名清单 / 熔断 / 端点覆盖
        _photoCache = new Dictionary<string, Photo?>(StringComparer.OrdinalIgnoreCase);
        _photos = new List<Photo>();
        _photoOffline = false;
        _photoBudgetWarned = false;
        _photoDeadline = Environment.TickCount64 + PhotoBudgetSec * 1000L;
        // 配图来源策略：入参优先，其次环境变量（部署级一次性配置：内网部署设 library 彻底不出网）
        _imageSource = (Str(root, "imageSource") ?? Environment.GetEnvironmentVariable("AGUI_IMAGE_SOURCE") ?? "").Trim();
        _imageScopeId = Str(root, "imageScopeId") ?? Str(root, "image_scope_id");
        _photoApi = Str(root, "imageSearchApi") ?? Environment.GetEnvironmentVariable(PhotoApiEnvVar);
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

        // 两个自动分页：先按表格行数拆表，再按「要点装不下」拆要点页（顺序有讲究：
        // 要点页拆分不涉及表格，两者互不干扰，但都必须在渲染前做完——RenderSlide 一次只出一页）。
        var pageJson = new List<string>();
        foreach (var s in slides)
            foreach (var one in ExpandTableSlide(s))
                pageJson.AddRange(ExpandOverflowSlide(one));

        // 联网配图：把整份稿子里的 imageQuery 都检索/下载到位（同时填好署名清单）。
        // 要点页在拆页阶段可能已经查过一次（要定版式），这里是补上“其余页型 + 追加署名页”。
        PreResolvePhotos(pageJson);
        // 署名页也要走**同一套拆页**：40 张照片的清单绝不可能装在一页里。
        // （之前这里是 pageJson.Add(...)，没拆页 —— 压测 40 张时全部挤在一页、溢出框外，已修）
        // 只有**网络照片**才需要署名：图库里的图是团队自有素材。
        if (NetworkPhotoCount > 0) pageJson.AddRange(ExpandOverflowSlide(CreditsPageJson()));

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

        /// <summary>
        /// 把一张位图作为图片挂进本页，返回关系 id（供 &lt;a:blip r:embed&gt; 引用）。
        /// 按<b>魔数</b>判类型：JPEG 照片若被标成 PNG，PowerPoint 会报“图片不可读”，
        /// 而联网配图（Wikimedia Commons）大多就是 JPEG。
        /// </summary>
        public string AddImage(byte[] bytes)
        {
            var prepared = PrepareImageBytes(bytes);
            var part = AddPartOfDetectedType(prepared);
            using var ms = new MemoryStream(prepared);
            part.FeedData(ms);
            return Part.GetIdOfPart(part);
        }

        /// <summary>嵌入前要不要瘦身的字节阀值：小于它的原样用（不重编）。</summary>
        private const int ImageSlimBytes = 1_200_000;

        /// <summary>嵌入图片的单边像素上限（1920 已超过 16:9 满屏所需）。</summary>
        private const int ImageMaxPx = 1920;

        /// <summary>已瘦身结果的缓存（同一张图会嵌到多页，不必反复解码重编）。</summary>
        [ThreadStatic] private static Dictionary<string, byte[]>? _slimCache;

        /// <summary>
        /// 嵌入前给位图“瘦身”：最长边压到 1920px，照片重编为 JPEG q85（可能带透明通道的保 PNG）。
        ///
        /// <para>
        /// 为何要做：图库里的图是**原图**——一张 12MP 手机照就是 3~12MB，四张就能把 .pptx 顶到 21~31MB，
        /// 而平台对附件有大小上限，超了的产物**挂不到对话里**：用户看到的是“回复说文件生成了、
        /// 对话里却没有下载入口”（实测踩到 21.3MB / 31.2MB 两份）。而幻灯片根本用不到那么大。
        /// </para>
        ///
        /// <para>已经够小的原样返回；重编反而更大的也用原图；解码失败不阻断出稿（当原图用）。</para>
        /// </summary>
        private static byte[] PrepareImageBytes(byte[] bytes)
        {
            if (bytes.Length <= ImageSlimBytes) return bytes;
            try
            {
                _slimCache ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
                // 指纹用“长度 + 头 32 字节”：同一张图会嵌到多页，命中缓存就不必反复解码重编
                var key = bytes.Length + ":" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(32, bytes.Length)));
                if (_slimCache.TryGetValue(key, out var hit)) return hit;

                byte[] result;
                using (var img = Image.Load(bytes))
                {
                    var max = Math.Max(img.Width, img.Height);
                    if (max > ImageMaxPx)
                    {
                        var scale = (double)ImageMaxPx / max;
                        img.Mutate(x => x.Resize(Math.Max(1, (int)Math.Round(img.Width * scale)),
                                                 Math.Max(1, (int)Math.Round(img.Height * scale))));
                    }
                    using var ms = new MemoryStream();
                    // 只有**真的用了透明度**才保 PNG：图库里很多是“截图 / 照片型 PNG”，
                    // 按容器格式一律保 PNG 的话 1920px 的照片仍有 3MB —— 等于没瘦（实测踩到 3.7MB 的 PNG 未被压小）。
                    if (MayHaveAlpha(bytes) && HasTransparency(img)) img.SaveAsPng(ms);
                    else img.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
                    var slim = ms.ToArray();
                    result = slim.Length < bytes.Length ? slim : bytes;
                }
                _slimCache[key] = result;
                return result;
            }
            catch { return bytes; }
        }

        /// <summary>可能带透明通道的格式（PNG/GIF/BMP）：还得再看真用没用（见 HasTransparency）。</summary>
        private static bool MayHaveAlpha(byte[] b)
            => IsPng(b)
            || (b.Length > 3 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
            || (b.Length > 2 && b[0] == (byte)'B' && b[1] == (byte)'M');

        /// <summary>逐像素查是否真的存在 A&lt;255。只有“真的透明”才保 PNG —— 否则照片型 PNG 瘦不下来。</summary>
        private static bool HasTransparency(Image img)
        {
            try
            {
                using var rgba = img.CloneAs<Rgba32>();
                var found = false;
                rgba.ProcessPixelRows(acc =>
                {
                    for (var y = 0; y < acc.Height && !found; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        for (var x = 0; x < row.Length; x++)
                            if (row[x].A < 255) { found = true; break; }
                    }
                });
                return found;
            }
            catch { return true; }   // 查不了就当有透明（宁可大一点也不丢透明）
        }

        /// <summary>
        /// 不能一律当 PNG：<c>[Content_Types].xml</c> 里声明的类型与实际内容不一致时
        /// PowerPoint 会报“图片不可读”。按魔数选类型，支持 OOXML 认识的全部常见位图；
        /// 认不出的（如 WebP）保持旧行为当 PNG 处理，不新增失败路径。
        /// （注：<c>ImagePartType</c> 是静态类，其成员类型不方便做返回类型，所以直接在此分支。）
        /// </summary>
        private ImagePart AddPartOfDetectedType(byte[] bytes)
        {
            if (IsPng(bytes)) return Part.AddImagePart(ImagePartType.Png);
            if (IsJpeg(bytes)) return Part.AddImagePart(ImagePartType.Jpeg);
            if (bytes.Length > 3 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
                return Part.AddImagePart(ImagePartType.Gif);
            if (bytes.Length > 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
                return Part.AddImagePart(ImagePartType.Bmp);
            if (bytes.Length > 4 && ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A)
                || (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00)))
                return Part.AddImagePart(ImagePartType.Tiff);
            return Part.AddImagePart(ImagePartType.Png);
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
            case "pyramid":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(PyramidBody(el, ctx));
                break;
            case "funnel":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(FunnelBody(el, ctx));
                break;
            case "matrix":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(MatrixBody(el, ctx));
                break;
            case "cycle":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(CycleBody(el, ctx));
                break;
            case "stack":
                shapes.Add(SlideTitle(Str(el, "title") ?? "", ctx));
                shapes.Add(StackBody(el, ctx));
                break;
            case "hero":
                return HeroSlide(el, ctx);
            case "credits":
                // 用了联网照片时由程序自动追加的「图片来源」页（署名是许可要求，不由模型决定）
                return CreditsSlide(el, ctx);
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
                    "pyramid" or "layers" or "levels" => PyramidBody(el, ctx),
                    "funnel" => FunnelBody(el, ctx),
                    "matrix" or "quadrant" => MatrixBody(el, ctx),
                    "cycle" or "loop" => CycleBody(el, ctx),
                    "stack" or "architecture" => StackBody(el, ctx),
                    "iconrows" or "icon-rows" or "rows" => IconRowsBody(el, ctx),
                    // 没写 layout 但带了图（path / imageQuery）→ 自动变成“文左图右”：
                    // 这样模型只要在要点页上多写一个 imageQuery 就能页页有图，不必改记别的页型。
                    // 只看“图真的拿到手了没有”，检索失败就老老实实地走纯要点版式，
                    // 不把半页白白交给一张降级题图。
                    _ when HasUsablePhoto(el) => ImageSide(el, ctx, imageFirst: false),
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
        var txts = new List<Txt> { T(title, 4000, t.Primary, bold: true, titleFont: true, spacing: 105) };
        if (!string.IsNullOrWhiteSpace(subtitle))
            txts.Add(T(subtitle, 1800, t.Secondary, spaceBefore: 12, spacing: 130));
        shapes.Add(FitBox(ctx.NextId(), left, 1900000, rightW, 2600000, txts, "封面标题"));

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

        var txts = new List<Txt> { T(title, 4400, t.Primary, bold: true, align: "ctr", titleFont: true, spacing: 105) };
        if (!string.IsNullOrWhiteSpace(subtitle))
            txts.Add(T(subtitle, 1800, t.Secondary, align: "ctr", spaceBefore: 18, spacing: 130));
        if (!string.IsNullOrWhiteSpace(author)) txts.Add(T(author, 1400, t.Text, align: "ctr"));
        if (!string.IsNullOrWhiteSpace(date)) txts.Add(T(date, 1400, t.Secondary, align: "ctr", spaceBefore: 4));
        shapes.Add(FitBox(ctx.NextId(), MX, 1714500, CW, 3429000, txts, "封面标题", "ctr"));
        return SlideXml(t.Bg, shapes);
    }

    /// <summary>背景图封面：整页图 + 半透明蒙层，保证标题在任何图上都读得清。</summary>
    private static string CoverImageBg(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var requested = Str(el, "path");
        var path = ImagePathOf(el);
        var title = Str(el, "title") ?? ctx.Title;
        var shapes = new List<string>();
        var hasImg = path is not null;
        if (hasImg)
        {
            shapes.Add(Picture(ctx.NextId(), AddCoverImage(ctx, path!, W, H), 0, 0, W, H));
            // 蒙层：主色 45% 透明 —— 文字对比度靠它守住，不能省
            shapes.Add(Rect(ctx.NextId(), 0, 0, W, H, t.Primary, alpha: 55));
        }
        else
        {
            // 没给背景图 → 用自动生成的题图（以前是一块纯色，看着就是一页色块）
            shapes.Add(HeroArt(ctx, 0, 0, W, H, title));
            if (!string.IsNullOrWhiteSpace(requested)) Warn("封面背景图不存在，已改用自动生成的题图：" + requested);
        }
        var onImg = hasImg ? t.OnPrimary : t.Light;

        var subtitle = Str(el, "subtitle") ?? ctx.Subtitle;
        var txts = new List<Txt> { T(title, 4400, t.Bg, bold: true, align: "ctr", titleFont: true, spacing: 105) };
        if (!string.IsNullOrWhiteSpace(subtitle))
            txts.Add(T(subtitle, 1800, onImg, align: "ctr", spaceBefore: 18, spacing: 130));
        var author = Str(el, "author") ?? ctx.Author;
        var date = Str(el, "date") ?? ctx.Date;
        if (!string.IsNullOrWhiteSpace(author) || !string.IsNullOrWhiteSpace(date))
            txts.Add(T(string.Join("　·　", new[] { (author ?? "").Trim(), (date ?? "").Trim() }
                .Where(s => s.Length > 0)), 1400, onImg, align: "ctr", spaceBefore: 24));
        shapes.Add(FitBox(ctx.NextId(), MX, 1714500, CW, 3429000, txts, "封面标题", "ctr"));
        return SlideXml(hasImg ? t.Bg : t.Primary, shapes);
    }

    /// <summary>左文右图封面（非对称布局）：右半页铺满图，左半页放标题与元信息。</summary>
    private static string CoverSplit(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new List<string> { Rect(ctx.NextId(), 0, 0, W, H, t.Bg) };
        var imgW = W * 5 / 12;
        var imgX = W - imgW;
        var requested = Str(el, "path");
        var path = ImagePathOf(el);
        var title = Str(el, "title") ?? ctx.Title;
        var hasImg = path is not null;
        if (hasImg)
            shapes.Add(Picture(ctx.NextId(), AddCoverImage(ctx, path!, imgW, H), imgX, 0, imgW, H));
        else
        {
            // 右图缺位 → 自动生成题图（与 image/split 版式同一份视觉语言）
            shapes.Add(HeroArt(ctx, imgX, 0, imgW, H, title));
            if (!string.IsNullOrWhiteSpace(requested)) Warn("封面右图不存在，已改用自动生成的题图：" + requested);
        }

        var textW = imgX - MX - 457200;
        var subtitle = Str(el, "subtitle") ?? ctx.Subtitle;
        var txts = new List<Txt> { T(title, 3600, t.Primary, bold: true, titleFont: true, spacing: 105) };
        if (!string.IsNullOrWhiteSpace(subtitle))
            txts.Add(T(subtitle, 1700, t.Secondary, spaceBefore: 14, spacing: 130));
        shapes.Add(FitBox(ctx.NextId(), MX, 1900000, textW, 2743200, txts, "封面标题"));

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
        var sub = Str(el, "subtitle");
        var txts = new List<Txt> { T(Str(el, "title") ?? "谢谢", 4000, t.Bg, bold: true, align: "ctr", titleFont: true) };
        if (!string.IsNullOrWhiteSpace(sub))
            txts.Add(T(sub, 1600, t.Accent, align: "ctr", spaceBefore: 16));
        shapes.Add(FitBox(ctx.NextId(), MX, 2286000, CW, 2286000, txts, "结束页", "ctr"));
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
        var paras = new List<Txt> { T(Str(el, "title"), 3600, t.Bg, bold: true, titleFont: true, spacing: 110) };
        var sub = Str(el, "subtitle");
        if (!string.IsNullOrWhiteSpace(sub))
            paras.Add(T(sub, 1600, t.OnPrimary, spaceBefore: 12));
        shapes.Add(FitBox(ctx.NextId(), MX + 1524000, 1600200, CW - 1524000, 2286000, paras, "章节标题"));
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

        var paras = new List<Txt> { T(Str(el, "title"), 3400, t.Primary, bold: true, titleFont: true, spacing: 110) };
        var sub = Str(el, "subtitle");
        if (!string.IsNullOrWhiteSpace(sub))
            paras.Add(T(sub, 1600, t.Secondary, spaceBefore: 14));
        shapes.Add(FitBox(ctx.NextId(), blockW + 76200 + MX, 2057400, W - blockW - 76200 - MX * 2, 2743200,
            paras, "章节标题", "ctr"));
        shapes.Add(PageBadge(ctx));
        return SlideXml(t.Bg, shapes);
    }

    /// <summary>满页色 + 超大序号水印：序号用低透明度画在上部，标题压在下部（两者不重叠才能读）。</summary>
    private static string SectionFull(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new List<string> { Rect(ctx.NextId(), 0, 0, W, H, t.Primary) };
        // 有配图（path 或 imageQuery）就用满页照片 + 主色蒙层：分隔页是全稿最值得放图的页型之一，
        // 蒙层与背景图封面同一手法（对比度靠它守住）。
        var path = ImagePathOf(el);
        if (path is not null)
        {
            shapes.Add(Picture(ctx.NextId(), AddCoverImage(ctx, path, W, H), 0, 0, W, H));
            shapes.Add(Rect(ctx.NextId(), 0, 0, W, H, t.Primary, alpha: 62));
        }
        // 水印序号：同底色的低透明度大字（用 a:alpha，不要用颜色编透明度）
        shapes.Add(TextBox(ctx.NextId(), MX, 0, CW, 4114800,
            Para(ctx.Index.ToString("00"), 20000, t.Bg, bold: true, align: "ctr",
                font: t.FontTitle, alpha: 22), anchor: "ctr"));
        var paras = new List<Txt> { T(Str(el, "title"), 4000, t.Bg, bold: true, align: "ctr", titleFont: true, spacing: 110) };
        var sub = Str(el, "subtitle");
        if (!string.IsNullOrWhiteSpace(sub))
            paras.Add(T(sub, 1700, t.OnPrimary, align: "ctr", spaceBefore: 16));
        shapes.Add(FitBox(ctx.NextId(), MX, 4114800, CW, 2057400, paras, "章节标题", "ctr"));
        shapes.Add(PageBadge(ctx));
        return SlideXml(t.Primary, shapes);
    }

    // ---- 引言 ----
    private static string QuoteSlide(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var shapes = new List<string>();
        shapes.Add(Rect(ctx.NextId(), MX, 1143000, 57150, 2743200, t.Accent));
        // 引言是自由文本，长短完全看调用方 —— 必须量高后缩字号，否则一段长引言直接溢出框外
        var txts = new List<Txt> { T(Str(el, "text"), 2600, t.Primary, titleFont: true, spacing: 130) };
        shapes.Add(FitBox(ctx.NextId(), MX + 457200, 1143000, CW - 457200, 2743200, txts, "引言", "ctr"));
        var cite = Str(el, "cite");
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
        // 标题框可以长到正文区上缘：长标题折两行本来就该允许，但绝不能顶进正文里
        var availH = Math.Max(TitleH, BodyY - TitleY - Sz(57150));
        var txts = new List<Txt> { T(text, 2800, t.Primary, bold: true, titleFont: true, spacing: 100) };
        var fit = FitTexts(txts, CW, availH, "页标题");
        var usedH = Math.Max(TitleH, fit.UsedH);
        shapes.Append(TextBox(ctx.NextId(), MX, TitleY, CW, usedH, fit.Xml));
        if (_titleRule)
            shapes.Append(Rect(ctx.NextId(), MX, TitleY + usedH + 57150, 838200, 45720, t.Accent));
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
        // 深色主题下 Light 是“面”（很深），拿来当字色会看不见 —— 保色相抬亮到能读
        var color = IsDarkBg(t) ? EnsureContrastHsl(t.Light, t.Bg, 3.0, minSat: 0.10) : "7A7A7A";
        // 脚注常被用来放数据来源/补充说明，一长就会跑到页码徽标那一行：量高后缩/截断
        return FitBox(ctx.NextId(), MX, H - 1097280, CW - BadgeW - _m.Pad, Sz(457200),
            [T(text, 1100, color)], "脚注/图注");
    }

    // ===== 版面文字度量：真实字形量宽 + 真实行高，「排不进框就缩，缩不动就分页/截断」 =====
    //
    // 为什么要有这一套（实测踩坑记录，详见 README 的「文字溢出」一节）：
    //   最初用「字数 × 字号」估算高度，有三处与真实排版不符 ——
    //     ① 行高系数当成 1.0，而 CJK 字体的自然行高是 1.27（微软雅黑）~1.45 em（Noto Sans CJK SC）；
    //     ② 段前距的单位算小了 100 倍（plan 里存的是「磅」，估算时却按「百分之一磅」乘）；
    //     ③ 粗体、左缩进（marL）与 CJK/拉丁混排的真实字宽都没算。
    //   三者叠加，估出来的高度常常不到真实值的一半 —— 「缩字号」因此几乎从不触发。
    //   以前样张看着还行，是因为文件里带着 <a:normAutofit/>：**LibreOffice 会替我们缩**；
    //   而 PowerPoint 打开时并不重算 autofit（PptxGenJS 的 shrinkText 被抱怨“编辑一下才生效”是同一个坑），
    //   用户看到的才是真身 —— 文字溢出自己的框（卡片 / 示意图层里尤其明显）。
    //   所以现在：自己按真实字形量、自己缩；缩到下限还放不下就**分页或截断并报警**，
    //   不再把「装得下」这件事推给渲染器。

    private const int MinFontSize = 1200;   // 缩字号的下限（12pt）：再小就不如不显示
    // 行距的下限：**不要压到 100% 以下**。
    // 实测踩到：spcPct < 100% 时行框比字体本身还矮，汉字与全角标点的**墨迹会越出行框**
    // （标题首行会冒到框上面去，渲染图上就是“顶到页边”）。缩放时行距最多缩到设计值的 100% 就不再压。
    private const int MinSpacingPct = 100;
    // 折行判定的容量余量：我们的量宽与渲染器的实际排版有 ~1.6% 的差
    // （实测：30 个汉字 @27.16pt = 814.8pt，我们判“刚好放得下”，LibreOffice 却折了一行，
    //  标题因此从下边冒出去；带“ / ”分隔的混合标题同理）。
    // 2% 是权衡后的取值：刚好盖住这个偏差（见上），而又不会把“离边界还有 4%”的
    // 表格单元格（实测 24 字 @10.5pt）挤成两行 —— 后者会让行高白翻一倍、每页少装一半的行。
    private const double LineFitSlack = 0.98;

    /// <summary>一段要排进文本框的文字（<b>量高的输入</b>）：字号 / 段前距 / 行距 / 缩进 / 粗体都要如实交上来。</summary>
    private struct ParaPlan
    {
        public string Text;
        public int Size;         // 字号（百分之一磅）
        public int SpaceBefore;  // 段前距（磅，与 <see cref="Para"/> 的 spaceBefore 同一单位）
        public int Spacing;      // 行距百分数（125 = 125%）
        public long MarL;        // 左缩进（EMU）：会吃掉可用宽度
        public bool Bold;

        public ParaPlan(string? text, int size, int spaceBefore, int spacing, long marL, bool bold)
        {
            Text = text ?? ""; Size = size; SpaceBefore = spaceBefore;
            Spacing = spacing <= 0 ? 100 : spacing; MarL = marL; Bold = bold;
        }
    }

    /// <summary>构造一段待排文字（字段默认值在这里，调用点才写不长）。</summary>
    private static ParaPlan P(string? text, int size, int spaceBefore = 0, int spacing = 100, long marL = 0, bool bold = false)
        => new ParaPlan(text, size, spaceBefore, spacing, marL, bold);

    /// <summary>缩放后的有效字号：不低于 12pt（但本来就更小的设计字号不会被抬上去）。</summary>
    private static int EffSize(int size, double scale)
        => Math.Max(Math.Min(size, MinFontSize), (int)Math.Round(size * scale));

    /// <summary>缩放后的有效行距（百分数）：不低于 80%。</summary>
    private static int EffSpacing(int pct, double scale)
        => Math.Max(MinSpacingPct, (int)Math.Round((pct <= 0 ? 100 : pct) * scale));

    /// <summary>段前距按同一比例缩放（缩字号时不至于留下突兀的大空档）。</summary>
    private static int EffSpaceBefore(int points, double scale)
        => Math.Max(0, (int)Math.Round(points * scale));

    private static readonly Dictionary<(string Text, int Size, bool Bold), long> _widthCache = new();
    private static double _lineHeightEm;

    /// <summary>
    /// 字体自然行高（em 倍数）：100% 行距下「一行文字」占的高度。
    ///
    /// <para>
    /// 这是整个估算里最关键的系数，也是最初错得最离谱的地方（当成 1.0）。
    /// 实测（容器 LibreOffice + Noto Sans CJK SC，见 README 的标定记录）：17pt / 行距 125% 的
    /// 两行间距是 30.92pt = 17 × 1.25 × <b>1.455</b>，即自然行高 ≈ 1.46 em。
    /// 微软雅黑这类 Windows 中文字体约 1.27 em —— 比容器字体小，所以取容器字体的实测值会
    /// **偏保守**（在 PowerPoint 里更宽裕，绝不至于溢出）。
    /// 这里直接从字体度量算，并留 2% 余量；算不出来时退回 1.46。
    /// </para>
    /// </summary>
    private static double LineHeightEm()
    {
        if (_lineHeightEm > 0) return _lineHeightEm;
        lock (_fontLock)
        {
            if (_lineHeightEm > 0) return _lineHeightEm;
            var em = 0.0;
            try
            {
                // 1.0.1 的字体度量在 HorizontalMetrics 上（FontMetrics 本身没有 Ascender/Descender/LineGap）
                var m = Family(20f).FontMetrics;
                if (m.UnitsPerEm > 0)
                    em = (m.HorizontalMetrics.Ascender - m.HorizontalMetrics.Descender
                        + m.HorizontalMetrics.LineGap) / (double)m.UnitsPerEm;
            }
            catch { /* 度量取不到就退回经验值 */ }
            if (em <= 0) em = 1.46;
            // 至少按 1.45 em 算：技能既可能在容器里跑（Noto Sans CJK SC，1.45），
            // 也可能在开发机 / 本机桥上跑（微软雅黑，1.27）。取大值 → 容器渲染一定不会溢出，
            // 而用户机器上的行高更小，只会更宽松。宁可略松，也不要在别的环境下溢出。
            _lineHeightEm = Math.Max(1.45, em * 1.02);
            return _lineHeightEm;
        }
    }

    /// <summary>真实字形量宽（EMU）。用渲染图表时挑的那套「确认含中文字形」的字体，粗体另取字面。</summary>
    private static long MeasuredWidthEmu(string text, int size, bool bold)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var key = (text, size, bold);
        lock (_fontLock)
        {
            if (_widthCache.TryGetValue(key, out var hit)) return hit;
            if (_widthCache.Count > 20000) _widthCache.Clear();
            long emu;
            try
            {
                var pt = size / 100f;
                var font = bold ? BoldFont(pt) : Family(pt);
                var w = SixLabors.Fonts.TextMeasurer.MeasureAdvance(
                    text, new SixLabors.Fonts.TextOptions(font)).Width;
                emu = (long)Math.Ceiling(w * 12700 * WidthSafety(text));
            }
            catch
            {
                // 度量失败也要能排版：退回「CJK 1em / ASCII 0.55em」的老办法
                var em = 0.0;
                foreach (var ch in text) em += ch < 0x2E80 ? 0.55 : 1.0;
                emu = (long)Math.Ceiling(em * size * 127.0 * WidthSafety(text));
            }
            _widthCache[key] = emu;
            return emu;
        }
    }

    /// <summary>
    /// 量宽余量：<b>只给拉丁字母留</b>。
    ///
    /// <para>
    /// 汉字的 advance 就是 1 em（Noto Sans CJK SC 与微软雅黑完全一致），量出来就是真值，
    /// 再乘个安全系数反而会把「刚好放下」的文字判成多一行 —— 实测踩到：
    /// 一段 149 字的引言在 26pt 下正好 5 行，加了 3% 余量后估成 6 行，字号被白白缩掉四分之一。
    /// 拉丁字母在不同字体间差得多（Calibri / Arial / Noto Sans 相差 5% 上下），所以那一部分才留余量。
    /// </para>
    /// </summary>
    private static double WidthSafety(string text)
    {
        var latin = 0;
        var cjk = 0;
        foreach (var ch in text)
        {
            if (ch >= 0x2E80) cjk++;
            else if (ch > ' ') latin++;
        }
        if (latin == 0) return 1.0;
        return cjk == 0 ? 1.05 : 1.03;
    }

    /// <summary>
    /// 贪心折行后的行数：CJK 逐字可断，拉丁按词断。
    ///
    /// <para>
    /// 这是「量得准」的另一半 —— 最初按 <c>总宽 / 可用宽</c> 直接除，遇到拉丁单词、标点、
    /// 混排时会明显低估行数（例如一行结尾刚好放不下一个长单词时，真实排版会把它整段挪到下一行）。
    /// </para>
    /// </summary>
    private static int WrappedLines(string text, int size, bool bold, long availW)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        if (availW < 100000) availW = 100000;
        // 折行判定用略窄的容量（见 LineFitSlack）
        availW = (long)(availW * LineFitSlack);

        // CJK 上下文里的空格要按“全角空格”（≥0.5em）算，不能按 SixLabors 量出来的窄空格（~0.25em）：
        // 实测跏到 —— 一个带 3 组“ / ”分隔的标题里 6 个空格就差了 1.5em，恰好让“刚好放得下”的标题
        // 在 LibreOffice 里多折一行。这里把差值补上（连**快路径**也要补，否则整串量宽直接判“放得下”。
        // 实测就是漏在这里：快路径没走下方的逐词循环，空格修正根本没生效）。
        var hasCjk = false;
        foreach (var ch in text) if (ch >= 0x2E80) { hasCjk = true; break; }
        var measuredSpace = hasCjk ? MeasuredWidthEmu(" ", size, bold) : 0;
        var cjkSpace = hasCjk ? Math.Max(measuredSpace, (long)(size * 127.0 * 0.5)) : 0;
        var raw = MeasuredWidthEmu(text, size, bold);
        if (hasCjk && cjkSpace > measuredSpace)
        {
            var spaces = 0;
            foreach (var ch in text) if (ch == ' ') spaces++;
            raw += spaces * (cjkSpace - measuredSpace);
        }
        if (raw <= availW) return 1;

        var lines = 1;
        long cur = 0;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\n') { lines++; cur = 0; i++; continue; }
            if (ch == '\r') { i++; continue; }
            var len = TokenLength(text, i);
            var token = text.Substring(i, len);
            i += len;
            var w = ch == ' ' && hasCjk ? cjkSpace : MeasuredWidthEmu(token, size, bold);
            if (ch == ' ')
            {
                // 空格只在行内占位：行尾的空格不能把行撑开
                cur += w;
                continue;
            }
            if (cur > 0 && cur + w > availW) { lines++; cur = w; }
            else cur += w;
        }
        return lines;
    }

    /// <summary>取一个「断行单元」：拉丁文/数字/半角符号按词（连成一片），其余逐字。</summary>
    private static int TokenLength(string text, int start)
    {
        var ch = text[start];
        if (ch >= 0x2E80) return 1;                       // CJK：逐字可断
        var i = start;
        while (i < text.Length && text[i] < 0x2E80 && text[i] != ' ' && text[i] != '\n' && text[i] != '\r') i++;
        return i > start ? i - start : 1;
    }

    /// <summary>一组段落按给定缩放排版后的总高度（EMU）。</summary>
    private static long BlockHeightEmu(List<ParaPlan> plan, long availW, double scale = 1.0)
    {
        var lh = LineHeightEm();
        long total = 0;
        foreach (var p in plan)
        {
            if (p.Text.Length == 0) continue;
            var sz = EffSize(p.Size, scale);
            var pct = EffSpacing(p.Spacing, scale);
            var w = availW - p.MarL;
            var lines = WrappedLines(p.Text, sz, p.Bold, w);
            total += (long)(lines * sz * 127.0 * lh * (pct / 100.0))
                   + EffSpaceBefore(p.SpaceBefore, scale) * 12700L;
        }
        return total;
    }

    /// <summary>
    /// 把一组段落排进「可用宽 × 可用高」的缩放系数（≤1；下限见 <see cref="FloorScale"/>）。
    ///
    /// <para>
    /// 为什么用<b>二分</b>而不是一步除（<c>availH / need</c>）：高度对缩放不是线性的 ——
    /// 字号一缩，**折行数也会变少**。实测踩到：3 行 28pt 的标题在 79pt 的标题区里放不下，
    /// 一步除给出 0.65，而其实缩到 0.85 就只剩 2 行、完全放得下 —— 标题被白白缩成了 18pt。
    /// 高度对缩放是单调不减的（字号大则行更高、行数也不会变少），所以二分求「放得下的最大缩放」是安全的。
    /// </para>
    ///
    /// <para>
    /// 返回 1.0 表示原样放得下；返回值处的实际高度请用 <see cref="BlockHeightEmu"/> 复核，
    /// <b>大于可用高就说明「缩到下限也放不下」</b>，调用方要分页或截断。
    /// </para>
    /// </summary>
    private static double FitScale(List<ParaPlan> plan, long availW, long availH, double minScale = 0.6)
    {
        if (plan.Count == 0 || availH <= 0) return 1.0;
        if (FitsBox(plan, availW, availH, 1.0)) return 1.0;
        var floor = FloorScale(plan, minScale);
        if (!FitsBox(plan, availW, availH, floor)) return floor;   // 缩到下限也放不下
        var lo = floor;      // 放得下
        var hi = 1.0;        // 放不下
        for (var i = 0; i < 12; i++)
        {
            var mid = (lo + hi) / 2;
            if (FitsBox(plan, availW, availH, mid)) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>缩放下限：既守住设计上的 minScale，也保证没有哪一段被抬到 12pt 以上（否则估算会对不上）。</summary>
    private static double FloorScale(List<ParaPlan> plan, double minScale)
    {
        var f = minScale;
        foreach (var p in plan)
            if (p.Size > MinFontSize) f = Math.Max(f, MinFontSize / (double)p.Size);
        return Math.Min(1.0, f);
    }

    /// <summary>这组段落在这个缩放下放得下吗。</summary>
    private static bool FitsBox(List<ParaPlan> plan, long availW, long availH, double scale)
        => BlockHeightEmu(plan, availW, scale) <= availH;

    /// <summary>
    /// 一段要排进文本框的文字（给 <see cref="FitBox"/> 用）：文本 + 版式参数放在一起，
    /// 于是「量高 → 缩字号 → 装不下就截断」只有一处实现，所有页型共用同一套规则。
    /// </summary>
    private sealed class Txt
    {
        public string Text = "";
        public int Size = 1700;
        public int SpaceBefore;      // 磅
        public int Spacing = 100;    // 行距百分数
        public bool Bold;
        public bool TitleFont;       // 走主题的标题字体
        public long MarL;
        public string? Color;        // 缺省用主题正文色
        public string Align = "l";
        public string? Bullet;
        public int Alpha = 100;

        public ParaPlan Plan() => P(Text, Size, SpaceBefore, Spacing, MarL, Bold);

        public string Xml(double scale)
            => Para(Text, EffSize(Size, scale), Color ?? _currentTheme?.Text ?? "333333", Bold, Align, Bullet,
                TitleFont ? _currentTheme?.FontTitle : null,
                EffSpaceBefore(SpaceBefore, scale), EffSpacing(Spacing, scale), (int)MarL, Alpha);
    }

    /// <summary>构造一段待排文字（参数全给名字，调用点才一眼看得清）。</summary>
    private static Txt T(string? text, int size, string? color = null, bool bold = false, string align = "l",
        int spaceBefore = 0, int spacing = 100, long marL = 0, string? bullet = null,
        bool titleFont = false, int alpha = 100)
        => new Txt
        {
            Text = text ?? "", Size = size, Color = color, Bold = bold, Align = align,
            SpaceBefore = spaceBefore, Spacing = spacing, MarL = marL, Bullet = bullet,
            TitleFont = titleFont, Alpha = alpha,
        };

    /// <summary>
    /// 把一组段落排进固定大小的文本框：真实字形量高 → 缩字号 → 装不下就截断并报警。
    ///
    /// <para>
    /// 卡片、示意图层、叠字、封面这类<b>结构固定的框</b>用这个：它们不能像要点页那样分页，
    /// 但可以缩字号、也可以少写一点。无论哪种，都不允许文字画出自己的框。
    /// </para>
    /// </summary>
    private static string FitBox(int id, long x, long y, long cx, long cy, List<Txt> txts, string where,
        string anchor = "t")
    {
        var fit = FitTexts(txts, cx, cy, where);
        return TextBox(id, x, y, cx, cy, fit.Xml, anchor: anchor);
    }

    /// <summary>排版结果：段落 XML、生效缩放、实际占用高度（EMU）。</summary>
    private static (string Xml, double Scale, long UsedH) FitTexts(List<Txt> txts, long availW, long availH, string where)
    {
        var scale = FitScale(txts.Select(t => t.Plan()).ToList(), availW, availH);
        TrimToFit(txts, availW, availH, scale, where);
        var sb = new StringBuilder();
        foreach (var t in txts) if (t.Text.Length > 0) sb.Append(t.Xml(scale));
        var used = Math.Min(availH, BlockHeightEmu(txts.Select(t => t.Plan()).ToList(), availW, scale));
        return (sb.ToString(), scale, Math.Max(0, used));
    }

    /// <summary>
    /// 缩到下限仍放不下时：按「还能放几行」截断，并 <see cref="Warn"/> 说清楚。
    ///
    /// <para>
    /// 为什么不一味缩字号：12pt 以下就不能读了。结构固定的框又不能分页，
    /// 那就<b>少写一点、并明确说出来</b>，绝不让文字压到隔壁卡片上
    /// （实测：示意图层的说明文字一长就糊到下一层，比少写几个字难看得多）。
    /// </para>
    /// </summary>
    private static void TrimToFit(List<Txt> txts, long availW, long availH, double scale, string where)
    {
        if (FitsBox(txts.Select(t => t.Plan()).ToList(), availW, availH, scale)) return;
        var lh = LineHeightEm();
        long used = 0;
        for (var i = 0; i < txts.Count; i++)
        {
            if (txts[i].Text.Length == 0) continue;
            var p = txts[i].Plan();
            var sz = EffSize(p.Size, scale);
            var pitch = sz * 127.0 * lh * (EffSpacing(p.Spacing, scale) / 100.0);
            var before = EffSpaceBefore(p.SpaceBefore, scale) * 12700L;
            var w = availW - p.MarL;
            var h = (long)(WrappedLines(p.Text, sz, p.Bold, w) * pitch) + before;
            if (used + h <= availH) { used += h; continue; }

            var room = availH - used - before;
            var keep = room <= 0 ? 0 : (int)(room / pitch);
            var original = txts[i].Text;
            txts[i].Text = keep <= 0 ? "" : TruncateToLines(original, sz, p.Bold, w, keep);
            var dropped = original.Length - txts[i].Text.Length;
            for (var j = i + 1; j < txts.Count; j++)
            {
                dropped += txts[j].Text.Length;
                txts[j].Text = "";
            }
            if (dropped > 0)
                Warn("“" + where + "”内容太多，已按版面截掉约 " + dropped + " 个字（缩到下限仍放不下；请精简该处文字或改用要点页/分页）");
            return;
        }
    }

    /// <summary>把一段文字截到最多 <paramref name="maxLines"/> 行（末尾加省略号）；截不下就返回空串。</summary>
    private static string TruncateToLines(string text, int size, bool bold, long availW, int maxLines)
    {
        if (maxLines <= 0) return "";
        if (WrappedLines(text, size, bold, availW) <= maxLines) return text;
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            var cand = text.Substring(0, mid) + "…";
            if (WrappedLines(cand, size, bold, availW) <= maxLines) lo = mid;
            else hi = mid - 1;
        }
        return lo <= 0 ? "" : text.Substring(0, lo).TrimEnd() + "…";
    }

    // ---- 正文：项目符号 ----

    private static string BulletBody(JsonElement el, SlideCtx ctx, bool accent, List<string>? only = null)
    {
        var t = ctx.Theme;
        var items = only ?? StringList(el, "bullets");
        if (items.Count == 0)
        {
            var p = Str(el, "text");
            if (!string.IsNullOrWhiteSpace(p)) items.Add(p!);
        }
        if (items.Count == 0) items.Add("（本页暂无要点）");

        // 先把「要排什么」整理成数据，再按真实字形量高决定缩放，最后才生成 XML（内容太多就缩，不越界）
        var plan = BulletPlan(items, _m.Gap);
        var scale = FitScale(plan, CW, BodyH);

        var paras = new StringBuilder();
        foreach (var raw in items)
        {
            var color = accent ? t.Primary : t.Text;
            // 支持「小标题：说明」的两行结构，让内容页更有层次
            if (SplitLabel(raw) is { } parts)
            {
                paras.Append(Para(parts.Label, EffSize(1800, scale), t.Primary, bold: true, align: "l",
                    bullet: "•", spaceBefore: EffSpaceBefore(12, scale), lineSpacing: EffSpacing(120, scale),
                    marL: (int)_m.Gap));
                paras.Append(Para(parts.Detail, EffSize(1500, scale), t.Secondary, align: "l",
                    lineSpacing: EffSpacing(125, scale), marL: (int)_m.Gap));
            }
            else
            {
                paras.Append(Para(raw, EffSize(1700, scale), color, align: "l", bullet: "•",
                    spaceBefore: EffSpaceBefore(10, scale), lineSpacing: EffSpacing(125, scale),
                    marL: (int)_m.Gap));
            }
        }
        return TextBox(ctx.NextId(), MX, BodyY, CW, BodyH, paras.ToString(), anchor: "t");
    }

    /// <summary>要点页的排版计划：与 <see cref="BulletBody"/> 用的是<b>同一套规则</b>（分页时也要用它）。</summary>
    private static List<ParaPlan> BulletPlan(List<string> items, long marL)
    {
        var plan = new List<ParaPlan>();
        foreach (var raw in items)
        {
            if (SplitLabel(raw) is { } parts)
            {
                plan.Add(P(parts.Label, 1800, 12, 120, marL, bold: true));
                plan.Add(P(parts.Detail, 1500, 0, 125, marL));
            }
            else plan.Add(P(raw, 1700, 10, 125, marL));
        }
        return plan;
    }

    /// <summary>
    /// 条目型页型的排版计划（分页判定与渲染<b>必须</b>用同一套规则，否则拆出来的页会与预期不符）。
    /// </summary>
    private static List<ParaPlan> ItemsPlan(string type, List<string> items)
        => type switch
        {
            // 目录渲染出来的是「01   标题」这一串（序号占宽），量高必须按同一串量
            "toc" => items.Select((it, i) => P((i + 1).ToString("00") + "   " + it, 1800, 14, 120)).ToList(),
            "credits" => CreditPlan(items),
            _ => BulletPlan(items, _m.Gap),
        };

    /// <summary>单条目的排版计划（分页用；目录的序号前缀用等宽的 “00” 占位）。</summary>
    private static List<ParaPlan> SingleItemPlan(string type, string item)
        => type switch
        {
            "toc" => [P("00   " + item, 1800, 14, 120)],
            "credits" => CreditPlan([item]),
            _ => BulletPlan([item], _m.Gap),
        };

    /// <summary>
    /// 一页能放下几条条目（按「缩到下限」算）。
    ///
    /// <para>
    /// 用于<b>分页</b>：一条要点很长、或条数太多时，与其把整页缩到 12pt（甚至缩不下而溢出），
    /// 不如按能放下的条数拆成多页（与表格「过长自动分页」同一口径：宁可多一页，不丢内容）。
    /// </para>
    /// </summary>
    private static int ItemsPerPage(List<string> items, List<ParaPlan> allPlan, Func<string, List<ParaPlan>> planOf,
        long availW, long availH)
    {
        if (items.Count == 0) return 1;
        var floor = FloorScale(allPlan, 0.6);
        long used = 0;
        var n = 0;
        foreach (var it in items)
        {
            var h = BlockHeightEmu(planOf(it), availW, floor);
            if (n > 0 && used + h > availH) break;
            used += h;
            n++;
        }
        return Math.Max(1, Math.Min(n, items.Count));
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

        // 目录项一多/一长就会撑出正文区，先按真实字形量高再缩
        // 注意：量的是**带序号前缀的那串文字**（“01   目录”），否则量出来的行数比真实少
        var plan = ItemsPlan("toc", items);
        var scale = FitScale(plan, CW, BodyH);

        var paras = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            paras.Append(Para((i + 1).ToString("00") + "   " + items[i], EffSize(1800, scale), t.Text,
                align: "l", spaceBefore: EffSpaceBefore(14, scale), lineSpacing: EffSpacing(120, scale)));
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
        var plan = items.Select(it => P(it, 1700, 0, 120)).ToList();
        var scale = FitScale(plan, cardW - _m.Pad * 2, cardH - _m.Pad);
        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var c = i % cols; var r = i / cols;
            var x = MX + c * (cardW + gap);
            var y = BodyY + r * (cardH + gap);
            sb.Append(Rect(ctx.NextId(), x, y, cardW, cardH, t.Light, radius: true));
            // 左侧序号块
            sb.Append(Rect(ctx.NextId(), x, y, 57150, cardH, t.Accent));
            // 卡里是「大序号 + 标题」两段：一起量（以前只量标题，序号那一行白占高度）
            sb.Append(FitBox(ctx.NextId(), x + _m.Pad, y, cardW - _m.Pad * 2, cardH,
                [T((i + 1).ToString("00"), EffSize(3000, scale), t.Accent, bold: true, titleFont: true),
                 T(items[i], EffSize(1700, scale), t.Text, spaceBefore: 6, spacing: 120)],
                "目录卡片 " + (i + 1), "ctr"));
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
        // 每项是「大序号 + 标题」两段：量的时候要一起量（只量标题会低估一半）
        var txts = new List<Txt>();
        for (var i = 0; i < items.Count; i++)
        {
            txts.Add(T((i + 1).ToString("00"), 1600, t.Accent, bold: true, spaceBefore: i == 0 ? 0 : 16));
            txts.Add(T(items[i], 1800, t.Text, spaceBefore: 2, spacing: 120));
        }
        sb.Append(FitBox(ctx.NextId(), textX, BodyY, textW, BodyH, txts, "侧栏目录"));
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
            sb.Append(FitBox(ctx.NextId(), MX + numW + 152400, y, CW - numW - 152400, rowH,
                [T(items[i], 1800, t.Primary, spacing: 120)], "小结行动项 " + (i + 1), "ctr"));
        }
        var contact = Str(el, "contact");
        if (!string.IsNullOrWhiteSpace(contact))
            sb.Append(FitBox(ctx.NextId(), MX, BodyY + availH, CW, 457200,
                [T(contact, 1400, t.Secondary)], "小结联系方式", "b"));
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
        var availW = colW - _m.Pad * 2;
        var availH = BodyH - _m.Pad * 2;
        var lt = bullets.Select(b => T(b, 1650, t.Text, bullet: "•", spaceBefore: 10, spacing: 125, marL: _m.Gap)).ToList();
        if (lt.Count == 0) lt.Add(T("（本页暂无回顾要点）", 1500, t.Secondary));
        sb.Append(Rect(ctx.NextId(), MX, BodyY, colW, BodyH, t.Light, radius: true));
        sb.Append(FitBox(ctx.NextId(), MX + _m.Pad, BodyY + _m.Pad, availW, availH, lt, "小结回顾"));

        // 右：下一步 / 联系方式（主色卡片，文字一律用底色/反色，保证深底上可读）
        var rightX = MX + colW + gap;
        var actions = StringList(el, "actions");
        if (actions.Count == 0) actions = StringList(el, "next");
        var rt = new List<Txt> { T(Str(el, "rightTitle") ?? "下一步", 2000, t.Bg, bold: true, titleFont: true) };
        for (var i = 0; i < actions.Count; i++)
            rt.Add(T((i + 1) + ". " + actions[i], 1600, t.Bg, spaceBefore: 12, spacing: 125));
        var contact = Str(el, "contact");
        if (!string.IsNullOrWhiteSpace(contact))
            rt.Add(T(contact, 1400, t.OnPrimary, spaceBefore: 20));
        sb.Append(Rect(ctx.NextId(), rightX, BodyY, colW, BodyH, t.Primary, radius: true));
        sb.Append(FitBox(ctx.NextId(), rightX + _m.Pad, BodyY + _m.Pad, availW, availH, rt, "小结行动项"));
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
            string? heading = null;
            var bullets = new List<string>();
            if (el.TryGetProperty(name, out var col) && col.ValueKind == JsonValueKind.Object)
            {
                heading = Str(col, "heading");
                bullets = StringList(col, "bullets");
            }
            // 栏底卡片
            shapes.Append(Rect(ctx.NextId(), x, BodyY, colW, BodyH, t.Light, radius: true));
            if (bullets.Count == 0) bullets.Add("（空）");
            // 两栏是「小标题 + 要点」，要点一多就溢出卡片：按真实字形量高再缩，实在放不下就截断并报警
            var availW = colW - 2 * _m.Pad;
            var availH = BodyH - 2 * _m.Pad;
            var txts = new List<Txt>();
            if (!string.IsNullOrWhiteSpace(heading))
                txts.Add(T(heading, 1900, t.Primary, bold: true, spacing: 110));
            foreach (var b in bullets)
                txts.Add(T(b, 1500, t.Text, bullet: "•", spaceBefore: 10, spacing: 125, marL: _m.Gap));
            shapes.Append(FitBox(ctx.NextId(), x + _m.Pad, BodyY + _m.Pad, availW, availH, txts,
                name == "left" ? "左栏" : "右栏"));
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
        var lineH = (long)(sz * 127.0 * LineHeightEm() * 1.10);
        var avail = Math.Max(100000L, colW - 2 * cellMarH);
        var used = (long)(457200 * s);          // 表头行
        var fit = 0;
        foreach (var row in rows)
        {
            var lines = 1;
            for (var c = 0; c < cols; c++)
                lines = Math.Max(lines, WrappedLines(c < row.Count ? row[c] : "", sz, bold: false, avail));
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

    /// <summary>
    /// 把「要点装不下」的一页拆成多页（与表格分页同一口径：宁可多一页，不丢内容）。
    ///
    /// <para>
    /// 触发条件是<b>缩到下限（12pt / 行距 80%）仍然装不下</b>：
    /// 再缩就不能读了，而直接截断会丢内容。要点页（content / summary / toc）的正文本来就是
    /// 平行的条目，拆页天然安全；标题带「（n/m）」，一眼能看出是续表。
    /// </para>
    ///
    /// <para>
    /// 只处理<b>默认要点版式</b>：容器类页型（grid / kpi / timeline / 示意图…）的条目是嵌在固定卡片里的，
    /// 拆页反而会把版式弄乱 —— 它们走 <see cref="FitBox"/> 的「缩 + 截断 + 报警」。
    /// </para>
    /// </summary>
    private static List<string> ExpandOverflowSlide(string pageJson)
    {
        var single = new List<string> { pageJson };
        JsonDocument doc;
        try { doc = JsonDocument.Parse(pageJson); }
        catch { return single; }

        using (doc)
        {
            var el = doc.RootElement;
            var type = (Str(el, "type") ?? "content").Trim().ToLowerInvariant();
            if (type is not ("content" or "summary" or "toc" or "credits")) return single;
            // 换了版式（如 content + layout:grid）就不拆：那些页型的条目在固定卡里
            if (type == "content" && !string.IsNullOrWhiteSpace(Str(el, "layout"))) return single;
            var variant = VariantOf(el, "list");
            // summary 的 split 版式只拆**左侧回顾栏**（右边的行动项跟到最后一页去）
            var splitSummary = type == "summary" && variant is "split" or "recap";
            if (type == "toc" && variant != "list") return single;
            if (type == "summary" && !splitSummary && variant != "list") return single;

            var key = type is "toc" or "credits" ? "items" : "bullets";
            var items = StringList(el, key);
            if (items.Count == 0) items = StringList(el, type == "toc" ? "bullets" : "items");
            if (items.Count < 2) return single;

            // 拆页要按**这个版式真正的可用空间**算：split 的左栏只有半页宽
            var availW = CW;
            var availH = BodyH;
            // 带配图的要点页会渲染成“文左图右”（见 RenderSlide 的默认分支）：正文只有半页宽，
            // 拆页必须按这个宽度算，否则拆出来的页在半栏里依旧放不下。
            // 这里会真的去检索一次（结果进缓存，后续 PreResolvePhotos / 渲染都直接命中）——
            // 必须先知道“图到底拿没拿到”才能决定版式，所以顺序上把它放在拆页里是故意的。
            if (type == "content" && HasUsablePhoto(el)) availW = CW * 48 / 100 - _m.Gap;
            List<ParaPlan> allPlan;
            Func<string, List<ParaPlan>> planOf;
            if (splitSummary)
            {
                availW = (CW - _m.Gap) / 2 - _m.Pad * 2;
                availH = BodyH - _m.Pad * 2;
                allPlan = items.Select(b => P(b, 1650, 10, 125, _m.Gap)).ToList();
                planOf = b => [P(b, 1650, 10, 125, _m.Gap)];
            }
            else
            {
                allPlan = ItemsPlan(type, items);
                planOf = it => SingleItemPlan(type, it);
            }
            var perPage = ItemsPerPage(items, allPlan, planOf, availW, availH);
            if (perPage >= items.Count) return single;

            var pages = (items.Count + perPage - 1) / perPage;
            var baseTitle = Str(el, "title") ?? (type == "toc" ? "目录" : "");
            var note = Str(el, "note");
            var result = new List<string>(pages);
            for (var p = 0; p < pages; p++)
            {
                var chunk = items.Skip(p * perPage).Take(perPage).ToList();
                var sb = new StringBuilder("{\"type\":").Append(Js(type));
                sb.Append(",\"title\":").Append(Js(baseTitle + "（" + (p + 1) + "/" + pages + "）"));
                sb.Append(',').Append(Js(key)).Append(":[").Append(string.Join(",", chunk.Select(Js))).Append(']');
                // 续页不再带配图：同一张照片连着两页出现反而像出错（拆页前的字段本来就被丢掉了，
                // 除 path/imageQuery 外还有可能被丢掉的展示字段，一并在这里补回）
                if (p == 0)
                {
                    var q = ImageQueryOf(el);
                    if (q.Length > 0) sb.Append(",\"imageQuery\":").Append(Js(q));
                    else if (Str(el, "path") is { Length: > 0 } pp) sb.Append(",\"path\":").Append(Js(pp));
                }
                if (splitSummary)
                {
                    sb.Append(",\"variant\":\"split\"");
                    // 行动项与联系方式只在最后一页出现（回顾栏说完了再收尾）
                    if (p == pages - 1)
                    {
                        var actions = StringList(el, "actions");
                        if (actions.Count == 0) actions = StringList(el, "next");
                        if (actions.Count > 0)
                            sb.Append(",\"actions\":[").Append(string.Join(",", actions.Select(Js))).Append(']');
                        var rt = Str(el, "rightTitle");
                        if (!string.IsNullOrWhiteSpace(rt)) sb.Append(",\"rightTitle\":").Append(Js(rt!));
                        var ct = Str(el, "contact");
                        if (!string.IsNullOrWhiteSpace(ct)) sb.Append(",\"contact\":").Append(Js(ct!));
                    }
                }
                // 备注与脚注只挂第一页，不重复到每一页
                if (p == 0 && !string.IsNullOrWhiteSpace(note)) sb.Append(",\"note\":").Append(Js(note!));
                var notes = Str(el, "notes");
                if (p == 0 && !string.IsNullOrWhiteSpace(notes)) sb.Append(",\"notes\":").Append(Js(notes!));
                sb.Append('}');
                result.Add(sb.ToString());
            }
            return result;
        }
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
        // 用真实字形量行数（与其它页型同一套度量），单元格里的全角/半角混排才不会估错。
        int LinesFor(string text, int size)
        {
            var avail = Math.Max(100000L, colW - 2 * cellMarH);
            return WrappedLines(text, size, bold: false, avail);
        }

        long RowH(int r, double s)   // TableCell 用 lineSpacing=110 → 110% 行距
        {
            var sz = Math.Max(1000, (int)Math.Round(cellSz * s));
            var lineH = (long)(sz * 127.0 * LineHeightEm() * 1.10);
            var lines = 1;
            for (var c = 0; c < cols; c++)
                lines = Math.Max(lines, LinesFor(c < rows[r].Count ? rows[r][c] : "", sz));
            return Math.Max((long)(minRowH * s), lines * lineH + 2 * cellMarV);
        }

        // 字号下限 0.70（约 9pt）：再小就不如不显示
        const double minScale = 0.70;
        long FitTotal(double s)
        {
            long total = (long)(headerH * s);
            for (var r = 0; r < rows.Count; r++) total += RowH(r, s);
            return total;
        }
        var scale = 1.0;
        for (var i = 0; i < 4 && scale > minScale; i++)
        {
            var total = FitTotal(scale);
            if (total <= BodyH) break;
            scale = Math.Max(minScale, scale * ((double)BodyH / total));
        }
        var hdrH = (long)(headerH * scale);
        var szHeader = Math.Max(1000, (int)Math.Round(headerSz * scale));
        var szCell = Math.Max(1000, (int)Math.Round(cellSz * scale));
        var lineHCell = (long)(szCell * 127.0 * LineHeightEm() * 1.10);

        // 只有“缩到下限仍装不下”时才减少行数**并明说**：
        //   一张幻灯片本来就装不下“30 行 × 每格折 4 行”这种东西，硬画出去只会得到看不见的表；
        //   宁可显示能显示的行 + 一行“另有 N 行未显示”，也不静默丢数据。
        //
        // 注意（实测踩坑）：早先的写法是**无条件**给提示行预留一行高度，而已经按“全部行装得下”
        // 选好了缩放 —— 于是每页最后一行都被换成“另有 1 行未显示”，超长表格的自动拆页也就白拆了
        // （行数没丢在切块里，却丢在渲染阶段）。现在改成：先看全部行放得下吗，放不下才留提示行。
        var noteH = Math.Max((long)(minRowH * scale), lineHCell + 2 * cellMarV);
        var shownRows = rows.Count;
        if (FitTotal(scale) > BodyH)
        {
            var used2 = hdrH;
            for (var r = 0; r < rows.Count; r++)
            {
                var h = RowH(r, scale);
                var reserve = r < rows.Count - 1 ? noteH : 0;   // 先给“未显示”提示行留位
                if (used2 + h + reserve > BodyH && r > 0) { shownRows = r; break; }
                used2 += h;
            }
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
            var plan = new List<ParaPlan> { P(it.Value, 3200, 0, 100), P(it.Label, 1300, 6, 115) };
            scale = Math.Min(scale, FitScale(plan, availW, availH));
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
            inner.Append(Para(items[i].Value, EffSize(3200, scale), t.Primary, bold: true, align: "ctr",
                lineSpacing: EffSpacing(100, scale), font: t.FontTitle));
            if (items[i].Label.Length > 0)
                inner.Append(Para(items[i].Label, EffSize(1300, scale), t.Secondary, align: "ctr",
                    spaceBefore: EffSpaceBefore(6, scale), lineSpacing: EffSpacing(115, scale)));
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
            var plan = new List<ParaPlan> { P(it.V1, 4800, 0, 105), P(it.V2, 1400, 8, 115) };
            scale = Math.Min(scale, FitScale(plan, cellW, availH));
        }

        var shapes = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var x = MX + (cellW + _m.Gap) * (i % cols);
            var y = BodyY + (cellH + _m.Gap) * (i / cols);
            var inner = new StringBuilder();
            inner.Append(Para(items[i].V1, EffSize(4800, scale), t.Accent, bold: true, align: "l",
                lineSpacing: EffSpacing(95, scale), font: t.FontTitle));
            if (items[i].V2.Length > 0)
                inner.Append(Para(items[i].V2, EffSize(1400, scale), t.Text, align: "l", spaceBefore: EffSpaceBefore(8, scale),
                    lineSpacing: EffSpacing(115, scale)));
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
            var plan = new List<ParaPlan> { P(s.V1, 1600, 0, 110), P(s.V2, 1250, 6, 115) };
            scale = Math.Min(scale, FitScale(plan, slotW, textH));
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
                Para((i + 1).ToString("00"), EffSize(1500, scale), t.OnAccent, bold: true, align: "ctr"), anchor: "ctr"));

            var txt = new StringBuilder();
            txt.Append(Para(steps[i].V1, EffSize(1600, scale), t.Primary, bold: true, align: "ctr",
                lineSpacing: EffSpacing(110, scale)));
            if (steps[i].V2.Length > 0)
                txt.Append(Para(steps[i].V2, EffSize(1250, scale), t.Text, align: "ctr", spaceBefore: EffSpaceBefore(6, scale),
                    lineSpacing: EffSpacing(115, scale)));
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
            var plan = new List<ParaPlan> { P(cards[i].V1, 1700, 0, 110), P(cards[i].V2, 1300, 8, 120) };
            var scale = FitScale(plan, availW, availH);

            var inner = new StringBuilder();
            if (cards[i].V1.Length > 0)
                inner.Append(Para(cards[i].V1, EffSize(1700, scale), t.Primary, bold: true, align: "l",
                    lineSpacing: EffSpacing(110, scale)));
            if (cards[i].V2.Length > 0)
                inner.Append(Para(cards[i].V2, EffSize(1300, scale), t.Text, align: "l", spaceBefore: EffSpaceBefore(8, scale),
                    lineSpacing: EffSpacing(120, scale)));

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
            var plan = new List<ParaPlan> { P(r.V1, 1600, 0, 110), P(r.V2, 1300, 6, 120) };
            scale = Math.Min(scale, FitScale(plan, textW, availH));
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
                    Para(mark, EffSize(1800, scale), t.OnAccent, bold: true, align: "ctr"), anchor: "ctr"));
            }

            var inner = new StringBuilder();
            if (rows[i].V1.Length > 0)
                inner.Append(Para(rows[i].V1, EffSize(1600, scale), t.Primary, bold: true, align: "l",
                    lineSpacing: EffSpacing(110, scale)));
            if (rows[i].V2.Length > 0)
                inner.Append(Para(rows[i].V2, EffSize(1300, scale), t.Text, align: "l", spaceBefore: EffSpaceBefore(6, scale),
                    lineSpacing: EffSpacing(120, scale)));
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

    // ---- 示意图（金字塔 / 漏斗 / 四象限 / 循环 / 层叠）----
    //
    // “插图”在商务稿里多半指这类**示意图**:不靠照片，而是用形状把关系画出来。
    // 全部用 DrawingML 预设几何（trapezoid / rect / ellipse / triangle）拼出来，
    // 不写自定义几何（custGeom）、不需要任何外部素材、不用联网，因此产出确定、无版权问题。

    /// <summary>
    /// 任意 DrawingML 预设几何形状。<paramref name="rot"/> 单位是 1/60000 度（顺时针）；
    /// <paramref name="adj"/> 是形状调整值（如梯形斜边收进量）。
    /// </summary>
    private static string Preset(int id, string prst, long x, long y, long cx, long cy, string? fill,
        int rot = 0, string? adj = null, string? line = null, long lineW = 0, int alpha = 100)
    {
        var sb = new StringBuilder();
        sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id).Append("\" name=\"Shape ").Append(id).Append("\"/>").Append("<p:cNvSpPr/><p:nvPr/></p:nvSpPr>")
          .Append("<p:spPr><a:xfrm");
        if (rot != 0) sb.Append(" rot=\"").Append(rot).Append("\"");
        sb.Append("><a:off x=\"").Append(x).Append("\" y=\"").Append(y).Append("\"/><a:ext cx=\"")
          .Append(cx).Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm>")
          .Append("<a:prstGeom prst=\"").Append(prst).Append("\">");
        if (adj is null) sb.Append("<a:avLst/>");
        else sb.Append("<a:avLst><a:gd name=\"adj\" fmla=\"val ").Append(adj).Append("\"/></a:avLst>");
        sb.Append("</a:prstGeom>");
        // fill == null → 只描边不填充（如循环图的环）
        if (fill is null) sb.Append("<a:noFill/>");
        else
        {
            sb.Append("<a:solidFill>");
            if (alpha < 100) sb.Append("<a:srgbClr val=\"").Append(BareHex(fill)).Append("\"><a:alpha val=\"").Append(alpha * 1000).Append("\"/></a:srgbClr>");
            else sb.Append(Rgb(fill));
            sb.Append("</a:solidFill>");
        }
        if (line is null) sb.Append("<a:ln><a:noFill/></a:ln>");
        else sb.Append("<a:ln w=\"").Append(lineW <= 0 ? 12700 : lineW).Append("\"><a:solidFill>")
                  .Append(Rgb(line)).Append("</a:solidFill></a:ln>");
        sb.Append("</p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p/></p:txBody></p:sp>");
        return sb.ToString();
    }

    /// <summary>画在 <paramref name="fill"/> 底色上的可读文字色（黑或白）。</summary>
    private static string OnFill(string fill) => OnColor(fill, "FFFFFF");

    /// <summary>示意图的层颜色：相邻层用主色/强调色交替，保证层次看得清又不花。</summary>
    private static string LayerColor(Theme t, int i) => i % 2 == 0 ? t.Primary : t.Accent;

    /// <summary>
    /// 梯形层的 adj 值：OOXML 的 trapezoid 把“斜边收进量”定义为 <c>min(w,h) * adj / 100000</c>。
    /// 想让上下两层刚好衔接（斜边连续），收进量就是上下宽度差的一半。
    /// </summary>
    private static string SlopeAdj(long w, long h, long inset)
    {
        var basis = Math.Max(1, Math.Min(w, h));
        var adj = (long)Math.Round(inset * 100000.0 / basis);
        return Math.Max(0, Math.Min(50000, adj)).ToString();
    }

    /// <summary>金字塔 / 分层（3~6 层，顶层最窄）：适合“分层策略 / 成熟度模型 / 价值层级”。</summary>
    private static string PyramidBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var rows = Items(el, ["title", "label", "name"], ["text", "detail", "desc"], []);
        if (rows.Count == 0) return BulletBody(el, ctx, accent: false);
        var n = Math.Min(rows.Count, 6);

        var layerH = Math.Min(BodyH / n, BodyH);
        var y0 = BodyY + (BodyH - layerH * n) / 2;
        // 顶层 46% 宽 → 底层 100% 宽，等差递增
        const double topRatio = 0.46;
        var sb = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            var frac = n == 1 ? 1.0 : topRatio + (1 - topRatio) * i / (n - 1);
            var wNext = i + 1 < n ? topRatio + (1 - topRatio) * (i + 1) / (n - 1) : frac;
            var w = (long)(CW * frac);
            var wb = (long)(CW * wNext);
            var x = MX + (CW - w) / 2;
            var y = y0 + i * layerH;
            var gap = Sz(22860);
            var cy = layerH - gap;
            // 斜边连续：本层下边要接到下一层上边
            var adj = SlopeAdj(w, cy, Math.Max(0, (wb - w) / 2));
            var fill = LayerColor(t, i);
            sb.Append(Preset(ctx.NextId(), "trapezoid", x, y, w, cy, fill, adj: adj));
            var label = rows[i].V1;
            var detail = rows[i].V2;
            // 梯形是上窄下宽：能容下文字的是**较窄那条边**，按它算可用宽度才不会压到斜边外
            var innerW = Math.Min(w, wb) - _m.Pad * 2;
            var txts = new List<Txt> { T(label, 1600, OnFill(fill), bold: true, align: "ctr", spacing: 110) };
            if (detail.Length > 0)
                txts.Add(T(detail, 1200, OnFill(fill), align: "ctr", spaceBefore: 4, spacing: 115));
            sb.Append(FitBox(ctx.NextId(), x + _m.Pad, y, innerW, cy, txts, "金字塔第 " + (i + 1) + " 层", "ctr"));
        }
        return sb.ToString();
    }

    /// <summary>漏斗（3~6 层，顶层最宽）：适合“转化率 / 逐步筛选 / 收敛流程”。</summary>
    private static string FunnelBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var rows = Items(el, ["title", "label", "name"], ["text", "detail", "value"], []);
        if (rows.Count == 0) return BulletBody(el, ctx, accent: false);
        var n = Math.Min(rows.Count, 6);

        var layerH = Math.Min(BodyH / n, BodyH);
        var y0 = BodyY + (BodyH - layerH * n) / 2;
        var rightW = CW * 26 / 100;                 // 右侧放数值/说明
        var areaW = CW - rightW - _m.Gap;
        var x0 = MX;
        const double bottomRatio = 0.34;
        var sb = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            var frac = n == 1 ? 1.0 : 1.0 - (1 - bottomRatio) * i / (n - 1);
            var fracNext = i + 1 < n ? 1.0 - (1 - bottomRatio) * (i + 1) / (n - 1) : frac;
            var w = (long)(areaW * frac);
            var wn = (long)(areaW * fracNext);
            var x = x0 + (areaW - w) / 2;
            var y = y0 + i * layerH;
            var cy = layerH - Sz(22860);
            var adj = SlopeAdj(w, cy, Math.Max(0, (w - wn) / 2));
            var fill = LayerColor(t, i);
            sb.Append(Preset(ctx.NextId(), "trapezoid", x, y, w, cy, fill, adj: adj));
            var shapeW = Math.Min(w, wn) - _m.Pad * 2;
            sb.Append(FitBox(ctx.NextId(), x + _m.Pad, y, shapeW, cy,
                [T(rows[i].V1, 1500, OnFill(fill), bold: true, align: "ctr", spacing: 110)],
                "漏斗第 " + (i + 1) + " 层", "ctr"));
            var detail = rows[i].V2;
            if (detail.Length > 0)
                sb.Append(FitBox(ctx.NextId(), x0 + areaW + _m.Gap, y, rightW, cy,
                    [T(detail, 1500, t.Text, spacing: 120)], "漏斗数值列 " + (i + 1), "ctr"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 四象限矩阵：适合“优先级 / 取舍 / 分类”。
    /// 轴名用 <c>xTitle</c>/<c>yTitle</c>，轴端用 <c>xLeft</c>/<c>xRight</c>/<c>yTop</c>/<c>yBottom</c>；
    /// <c>items</c> 按阅读顺序（左上→右上→左下→右下）对应四个象限。
    /// </summary>
    private static string MatrixBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var rows = Items(el, ["title", "label", "name"], ["text", "detail", "desc"], []);
        var gap = _m.Gap;
        var axisW = Sz(457200);
        var gridX = MX + axisW;
        var gridY = BodyY + Sz(274320);
        var gridW = CW - axisW;
        var gridH = BodyH - Sz(274320);
        var cellW = (gridW - gap) / 2;
        var cellH = (gridH - gap) / 2;

        var sb = new StringBuilder();
        // 四个象限：左上/右上用浅底，左下/右下深浅交替，便于区分
        for (var i = 0; i < 4; i++)
        {
            var c = i % 2;
            var r = i / 2;
            var x = gridX + c * (cellW + gap);
            var y = gridY + r * (cellH + gap);
            sb.Append(Rect(ctx.NextId(), x, y, cellW, cellH, i is 0 or 3 ? t.Light : t.Bg, radius: true,
                alpha: 100));
            sb.Append(Rect(ctx.NextId(), x, y, Sz(45720), cellH, t.Accent));
            if (i < rows.Count)
            {
                var txts = new List<Txt>();
                if (rows[i].V1.Length > 0)
                    txts.Add(T(rows[i].V1, 1700, t.Primary, bold: true, spacing: 110));
                if (rows[i].V2.Length > 0)
                    txts.Add(T(rows[i].V2, 1300, t.Text, spaceBefore: 8, spacing: 122));
                sb.Append(FitBox(ctx.NextId(), x + _m.Pad, y + _m.Pad, cellW - _m.Pad * 2, cellH - _m.Pad * 2,
                    txts, "四象限 " + (i + 1), "ctr"));
            }
        }
        // 轴名与轴端标签
        var yTitle = Str(el, "yTitle");
        if (!string.IsNullOrWhiteSpace(yTitle))
            // 宽度给足整幅内容区：这里只有 21pt 的竖向空间，若把宽度限成轴宽（36pt），
            // “业务价值”这种 4 字标签会被折成两行而溢出（实测被自检的 textOverflow 抳到）。
            sb.Append(FitBox(ctx.NextId(), MX, gridY - Sz(274320), CW, Sz(274320),
                [T(yTitle, 1200, t.Secondary)], "纵轴名称", "b"));
        var xTitle = Str(el, "xTitle");
        if (!string.IsNullOrWhiteSpace(xTitle))
            sb.Append(FitBox(ctx.NextId(), gridX, gridY + gridH, gridW, Sz(274320),
                [T(xTitle, 1200, t.Secondary, align: "ctr")], "横轴名称", "t"));
        var xl = Str(el, "xLeft");
        var xr = Str(el, "xRight");
        if (!string.IsNullOrWhiteSpace(xl) || !string.IsNullOrWhiteSpace(xr))
            sb.Append(FitBox(ctx.NextId(), gridX, gridY + gridH, gridW, Sz(228600),
                [T((xl ?? "") + "　　" + (xr ?? ""), 1100, t.Secondary, align: "ctr")], "轴端标签", "t"));
        return sb.ToString();
    }

    /// <summary>环形循环（3~6 步）：适合“迭代 / PDCA / 闭环流程”。中心可用 <c>center</c> 写一句话。</summary>
    private static string CycleBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var rows = Items(el, ["title", "label", "name"], ["text", "detail", "desc"], []);
        if (rows.Count == 0) return BulletBody(el, ctx, accent: false);
        // 不能写成 Math.Max(3, …)：只给了 1~2 项时会把 n 抬到 3，但 items 里没那么多，
        // 后面 rows[i] 直接越界（实测被“每个页型都真的接通了”那个用例抳到）。
        var n = Math.Min(rows.Count, 6);

        var d = Math.Min(BodyH, CW) * 68 / 100;      // 环直径
        var cx = MX + CW / 2;
        var cy = BodyY + BodyH / 2;
        var radius = d / 2;
        var nodeD = d * 34 / 100;                    // 节点圆直径
        var sb = new StringBuilder();

        // 环：只描边不填充。没它的时候整页只有几个圆点，显得很空（实测墨迹占比仅 3.3%）。
        sb.Append(Preset(ctx.NextId(), "ellipse", cx - radius, cy - radius, d, d, null,
            line: t.Light, lineW: Sz(19050)));

        // 节点圆均匀分布（12 点方向起步），节点之间放一个切向箭头表示“继续往下走”
        for (var i = 0; i < n; i++)
        {
            var ang = -Math.PI / 2 + i * 2 * Math.PI / n;
            var nx = cx + (long)(radius * Math.Cos(ang));
            var ny = cy + (long)(radius * Math.Sin(ang));
            var fill = i % 2 == 0 ? t.Primary : t.Accent;
            sb.Append(Ellipse(ctx.NextId(), nx - nodeD / 2, ny - nodeD / 2, nodeD, nodeD, fill));
            // 圆内可用宽度是内接正方形（≈ 直径 × 0.707），不按直径算，否则文字会顶出圆外
            var inW = (long)(nodeD * 0.72);
            var inH = (long)(nodeD * 0.72);
            sb.Append(FitBox(ctx.NextId(), nx - inW / 2, ny - inH / 2, inW, inH,
                [T(rows[i].V1, 1400, OnFill(fill), bold: true, align: "ctr", spacing: 106)],
                "闭环节点 " + (i + 1), "ctr"));

            // 说明文字：放在节点外侧的放射方向，按需要夹在画布内
            var detail = rows[i].V2;
            if (detail.Length > 0)
            {
                var boxW = Sz(1905000);
                var boxH = Sz(457200);
                var ox = cx + (long)(radius * 1.30 * Math.Cos(ang)) - boxW / 2;
                var oy = cy + (long)(radius * 1.30 * Math.Sin(ang)) - boxH / 2;
                ox = Math.Max(MX, Math.Min(W - MX - boxW, ox));
                oy = Math.Max(BodyY, Math.Min(BodyY + BodyH - boxH, oy));
                sb.Append(FitBox(ctx.NextId(), ox, oy, boxW, boxH,
                    [T(detail, 1200, t.Secondary, align: "ctr", spacing: 115)], "闭环说明 " + (i + 1), "ctr"));
            }

            // 箭头：放在两个节点之间的环上，按切线方向旋转
            var mid = ang + Math.PI / n;
            var ax = cx + (long)(radius * Math.Cos(mid));
            var ay = cy + (long)(radius * Math.Sin(mid));
            var arrow = d * 9 / 100;
            var rotDeg = (mid + Math.PI / 2) * 180.0 / Math.PI + 90;   // 三角形默认朝上
            sb.Append(Preset(ctx.NextId(), "triangle", ax - arrow / 2, ay - arrow / 2, arrow, arrow,
                t.Accent, rot: (int)Math.Round(rotDeg * 60000)));
        }

        var center = Str(el, "center");
        if (!string.IsNullOrWhiteSpace(center))
            sb.Append(FitBox(ctx.NextId(), cx - radius * 45 / 100, cy - d * 12 / 100, radius * 90 / 100, d * 24 / 100,
                [T(center, 1600, t.Primary, bold: true, align: "ctr", spacing: 115)], "闭环中心", "ctr"));
        return sb.ToString();
    }

    /// <summary>层叠架构（纵向分层条）：适合“技术架构 / 能力分层 / 从下到上的支撑关系”。</summary>
    private static string StackBody(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var rows = Items(el, ["title", "label", "name"], ["text", "detail", "desc"], []);
        if (rows.Count == 0) return BulletBody(el, ctx, accent: false);
        var n = Math.Min(rows.Count, 6);

        var gap = Sz(114300);
        var barH = Math.Min((BodyH - gap * (n - 1)) / n, BodyH);
        var totalH = barH * n + gap * (n - 1);
        var y0 = BodyY + (BodyH - totalH) / 2;
        var labelW = CW * 24 / 100;
        var sb = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            var y = y0 + i * (barH + gap);
            var fill = LayerColor(t, i);
            sb.Append(Rect(ctx.NextId(), MX, y, CW, barH, fill, radius: true));
            sb.Append(FitBox(ctx.NextId(), MX + _m.Pad, y, labelW, barH,
                [T(rows[i].V1, 1600, OnFill(fill), bold: true, spacing: 110)], "分层架构层名 " + (i + 1), "ctr"));
            if (rows[i].V2.Length > 0)
                sb.Append(FitBox(ctx.NextId(), MX + labelW + _m.Pad, y, CW - labelW - _m.Pad * 2, barH,
                    [T(rows[i].V2, 1300, OnFill(fill), spacing: 118)], "分层架构说明 " + (i + 1), "ctr"));
        }
        return sb.ToString();
    }

    // ---- 自动生成的题图（零素材、零联网）----

    /// <summary>
    /// 按主题配色**程序化生成**的抽象题图：用于封面/章节页/配图位没有真图时的位置。
    ///
    /// <para>
    /// 为什么值得有：以前“没提供图片”就只能画一块纯色（或写“图片不存在”），
    /// 于是稿子看起来还是“一页色块”。题图用形状拼出一个稳定的抽象构图，
    /// **不靠任何外部素材、不联网、无版权问题**，而且颜色跟着主题走。
    /// </para>
    ///
    /// <para>
    /// 设计约束：只用实色（无渐变，符合 design-system.md 的硬规则）；
    /// 透明度只用 <c>a:alpha</c>；构图由种子确定 —— 同一标题每次生成的图一样，
    /// 不会“每次重导出都不一样”。
    /// </para>
    /// </summary>
    private static string HeroArt(SlideCtx ctx, long x, long y, long cx, long cy, string seed)
    {
        var t = ctx.Theme;
        var dark = IsDarkBg(t);
        var baseFill = dark ? t.Primary : t.Secondary;
        var sb = new StringBuilder();
        sb.Append(Rect(ctx.NextId(), x, y, cx, cy, baseFill));

        // 确定性伪随机：用种子字符串算一个稳定的数列（不依赖 Random，避免每次导出都不一样）
        var h = 17;
        foreach (var ch in seed ?? "") h = (h * 31 + ch) & 0x7fffffff;
        int Next(int mod) { h = (h * 1103515245 + 12345) & 0x7fffffff; return (int)(h % mod); }

        // 所有形状都必须**完全落在传入矩形内**：这块图有时只占半页（split 版式），
        // 一旦画出边界，既会被自检报 overflow，也会在别人打开时“跑到页面外”。
        // （最初版本用旋转矩形做斜带、圆也不限位，自检抳到了——见 README 的实测记录。）
        var x2 = x + cx;
        var y2 = y + cy;

        // 1) 两条斜带：用 parallelogram 自带斜边，**不靠旋转**（旋转后的包围盒会超框）
        for (var i = 0; i < 2; i++)
        {
            var bw = cx * (45 + Next(35)) / 100;
            var bh = Math.Max(1, cy * (10 + Next(8)) / 100);
            var bx = x + cx * Next(40) / 100;
            var by = y + cy * (i == 0 ? 14 + Next(20) : 58 + Next(20)) / 100;
            bx = Math.Min(bx, x2 - bw);
            if (by + bh > y2) by = y2 - bh;
            sb.Append(Preset(ctx.NextId(), "parallelogram", bx, by, bw, bh,
                i == 0 ? t.Accent : t.Light, adj: "30000", alpha: i == 0 ? 65 : 50));
        }

        // 2) 几个大圆：中心与半径都限位在框内（可以贴边，但不能越界）
        for (var i = 0; i < 3; i++)
        {
            var d = Math.Max(1, Math.Min(cx, cy) * (42 + Next(34)) / 100);
            var r = d / 2;
            var minCx = x + r;
            var maxCx = x2 - r;
            var minCy = y + r;
            var maxCy = y2 - r;
            var ccx = maxCx > minCx ? minCx + (long)((maxCx - minCx) * (Next(100) / 100.0)) : x + cx / 2;
            var ccy = maxCy > minCy ? minCy + (long)((maxCy - minCy) * (Next(100) / 100.0)) : y + cy / 2;
            sb.Append(Ellipse(ctx.NextId(), ccx - r, ccy - r, d, d,
                i % 2 == 0 ? t.Accent : t.Light, alpha: 26 + i * 9));
        }

        // 3) 点阵：右下角一片小圆点（给“科技/数据”的感觉）
        var step = Math.Max(1, Math.Min(cx, cy) / 16);
        var dot = Math.Max(1, step / 6);
        var cols = (int)Math.Min(9, Math.Max(1, cx / step - 1));
        var rows = (int)Math.Min(5, Math.Max(1, cy / step - 1));
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                var dx = x2 - (cols - c) * step;
                var dy = y2 - (rows - r) * step;
                sb.Append(Ellipse(ctx.NextId(), dx, dy, dot, dot, t.Light, alpha: 55));
            }
        return sb.ToString();
    }

    /// <summary>题图页：整页自动生成的抽象图，可叠标题与副标题。</summary>
    private static string HeroSlide(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var title = Str(el, "title") ?? ctx.Title;
        var shapes = new List<string> { HeroArt(ctx, 0, 0, W, H, title) };
        var sub = Str(el, "subtitle") ?? ctx.Subtitle;
        var txts = new List<Txt> { T(title, 3800, t.Bg, titleFont: true, spacing: 106) };
        if (!string.IsNullOrWhiteSpace(sub))
            txts.Add(T(sub, 1600, t.OnPrimary, spaceBefore: 14, spacing: 128));
        shapes.Add(FitBox(ctx.NextId(), MX, 2457450, CW * 70 / 100, 2286000, txts, "题图标题", "ctr"));
        shapes.Add(PageBadge(ctx));
        return SlideXml(t.Bg, shapes);
    }

    // ---- 联网配图（Wikimedia Commons）----
    //
    // 为什么要联网取图：示意图与题图都是**图形**，不是照片。用户要“每页配一张符合内容的图”时
    // 指的是照片级画面，那只能来自图库。选 Commons 的理由：**免密钥**、素材全部是自由许可
    // （CC0 / CC BY / CC BY-SA / PD）、有稳定的公开 API。代价是**必须署名**：只要用了检索来的
    // 照片，就自动在稿末追加「图片来源」页（见 CreditsSlide）—— 这是合规要求，不是可选装饰。
    //
    // 失败一律降级：无网络 / 无结果 / 格式不认 → 退回自动题图 + Warn，绝不让一次联网失败
    // 把整份稿子拖垮。另外做了**熔断**：第一次连接级失败后本轮不再尝试 ——
    // 否则“离线环境 + 每页一张图”会变成每页一次超时，十页的稿子要等几分钟。

    private const string CommonsSearchApi = "https://commons.wikimedia.org/w/api.php";

    /// <summary>
    /// 环境变量 <c>AGUI_PHOTO_API</c>：把检索端点改成自建代理 / 镜像（入参 <c>imageSearchApi</c> 优先于它）。
    ///
    /// <para>
    /// 为何必须留这个口子：Wikimedia 在部分网络里不可达 —— 实测本环境容器能访问 example.com（200）
    /// 与 api.nuget.org（302），但 en.wikipedia.org 与 upload.wikimedia.org 都返回 000（被拦）。
    /// 这类部署要么给容器配 <c>HTTPS_PROXY</c>（HttpClient 会读该环境变量），要么把端点指到镜像。
    /// </para>
    /// </summary>
    private const string PhotoApiEnvVar = "AGUI_PHOTO_API";
    private const string PhotoUserAgent = "AguiGroupChat-PptxDeck/1.0 (slide illustration; Wikimedia Commons API)";
    private const int PhotoSearchTimeoutSec = 8;
    private const int PhotoDownloadTimeoutSec = 12;
    /// <summary>
    /// 一次生成里花在取图上的总时间上限（秒）。
    ///
    /// <para>
    /// 为何要有：技能执行有<b>硬预算</b>（内置技能默认 60 秒，见 AgentOptions.BuiltinSkillTimeoutMs），
    /// 而超时的后果是**整份稿子都没有**，比“剩下几页降级为题图”差得多。
    /// 所以取图自己先封顶，把剩下的时间留给排版与落盘。
    /// </para>
    /// </summary>
    private const int PhotoBudgetSec = 35;
    private const long PhotoMaxBytes = 12L * 1024 * 1024;
    /// <summary>向图库请求的缩略图宽度：原图常常几十 MB，而幻灯片用不到那么多像素。</summary>
    private const int PhotoWidth = 1600;
    /// <summary>「图片来源」页的字号：比正文小，长 URL 也能一行放下。</summary>
    private const int CreditSize = 1050;

    /// <summary>一张检索来的照片：本地缓存文件 + 署名所需的元数据。</summary>
    private sealed class Photo
    {
        public string Path = "";
        public string FileName = "";
        public string Author = "";
        public string License = "";
        public string PageUrl = "";
        public string Query = "";
        /// <summary>来源：<c>library</c>（团队图库，自有素材、不需署名）| <c>commons</c>（Wikimedia，CC 需署名）。</summary>
        public string Source = "commons";
        /// <summary>来自图库时的图库名（回显与排障用）。</summary>
        public string LibraryName = "";
        /// <summary>来自图库时的图片描述（视觉模型生成，方便校对“配得对不对”）。</summary>
        public string Caption = "";
        /// <summary>平台检索给出的相似度得分（图库才有；网图记 0）—— 用于在多个候选间择优。</summary>
        public double Score;

        /// <summary>「图片来源」页里的一行。顺序按“最该留住的排前面”：截断时先丢链接而不是先丢作者。</summary>
        public string CreditLine()
        {
            var sb = new StringBuilder(FileName);
            sb.Append(" — ").Append(Author.Length > 0 ? Author : "（未标注作者）");
            sb.Append(" — ").Append(License.Length > 0 ? License : "Wikimedia Commons");
            if (PageUrl.Length > 0) sb.Append(" — ").Append(PageUrl);
            return sb.ToString();
        }
    }

    /// <summary>同一关键词一份的检索缓存（关键词 → 照片；null = 查过了、没有）。</summary>
    [ThreadStatic] private static Dictionary<string, Photo?>? _photoCache;
    /// <summary>本次生成用到的照片（按原页面去重），用于生成「图片来源」页与返回 JSON。</summary>
    [ThreadStatic] private static List<Photo>? _photos;
    /// <summary>检索端点覆盖（离线部署 / 自建代理 / 测试注入）。</summary>
    [ThreadStatic] private static string? _photoApi;
    /// <summary>熔断：连接级失败后不再逐页重试。</summary>
    [ThreadStatic] private static bool _photoOffline;
    /// <summary>取图总时间预算的截止点（Environment.TickCount64）。</summary>
    [ThreadStatic] private static long _photoDeadline;
    /// <summary>预算耗尽的提示只报一次。</summary>
    [ThreadStatic] private static bool _photoBudgetWarned;
    /// <summary>配图来源策略：auto（默认，先图库后网络）| library（仅图库，不出网）| network（仅网络）。</summary>
    [ThreadStatic] private static string? _imageSource;
    /// <summary>平台注入的图库检索范围句柄（没有 = 没有可用的图库，直接走网络）。</summary>
    [ThreadStatic] private static string? _imageScopeId;

    private const string SelfBaseEnv = "AGUI_SELF_BASE";
    private const string SelfTokenEnv = "AGUI_SELF_TOKEN";
    private const int LibrarySearchTimeoutSec = 10;

    /// <summary>
    /// 图库检索的分数门槛（传给平台 <c>/ag-ui/images/search</c> 的 <c>minScore</c>）。
    ///
    /// <para>
    /// 为何要显式指定：平台默认 0.25 太松，而实测（公司生活照库 18 张，bge-m3）无意义关键词的
    /// 最高分能到 <b>0.44~0.55</b>（“qzxv-不存在-9987”→2Vivi.png 0.4412；“一只在雪地里的猫”→0.5475），
    /// 而真实命中是 <b>0.62~0.84</b>（“团队协作 会议”→0.8225、“城市 日落”→0.8411）。
    /// 用 0.25 就会把不相干的人物照当作“命中”嵌进幻灯片 —— 比降级成题图差得多。
    /// </p>
    ///
    /// <para>阈值以下不静默：走降级路径并报 warnings（关键词写出来，用户能自己改词重试）。</para>
    /// </summary>
    private const double LibraryMinScore = 0.60;

    /// <summary>是否允许联网取图（imageSource=library 时彻底不出网，内网部署用）。</summary>
    private static bool NetworkImagesAllowed
        => !string.Equals(_imageSource, "library", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否允许查团队图库（imageSource=network 时只走网络）。</summary>
    private static bool LibraryImagesAllowed
        => !string.Equals(_imageSource, "network", StringComparison.OrdinalIgnoreCase);

    private static List<Photo> Photos => _photos ??= new List<Photo>();

    private static string PhotoCacheDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agui-commons-photos");
        try { Directory.CreateDirectory(dir); } catch { /* 建不了就用临时目录本身 */ }
        return dir;
    }

    /// <summary>
    /// 取一张图：① 团队图库（自有素材，不需署名）→ ② Wikimedia Commons（可自由使用）。
    /// 下载到本地缓存后返回；都取不到（无图库 / 无结果 / 无网络）返回 null —— 调用方降级为题图并记 warning。
    ///
    /// <para>
    /// <paramref name="libraryOnly"/>：只查团队图库、不出网（用于 <see cref="ImagePathOf"/> 的二次尝试）。
    /// <paramref name="label"/>：失败信息里这串字的叫法（模型给的叫“关键词”，本页文字就叫“本页文字”）。
    /// <paramref name="warn"/>：没配上时的降级提示（<b>不写进 warnings</b>）—— 由调用方决定报不报：
    /// 见 <see cref="ImagePathOf"/>，二次尝试配上图时就不能再报“已降级为题图”（那是假消息）。
    /// </para>
    /// </summary>
    private static Photo? ResolvePhoto(string query, bool libraryOnly, string label, out string? warn)
    {
        warn = null;
        var key = (query ?? "").Trim();
        if (key.Length == 0) return null;
        _photoCache ??= new Dictionary<string, Photo?>(StringComparer.OrdinalIgnoreCase);
        // 缓存键带上模式：
        // “仅图库”试过的结果不能当作“图库+网络”的结果（否则同一串文字先被二次尝试缓存成 null，后面就不再联网了）
        var cacheKey = (libraryOnly ? "L|" : "A|") + key;
        if (_photoCache.TryGetValue(cacheKey, out var hit)) return hit;   // 命中缓存不受预算限制（提示已在首次报过）
        // 离网标记只代表“外网取不到图”：团队图库走的是本机回环回调（不需要外网），
        // 所以只要图库还可用，就不能因为这个标记直接放弃 —— 否则在无外网部署里，
        // 第一页把标记置上之后，后续所有页连图库都不会再查了。
        var allowNet = NetworkImagesAllowed && !libraryOnly && !_photoOffline;
        if (!allowNet && !LibraryImagesAllowed) return null;
        if (_photoDeadline > 0 && Environment.TickCount64 > _photoDeadline)
        {
            if (!_photoBudgetWarned)
            {
                _photoBudgetWarned = true;
                Warn("配图已用完全部时间预算（" + PhotoBudgetSec + " 秒），其余配图改用自动生成的题图");
            }
            return null;
        }

        Photo? photo = null;
        var reasons = new List<string>();
        // ① 团队图库优先：命中就是自有素材（不需要 CC 署名），也完全不依赖外网
        if (LibraryImagesAllowed)
        {
            try
            {
                photo = ResolveFromLibrary(key, out var libWhy);
                if (photo is null && libWhy is not null) reasons.Add("图库：" + libWhy);
            }
            catch (Exception ex) { reasons.Add("图库：" + ex.GetType().Name + "：" + ex.Message); }
        }
        // ② 库内没有（或没图库）→ 回落网络；imageSource=library 时彻底不出网；
        //    二次尝试（libraryOnly）不走网络：那是“用本页文字”的补充尝试，把中文页面文字丢给
        //    Wikimedia 既不会有结果、也会白耗预算。
        if (photo is null && allowNet)
        {
            try
            {
                photo = SearchAndDownload(key, out var netWhy);
                if (photo is null) reasons.Add("网络：" + (netWhy ?? "未找到合适的图片"));
            }
            catch (Exception ex) { reasons.Add("网络：" + ex.GetType().Name + "：" + ex.Message); }
        }
        else if (photo is null && !libraryOnly && !NetworkImagesAllowed)
        {
            reasons.Add("已配置为仅用团队图库（imageSource=library），不联网取图");
        }

        if (photo is null)
        {
            var why = reasons.Count > 0 ? string.Join("；", reasons) : "未找到合适的图片";
            warn = "配图检索未成功，已改用自动生成的题图：" + label + "“" + key + "”（" + why + "）【端点：" + PhotoEndpoint() + "】";
            if (reasons.Any(r => r.Contains("无法连接", StringComparison.Ordinal))) _photoOffline = true;
        }
        else if (!Photos.Any(p => string.Equals(p.Path, photo.Path, StringComparison.OrdinalIgnoreCase)))
        {
            Photos.Add(photo);
        }
        _photoCache[cacheKey] = photo;
        return photo;
    }

    /// <summary>
    /// 查<b>团队图库</b>：技能经回环 HTTP 调平台的 <c>/ag-ui/images/search</c>，拿回一张图片的**本地路径**。
    ///
    /// <para>
    /// 句柄（<c>imageScopeId</c>）由平台在调用本技能前注入，技能只带句柄、不带图库 ID ——
    /// 入参是模型生成的，若能自报图库 ID 就能越权读别人的图。没有句柄 = 没有可用图库，静默跳过。
    /// </para>
    ///
    /// <para>取不到返回 null 并给出原因（原因会汇总进 warnings，不静默）。</para>
    /// </summary>
    private static Photo? ResolveFromLibrary(string query, out string? why)
    {
        why = null;
        var scope = _imageScopeId;
        if (string.IsNullOrWhiteSpace(scope)) return null;   // 没注入句柄：环境里没有图库，不必报错
        var baseUrl = Environment.GetEnvironmentVariable(SelfBaseEnv);
        var token = Environment.GetEnvironmentVariable(SelfTokenEnv);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            why = "未拿到平台回调地址（AGUI_SELF_BASE / AGUI_SELF_TOKEN 未注入）";
            return null;
        }

        var body = "{\"query\":" + Js(query) + ",\"scopeHandle\":" + Js(scope!) + ",\"topK\":3,\"minScore\":"
                 + LibraryMinScore.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        var json = HttpPostJson(baseUrl!.TrimEnd('/') + "/ag-ui/images/search", body, token!, LibrarySearchTimeoutSec, out why);
        if (json is null) return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("images", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            why = "平台返回格式异常";
            return null;
        }
        foreach (var img in arr.EnumerateArray())
        {
            var path = Str(img, "path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            return new Photo
            {
                Path = path!,
                FileName = Str(img, "fileName") ?? Path.GetFileName(path!),
                Source = "library",
                LibraryName = Str(img, "libName") ?? "",
                Caption = Str(img, "caption") ?? "",
                Query = query,
                Score = NumOf(img, "score", 0),
            };
        }
        why = "图库里没有匹配的图片";
        return null;
    }

    /// <summary>POST JSON 并读回响应体（与 GET 同一套超时/限流重试策略）。</summary>
    private static string? HttpPostJson(string url, string jsonBody, string token, int timeoutSec, out string? why)
    {
        for (var attempt = 1; ; attempt++)
        {
            var text = HttpPostOnce(url, jsonBody, token, timeoutSec, out why, out var retryable);
            if (text is not null) return text;
            if (!retryable || attempt >= 2) return null;
            Thread.Sleep(2000);
        }
    }

    private static string? HttpPostOnce(string url, string jsonBody, string token, int timeoutSec,
        out string? why, out bool retryable)
    {
        why = null;
        retryable = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(PhotoUserAgent);
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.TryAddWithoutValidation(SelfTokenEnv, token);
            using var resp = Task.Run(() => http.SendAsync(req, HttpCompletionOption.ResponseContentRead))
                .GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                why = "平台返回 HTTP " + code;
                retryable = code is 429 or 503;
                return null;
            }
            var text = Task.Run(() => resp.Content.ReadAsStringAsync()).GetAwaiter().GetResult();
            return text;
        }
        catch (Exception ex)
        {
            why = ex.GetType().Name + "：" + ex.Message;
            retryable = ex is TaskCanceledException or TimeoutException;
            return null;
        }
    }

    private static string PhotoEndpoint()
        => string.IsNullOrWhiteSpace(_photoApi) ? CommonsSearchApi : _photoApi!;

    /// <summary>
    /// 检索 + 下载。按 Commons 给出的相关性顺序逐个试：前面几张可能格式/尺寸不合意，
    /// 换一张比直接放弃好。
    /// </summary>
    private static Photo? SearchAndDownload(string query, out string? why)
    {
        why = null;
        var api = PhotoEndpoint();
        var url = api + (api.Contains('?', StringComparison.Ordinal) ? "&" : "?")
            + "action=query&format=json&formatversion=1&generator=search&gsrnamespace=6&gsrlimit=10"
            + "&gsrsearch=" + Uri.EscapeDataString(query + " filetype:bitmap")
            + "&prop=imageinfo&iiprop=url%7Cmime%7Csize%7Cextmetadata&iiurlwidth=" + PhotoWidth;

        var json = HttpGetText(url, PhotoSearchTimeoutSec, out var netWhy);
        if (json is null) { why = netWhy; return null; }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var q)
            || !q.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Object)
        { why = "Wikimedia 检索没有返回可用结果"; return null; }

        // generator=search 返回的是对象（不是按序数组），靠每条自带的 index 还原相关性顺序
        var candidates = new List<(int Index, JsonElement El)>();
        foreach (var p in pages.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            var idx = p.Value.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var iv) ? iv : int.MaxValue;
            candidates.Add((idx, p.Value));
        }
        candidates.Sort((a, b) => a.Index.CompareTo(b.Index));

        foreach (var (_, cand) in candidates)
        {
            if (!cand.TryGetProperty("imageinfo", out var info) || info.ValueKind != JsonValueKind.Array
                || info.GetArrayLength() == 0) continue;
            var ii = info[0];
            var mime = Str(ii, "mime") ?? "";
            if (mime is not ("image/jpeg" or "image/png")) continue;
            var w = Num(ii, "width"); var h = Num(ii, "height");
            // 太小的图放到整页会糊：实测 800px 宽的图铺一页就能看出虚，提到 1200 才有底。
            if (w < 1200 || h <= 0) continue;
            var ar = (double)w / h;
            // 极端长宽比在“定框裁切（cover）”后只剩中间一条：9000x3600 的全景铺到 16:9 页上
            // 等于把中间 60% 拉满，画面已经不可用了。两端都挡。
            if (ar is < 0.55 or > 2.2) continue;
            var title = Str(cand, "title") ?? "";                       // “File:Sunset.jpg”
            var fileName = title.StartsWith("File:", StringComparison.OrdinalIgnoreCase) ? title.Substring(5) : title;
            // 标题里已写明是 logo / 图标 / 旗帜徽章的，不是照片：实测“teamwork”这类抽象词
            // 会把 Teamwork-icon.jpg、Teamwork.com-Logo-200.png 排在前面。
            if (LooksLikeNonPhoto(fileName)) continue;
            var license = MetaValue(ii, "LicenseShortName");
            if (IsNonFree(license)) continue;
            // 书刊扫描件：Commons 的相关性排序常把 1920~30 年代的插图排在前面
            // （实测搜 “business people working together” 第一张就是 1920 年的书里插图）。
            if (IsArchivalScan(MetaValue(ii, "Categories"))) continue;
            // 历史档案照：实测 “meeting room” 抳到 1968 年白宫会议的新闻照（Public domain），
            // 放进商务稿里很出戏。分类里的早期年份是档素材的可靠信号。
            if (HasEarlyYear(MetaValue(ii, "Categories"))) continue;
            var src = Str(ii, "thumburl") ?? Str(ii, "url");
            if (string.IsNullOrWhiteSpace(src)) continue;

            var bytes = HttpGetBytes(src!, PhotoDownloadTimeoutSec, out var dlWhy);
            if (bytes is null) { why = dlWhy; continue; }
            if (!IsJpeg(bytes) && !IsPng(bytes)) { why = "图库返回的不是 JPEG/PNG"; continue; }

            var ext = mime == "image/png" ? ".png" : ".jpg";
            var file = Path.Combine(PhotoCacheDir(), CacheKey(query) + "_" + CacheKey(fileName) + ext);
            try { File.WriteAllBytes(file, bytes); }
            catch (Exception ex) { why = "写入缓存失败：" + ex.Message; continue; }

            return new Photo
            {
                Path = file,
                FileName = fileName.Length > 0 ? fileName : "（未命名图片）",
                Author = MetaValue(ii, "Artist"),
                License = license,
                PageUrl = Str(ii, "descriptionurl") ?? "",
                Query = query,
            };
        }
        why ??= "图库里没有尺寸/格式合适的照片";
        return null;
    }

    /// <summary>Commons 的数值字段（可能是数字，也可能是字符串）。</summary>
    private static int Num(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    /// <summary>标题就写明不是照片的（logo / 图标 / 旗帜徽章 / 地图）：这类在“定框裁切”下毫无意义。</summary>
    private static bool LooksLikeNonPhoto(string fileName)
    {
        foreach (var w in new[] { "logo", "icon", "wordmark", "coat of arms", "flag of", "signature" })
            if (fileName.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>书刊/档案扫描件：Commons 上这类内容量极大，且常被相关性排序排到前面。</summary>
    private static bool IsArchivalScan(string categories)
        => categories.Contains("Internet Archive Book Images", StringComparison.OrdinalIgnoreCase)
        || categories.Contains("Book scans", StringComparison.OrdinalIgnoreCase)
        || categories.Contains("Scanned books", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 分类里出现 20 世纪及更早的年份（≤1999）→ 基本是历史/档案素材。
    ///
    /// <para>
    /// 为何不用“标题里的年份”：相机直出文件名如 <c>IMG_1912.jpg</c> 会被误杀；
    /// 而分类是人工维护的（如“1968 in Washington, D.C.”），误杀面小得多。
    /// 当代照片同样会被归入年份分类，但那是 2000 年以后，不会命中。
    /// </para>
    /// </summary>
    private static bool HasEarlyYear(string categories)
    {
        if (categories.Length == 0) return false;
        var digits = 0;
        var value = 0;
        foreach (var ch in categories)
        {
            if (ch is >= '0' and <= '9')
            {
                digits++;
                value = value * 10 + (ch - '0');
                if (digits > 4) { digits = 0; value = 0; }   // 超过 4 位就不是年份
                continue;
            }
            if (digits == 4 && value <= 1999) return true;
            digits = 0;
            value = 0;
        }
        return digits == 4 && value <= 1999;
    }

    /// <summary>Commons 的 extmetadata：值藏在 { "value": "…" } 里，且作者字段常带 HTML。</summary>
    private static string MetaValue(JsonElement imageInfo, string key)
    {
        if (imageInfo.ValueKind != JsonValueKind.Object
            || !imageInfo.TryGetProperty("extmetadata", out var meta) || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Object) return "";
        return StripHtml(Str(v, "value") ?? "").Trim();
    }

    /// <summary>Commons 上不该出现非自由素材，但万一混进来一张，宁可不配图也不惹授权风险。</summary>
    private static bool IsNonFree(string license)
        => license.Contains("fair use", StringComparison.OrdinalIgnoreCase)
        || license.Contains("non-free", StringComparison.OrdinalIgnoreCase)
        || license.Contains("nonfree", StringComparison.OrdinalIgnoreCase);

    /// <summary>署名行里不能带 HTML 标签（Commons 的作者字段常是 &lt;a&gt;…&lt;/a&gt;）。</summary>
    private static string StripHtml(string html)
    {
        var sb = new StringBuilder(html.Length);
        var inTag = false;
        foreach (var ch in html)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }
        return DecodeEntities(sb.ToString()).Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    /// <summary>只解最常见的几个实体：引 System.Net.WebUtility 没必要，自己替换更稳。</summary>
    private static string DecodeEntities(string s)
        => s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
            .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'").Replace("&nbsp;", " ");

    /// <summary>缓存文件名：只留单词字符与 CJK，再加一段稳定短哈希防撞。</summary>
    private static string CacheKey(string s)
    {
        var sb = new StringBuilder(48);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) && sb.Length < 32) sb.Append(ch);
        }
        // FNV-1a：只要稳定、不用于安全，不引 System.Security.Cryptography（那个引用在受限宿主里会缺）
        ulong h = 14695981039346656037UL;
        foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
        return sb.Length == 0 ? h.ToString("x16") : sb + "_" + h.ToString("x8");
    }

    /// <summary>
    /// 带一次退避重试的 GET。重试只针对**限流与超时**：Commons 对密集请求会返回 429
    /// （探测时实测触发过），而匿名调用配额不大，一次 2 秒退避就够；仍失败则如实降级，不无休止重试。
    /// </summary>
    private static byte[]? HttpGetBytes(string url, int timeoutSec, out string? why)
    {
        for (var attempt = 1; ; attempt++)
        {
            var bytes = HttpGetOnce(url, timeoutSec, out why, out var retryable);
            if (bytes is not null) return bytes;
            if (!retryable || attempt >= 2) return null;
            Thread.Sleep(2000);
        }
    }

    private static byte[]? HttpGetOnce(string url, int timeoutSec, out string? why, out bool retryable)
    {
        why = null;
        retryable = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(PhotoUserAgent);
            // 同步等待要包到 Task.Run 里：宿主的调用线程可能带 SynchronizationContext，
            // 直接 GetAwaiter().GetResult() 有死锁风险（宿主里是同步反射调用 Run）。
            using var resp = Task.Run(() => http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                .GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                why = "请求失败 HTTP " + code;
                retryable = code is 429 or 503;
                return null;
            }
            if (resp.Content.Headers.ContentLength is { } len && len > PhotoMaxBytes)
            { why = "图片过大（" + (len / 1024 / 1024) + "MB）"; return null; }
            var bytes = Task.Run(() => resp.Content.ReadAsByteArrayAsync()).GetAwaiter().GetResult();
            if (bytes.Length == 0) { why = "图片内容为空"; return null; }
            if (bytes.LongLength > PhotoMaxBytes) { why = "图片过大"; return null; }
            return bytes;
        }
        catch (Exception ex)
        {
            // 连接类失败也要把**真实异常**写出来：只报“连不上”时无法区分
            // “端点写错了 / DNS 解析不了 / 需要代理”，而排障时正是要这个。
            var detail = ex.GetType().Name + "：" + ex.Message;
            if (ex.InnerException is { } inner) detail += " ＜ " + inner.GetType().Name + "：" + inner.Message;
            retryable = ex is TaskCanceledException or TimeoutException;
            why = IsNetworkError(ex)
                ? "无法连接图库（" + detail
                  + "；Wikimedia 在部分网络不可达，可给服务配 HTTPS_PROXY，或用 AGUI_PHOTO_API 指向镜像）"
                : detail;
            return null;
        }
    }

    private static string? HttpGetText(string url, int timeoutSec, out string? why)
    {
        var bytes = HttpGetBytes(url, timeoutSec, out why);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static bool IsNetworkError(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or OperationCanceledException
        || ex.InnerException is not null && IsNetworkError(ex.InnerException);

    private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    /// <summary>本页配图的检索关键词（没有则空串）。</summary>
    private static string ImageQueryOf(JsonElement el)
        => (Str(el, "imageQuery") ?? Str(el, "image_query"))?.Trim() ?? "";

    /// <summary>
    /// 把任意可解码的图片规范成 OOXML 认得的字节：平台只认 PNG/JPEG/GIF/BMP/TIFF，
    /// WebP（图库上传允许）与其它格式被错标类型时 PowerPoint 会报“图片不可读”。
    /// 认不出的走 ImageSharp 重编成 PNG（它支持 WebP 解码）。
    /// </summary>
    private static byte[] NormalizeImageBytes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (IsPng(bytes) || IsJpeg(bytes) || IsGif(bytes) || IsBmp(bytes) || IsTiff(bytes)) return bytes;
        using var img = Image.Load(bytes);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static bool IsGif(byte[] b) => b.Length > 3 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F';
    private static bool IsBmp(byte[] b) => b.Length > 2 && b[0] == (byte)'B' && b[1] == (byte)'M';
    private static bool IsTiff(byte[] b) => b.Length > 4
        && ((b[0] == 0x49 && b[1] == 0x49 && b[2] == 0x2A) || (b[0] == 0x4D && b[1] == 0x4D && b[2] == 0x00));

    /// <summary>
    /// 取本页要放的图的<b>本地路径</b>：显式 path 优先（文件存在才用）；否则用 imageQuery 检索。
    ///
    /// <para>
    /// <b>二次尝试</b>：模型写的关键词取不到图时，改用<b>本页自己的文字</b>再查一次图库。
    /// 为何需要：模型不知道图库里到底有什么 —— 实测图库描述常常就是<b>人名 / 产品名</b>
    /// （“刘佳俊”“黄敏谊”），而模型只会写“员工 颁奖 舞台”这种通用词，于是永远配不上。
    /// 而页面上往往就写着那个人名（“高效习惯优秀进步奖 · 刘佳俊”），与描述<b>词面</b>能对上
    /// （靠关键词召回那条路，实测用名字直接检索能到 0.88）。
    /// </para>
    ///
    /// <para>
    /// <b>怎么选</b>：先按模型的关键词查（图库 → 网络）；如果图库那张不够可信
    /// （低于 <see cref="LibraryConfidentScore"/>），再用本页文字查一次图库，<b>谁分高用谁</b>——
    /// 图库命中永远优先于网图（自有素材优先）。都没有才降级为题图。
    /// 二次尝试**只查图库不出网**（中文页面文字丢给 Wikimedia 不会有结果、还白耗预算）；
    /// 配上图时不报 warning（报“已降级为题图”就是假消息），两次都没配上则两条原因都报出来。
    /// </para>
    /// </summary>
    private static string? ImagePathOf(JsonElement el)
    {
        var path = Str(el, "path");
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
        var q = ImageQueryOf(el);
        if (q.Length == 0) return null;
        var first = ResolvePhoto(q, libraryOnly: false, label: "关键词", out var firstWarn);
        var best = first;
        // 二次尝试：见上方 summary。条件是“第一次没配上 / 只配到网图 / 图库那张不够可信”。
        var pageText = PageTextOf(el);
        string? altWarn = null;
        if (pageText.Length >= 2 && !string.Equals(pageText, q, StringComparison.Ordinal)
            && (best is null || best.Source != "library" || best.Score < LibraryConfidentScore))
        {
            var alt = ResolvePhoto(pageText, libraryOnly: true, label: "本页文字", out altWarn);
            if (BetterPhoto(best, alt)) best = alt;
            // 落选的候选从“用到的照片”里去掉，免得 images[] 里列着没进稿子的图
            if (best is not null && first is not null && !ReferenceEquals(best, first)
                && !string.Equals(best.Path, first.Path, StringComparison.OrdinalIgnoreCase))
                Photos.Remove(first);
        }

        if (best is null)
        {
            // 两次都没配上：两条原因都报出来（只报关键词那条，会让人以为根本没试过本页文字）
            if (firstWarn is not null) Warn(firstWarn);
            if (altWarn is not null) Warn(altWarn);
        }
        return best?.Path;
    }

    /// <summary>
    /// 图库命中的“可信分”。低于它时，会再拿<b>本页文字</b>查一次图库，谁分高用谁。
    ///
    /// <para>
    /// 为何门槛不贴着 <see cref="LibraryMinScore"/>（0.6）：那是“能不能用”的底线，这条是“够不够确定”。
    /// 实测图库自动生成的描述很长（含一堆“检索词：…”），通用词（“员工 颁奖 舞台”）也能靠“舞台/宴会厅”
    /// 蹭过 0.6，配出来的是不相干的合影 —— 实测就碰上过。真实命中在 0.84 以上（人名直查）。
    /// </para>
    /// </summary>
    private const double LibraryConfidentScore = 0.78;

    /// <summary>两张候选谁更该用：自有素材优先于网图，同源则分高者胜（<paramref name="alt"/> 为空表示不换）。</summary>
    private static bool BetterPhoto(Photo? cur, Photo? alt)
    {
        if (alt is null) return false;
        if (cur is null) return true;
        if (alt.Source == "library" && cur.Source != "library") return true;   // 自有素材优先
        if (alt.Source != "library" && cur.Source == "library") return false;
        return alt.Score > cur.Score;
    }

    /// <summary>
    /// 本页自己的文字（标题 / 副标题 / 正文文字，上限 <see cref="PageTextMaxChars"/>）。
    ///
    /// <para>
    /// 用于配图的二次尝试（见 <see cref="ImagePathOf"/>），也用于“人名是否能配上”这类场景。
    /// 截断到 120 字：BM25 会把得分按查询词项数摊薄，文字太长反而糊掉真正关键的那个名字。
    /// </para>
    /// </summary>
    private static string PageTextOf(JsonElement el)
    {
        var sb = new StringBuilder();
        CollectText(el, sb);
        var s = sb.ToString().Trim();
        return s.Length <= PageTextMaxChars ? s : s[..PageTextMaxChars];
    }

    private const int PageTextMaxChars = 120;

    /// <summary>递归收集页面文字；跳过结构化字段（type/layout/variant/path/imageQuery）免得把参数名当正文。</summary>
    private static void CollectText(JsonElement el, StringBuilder sb)
    {
        if (sb.Length >= PageTextMaxChars) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var v = el.GetString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(v!.Trim());
                }
                break;
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.NameEquals("imageQuery") || p.NameEquals("image_query") || p.NameEquals("path")
                        || p.NameEquals("variant") || p.NameEquals("layout") || p.NameEquals("type")
                        || p.NameEquals("imageSource") || p.NameEquals("imageScopeId")) continue;
                    CollectText(p.Value, sb);
                }
                break;
            case JsonValueKind.Array:
                foreach (var it in el.EnumerateArray()) CollectText(it, sb);
                break;
        }
    }

    /// <summary>本页是否真的拿到了图（渲染前用它决定版式，避免“检索失败还占掉半页”）。</summary>
    private static bool HasUsablePhoto(JsonElement el) => ImagePathOf(el) is not null;

    /// <summary>
    /// 配图预解析：把整份稿子里所有 imageQuery 先检索并下载到本地（同时填好署名清单）。
    ///
    /// <para>
    /// 为何要在渲染前做：① 拆页要按“这页到底有没有图”决定版式与可用宽度（带图的要点页只有半页宽）；
    /// ② 「图片来源」页必须在渲染前追加到页列表里；③ 检索失败要在同一处统一降级并报 warning。
    /// </para>
    /// </summary>
    private static void PreResolvePhotos(IEnumerable<string> pageJson)
    {
        foreach (var pj in pageJson)
        {
            try
            {
                using var d = JsonDocument.Parse(pj);
                // 先按“页面级”解析一次：版式决策（HasUsablePhoto）与渲染看到的是同一个结果，
                // 二次尝试（用本页文字再查图库）也在这条路上生效。
                ImagePathOf(d.RootElement);
                // 页内嵌套的图（如 gallery 的 images[]）：每条用自己的文字解析
                WalkImageQueries(d.RootElement);
            }
            catch { /* 解析不了就跳过：渲染阶段还会再试一次 */ }
        }
    }

    private static void WalkImageQueries(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String
                        && (p.NameEquals("imageQuery") || p.NameEquals("image_query")))
                    {
                        var q = (p.Value.GetString() ?? "").Trim();
                        if (q.Length > 0)
                        {
                            ResolvePhoto(q, libraryOnly: false, label: "关键词", out var wn);
                            if (wn is not null) Warn(wn);
                        }
                    }
                    else WalkImageQueries(p.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var it in el.EnumerateArray()) WalkImageQueries(it);
                break;
        }
    }

    /// <summary>
    /// 「图片来源」页的页面 JSON（多张时按能放下多少条自动拆页，走与要点页同一套分页）。
    ///
    /// <para>只列<b>网络照片</b>（Wikimedia 的 CC 素材）—— 图库里的图是团队自有素材，没有署名义务。</para>
    /// </summary>
    private static string CreditsPageJson()
    {
        var credits = Photos.Where(p => p.Source != "library").ToList();
        var sb = new StringBuilder("{\"type\":\"credits\",\"title\":")
            .Append(Js("图片来源 · Image credits（Wikimedia Commons）"))
            .Append(",\"items\":[");
        for (var i = 0; i < credits.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Js(credits[i].CreditLine()));
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>需要 CC 署名的照片数（决定是否追加「图片来源」页）。</summary>
    private static int NetworkPhotoCount => Photos.Count(p => p.Source != "library");

    /// <summary>来自团队图库的照片数（回显用）。</summary>
    private static int LibraryPhotoCount => Photos.Count(p => p.Source == "library");

    /// <summary>
    /// 「图片来源」页。CC BY / CC BY-SA 照片**必须署名**（标题 + 作者 + 许可 + 原页面），
    /// 所以只要用了检索来的照片就自动追加一页 —— 不放在模型可控的输入里，避免被“省略”掉。
    /// 条目可能很长（带 URL），所以这里**不用项目符号的“小标题：说明”拆分**，也不截断。
    /// </summary>
    private static string CreditsSlide(JsonElement el, SlideCtx ctx)
    {
        var t = ctx.Theme;
        var items = StringList(el, "items");
        var paras = new StringBuilder();
        foreach (var it in items)
            paras.Append(Para(it, CreditSize, t.Secondary, align: "l", bullet: "•",
                spaceBefore: 8, lineSpacing: 115, marL: (int)_m.Gap));
        if (items.Count == 0) paras.Append(Para("（本页应有图片来源，但清单为空）", CreditSize, t.Secondary));
        return SlideXml(t.Bg,
        [
            SlideTitle(Str(el, "title") ?? "图片来源", ctx),
            TextBox(ctx.NextId(), MX, BodyY, CW, BodyH, paras.ToString(), anchor: "t"),
            PageBadge(ctx),
        ]);
    }

    /// <summary>署名行的排版计划：与 <see cref="CreditsSlide"/> 同字号同间距（分页才判得准）。</summary>
    private static List<ParaPlan> CreditPlan(List<string> items)
        => items.Select(it => P(it, CreditSize, 8, 115, _m.Gap)).ToList();

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

    /// <summary>图片缺失时：不静默略过——记 warning，并改用<b>自动生成的题图</b>（比一块纯色好看，也不假称有图）。</summary>
    private static string ImageMissing(JsonElement el, SlideCtx ctx, string? path, long x, long y, long cx, long cy, bool radius)
    {
        var t = ctx.Theme;
        var query = ImageQueryOf(el);
        string msg;
        string seed;
        if (query.Length > 0)
        {
            // 检索失败的具体原因 ResolvePhoto 已经记过，这里只说结果，不把同一件事报两遍
            msg = "（未取到配图，已自动生成题图）";
            seed = query;
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            Warn("图片不存在，已改用自动生成的题图：" + path);
            msg = "（图片不存在，已自动生成题图）";
            seed = path!;
        }
        else
        {
            Warn("未提供图片 path，已改用自动生成的题图");
            msg = "（未提供 path，已自动生成题图）";
            seed = ctx.Title;
        }
        return HeroArt(ctx, x, y, cx, cy, seed)
             + TextBox(ctx.NextId(), x, y + cy - Sz(457200), cx, Sz(457200),
                Para(msg, 1100, t.Light, align: "ctr", alpha: 80), anchor: "b");
    }

    private static string ImageFull(JsonElement el, SlideCtx ctx)
    {
        var path = ImagePathOf(el);
        var caption = Str(el, "caption");
        var availH = BodyH - (string.IsNullOrWhiteSpace(caption) ? 0 : 457200);
        if (path is null)
            return ImageMissing(el, ctx, Str(el, "path"), MX, BodyY, CW, availH, radius: true);

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

        var rel = ctx.AddImage(NormalizeImageBytes(path!));
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
        var path = ImagePathOf(el);
        if (path is not null)
            sb.Append(Picture(ctx.NextId(), AddCoverImage(ctx, path, imgW, BodyH), imgX, BodyY, imgW, BodyH, radius: true));
        else
            sb.Append(ImageMissing(el, ctx, Str(el, "path"), imgX, BodyY, imgW, BodyH, radius: true));

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

        var plan = items.Select(b => P(b, 1600, 12, 125, _m.Gap)).ToList();
        var availH = BodyH - (string.IsNullOrWhiteSpace(heading) ? 0 : 762000);
        var scale = FitScale(plan, textW, availH);
        if (items.Count == 0) paras.Append(Para("（未提供文字内容）", 1500, t.Secondary));
        foreach (var b in items)
            paras.Append(Para(b, EffSize(1600, scale), t.Text, align: "l", bullet: "•",
                spaceBefore: EffSpaceBefore(12, scale), lineSpacing: EffSpacing(125, scale), marL: (int)_m.Gap));
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
                    // 图廊每张都可用 imageQuery 检索；取不到就保留原 path 以便报警文案说得清
                    shots.Add((ImagePathOf(it) ?? Str(it, "path") ?? "", Str(it, "caption") ?? ""));
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
                sb.Append(FitBox(ctx.NextId(), x, y + imgH, cellW, capH,
                    [T(cap, 1200, t.Secondary, align: "ctr")], "图廊图注", "ctr"));
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
        var path = ImagePathOf(el);
        if (path is not null)
            shapes.Add(Picture(ctx.NextId(), AddCoverImage(ctx, path, imgW, H), imgX, 0, imgW, H));
        else
            shapes.Add(ImageMissing(el, ctx, Str(el, "path"), imgX, 0, imgW, H, radius: false));

        var textW = imgX - MX * 2;
        var paras = new StringBuilder();
        var titleTx = Str(el, "title") ?? "";
        var items = StringList(el, "bullets");
        if (items.Count == 0) items = StringList(el, "items");
        var text = Str(el, "text");
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(text)) items.Add(text!);
        // 标题 + 要点一起量高：以前只缩要点、不管标题，标题一长就顶出叠字区
        var plan = new List<ParaPlan> { P(titleTx, 3400, 0, 108, 0, bold: true) };
        plan.AddRange(items.Select(b => P(b, 1600, 12, 125, _m.Gap)));
        var scale = FitScale(plan, textW, 4114800);
        paras.Append(ParaTitle(titleTx, EffSize(3400, scale), t.Bg, lineSpacing: EffSpacing(108, scale)));
        foreach (var b in items)
            paras.Append(Para(b, EffSize(1600, scale), t.OnPrimary, align: "l", bullet: "•",
                spaceBefore: EffSpaceBefore(12, scale), lineSpacing: EffSpacing(125, scale), marL: (int)_m.Gap));
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
            sb.Append(FitBox(ctx.NextId(), MX, y, labelW, rowH,
                [T(items[i].Label, 1600, t.Text, spacing: 120)], "进度标签 " + (i + 1), "ctr"));
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
            sb.Append(FitBox(ctx.NextId(), MX + cellW * i, y + d + Sz(68580), cellW, labelH,
                [T(FormatProgress(items[i].Value), 1800, t.Primary, bold: true, align: "ctr", spacing: 110),
                 T(items[i].Label, 1400, t.Secondary, align: "ctr", spaceBefore: 4, spacing: 115)],
                "环形标签 " + (i + 1), "t"));
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

    /// <summary>椭圆（cx==cy 即正圆）：用来做时间轴的序号节点 / 图标圆 / 题图里的圆。</summary>
    private static string Ellipse(int id, long x, long y, long cx, long cy, string fill, int alpha = 100)
    {
        var sb = new StringBuilder();
        sb.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id).Append("\" name=\"Ellipse ").Append(id).Append("\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>")
          .Append("<p:spPr><a:xfrm><a:off x=\"").Append(x).Append("\" y=\"").Append(y)
          .Append("\"/><a:ext cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm>")
          .Append("<a:prstGeom prst=\"ellipse\"><a:avLst/></a:prstGeom>")
          .Append("<a:solidFill>");
        if (alpha < 100) sb.Append("<a:srgbClr val=\"").Append(BareHex(fill)).Append("\"><a:alpha val=\"").Append(alpha * 1000).Append("\"/></a:srgbClr>");
        else sb.Append(Rgb(fill));
        sb.Append("</a:solidFill><a:ln><a:noFill/></a:ln></p:spPr>")
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
        using (var processed = img.Clone(x => x.Crop(crop).Resize(outW, outH)))
        {
            // 照片（JPEG）仍存回 JPEG：PNG 是无损的，一张 1600px 的照片压成 PNG 会胀到十几 MB，
            // 而这份 pptx 是要发给用户下载的。截图类（PNG）保持 PNG，避免文字边缘变脏。
            // 类型看文件头自己判：ImageSharp 2.x 在 Image.Metadata 上不给 DecodedImageFormat。
            if (LooksLikeJpeg(path))
                processed.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
            else
                processed.SaveAsPng(ms);
        }
        return ctx.AddImage(ms.ToArray());
    }

    /// <summary>只看文件头 3 个字节判 JPEG（FFD8FF）——不依赖图像库的元数据 API。</summary>
    private static bool LooksLikeJpeg(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            var n = fs.Read(head);
            return n >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
        }
        catch { return false; }
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

    /// <summary>
    /// 粗体字面：量宽用。
    ///
    /// <para>
    /// 粗体会把拉丁字母撑宽（汉字advance 宽度基本不变），量宽时不区分就会低估行数 ——
    /// 而本技能的正文里粗体用得很多（要点小标题、卡片标题、示意图层名全是粗体）。
    /// 族里没有粗体字面时 CreateFont 会退到最接近的一档，量出来至少不会偏小。
    /// </para>
    /// </summary>
    private static SixLabors.Fonts.Font BoldFont(float size)
    {
        lock (_fontLock)
        {
            if (_fontFamily is null) PickFamily();
            try { return _fontFamily!.Value.CreateFont(size, SixLabors.Fonts.FontStyle.Bold); }
            catch { return _fontFamily!.Value.CreateFont(size); }
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
