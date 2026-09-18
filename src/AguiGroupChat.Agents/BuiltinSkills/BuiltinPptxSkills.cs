using System.Reflection;

namespace AguiGroupChat.Agents.BuiltinSkills;

/// <summary>
/// 内置「演示文稿生成」技能（PowerPoint / .pptx）——开箱即用，无需手工导入。
///
/// 设计参考 MiniMax-AI/skills 的 pptx-generator：把幻灯片按<b>页面类型</b>组织
/// （封面 / 目录 / 章节分隔 / 内容 / 两栏 / 表格 / 指标卡 / 引言 / 图片 / 图表 / 小结 / 结束页），
/// 并用一个<b>主题对象</b>（主色 / 辅色 / 强调色 / 浅色 / 底色 + 标题与正文字体）统一视觉。
/// 但实现为<b>纯 .NET</b>（DocumentFormat.OpenXml 的 PresentationML），与平台其它技能同一条
/// 执行链路，不依赖 Node / Python / PptxGenJS。
///
/// 图表与内置 docx 技能同口径：<b>不发 ChartPart</b>，用 ImageSharp 渲成 PNG 再按图片嵌入
/// （避开 DrawingML 图表 schema 风险，兼容性更好）。
///
/// 正文以<b>嵌入资源</b>形式随程序集分发（BuiltinSkills/pptx_deck.skill.txt）。
/// 技能正文的维护源是 <c>tools/pptx-skills/pptx_deck.cs</c>，改完用
/// <c>node tools/pptx-skills/sync-builtin.mjs</c> 同步过来，不要直接手改本目录下的 .skill.txt。
///
/// 播种方式与 <see cref="BuiltinDocxSkills"/> 一致：仅在技能库中不存在该 id 时写入，
/// 不覆盖用户改过的版本；用户可在界面上删除。
/// </summary>
public static class BuiltinPptxSkills
{
    /// <summary>内置技能 id → 嵌入资源名后缀。</summary>
    public static readonly (string SkillId, string ResourceSuffix, string Name, string Description)[] Definitions =
    [
        (
            "pptx_deck",
            "pptx_deck.skill.txt",
            "演示文稿生成（PPT）",
            "生成完整的 PowerPoint 演示文稿（.pptx）：封面、目录、章节分隔、内容页、两栏对比、" +
            "表格、指标卡（KPI）、大数字看板、进度条与环形仪表、网格卡片、时间轴/流程、图标行、引言页、" +
            "**示意图（金字塔 / 漏斗 / 四象限 / 循环闭环 / 层叠架构）**、配图页（含图文混排）、" +
            "**联网真照片（imageQuery，见下）**、" +
            "图表（柱状/折线/饼图/环形图/散点图/雷达图）、自动生成题图、小结与结束页。" +
            "当用户要求「做个 PPT」「出一套幻灯片 / 演示文稿」「把这份内容讲成一页页」「路演/汇报材料」「改改这份 PPT」时调用。" +
            "16:9 宽屏、统一主题配色与字体、除封面外每页带页码徽标；每页可附演讲者备注。" +
            "【插图】不需要用户提供任何图片素材：示意图页型用形状把关系画出来（分层/收敛/取舍/闭环/架构），" +
            "需要“配图”时若没有真图（path 缺失或文件不存在），会自动按主题配色生成一张抽象题图（零素材、零联网、无版权问题），" +
            "并在返回 warnings 里如实说明。**当用户说“要有插图/别只有文字/要好看一点”时，就用这些页型而不是堆文字。**" +
            "【真照片：imageQuery】用户说要“照片/实景图/每页配图”，或希望画面更像真实场景时，" +
            "在需要图的位置写 \"imageQuery\": \"关键词\"，工具会按顺序找图：① 先用平台的**团队图库**" +
            "（用户自己上传的图片，语义检索，命中就不需要署名）② 图库里没有再到 Wikimedia Commons（免密钥、自由许可）检索并嵌入照片；" +
            "cover(variant image/split)、section(variant full)、image（含 gallery 每张）、content 都支持；" +
            "**content 页写了 imageQuery 会自动变成“文左图右”**；给了 path 则优先用本地文件。" +
            "**关键词写法直接决定成图好坏（实测结论）**：用 **2~4 个能看得见的具体名词**，" +
            "如 \"modern office meeting room\"、\"glass office building\"、\"handshake business\"、\"city skyline sunset\"；" +
            "**不要用抽象词或动词**（teamwork / collaboration / together / growth / success）——" +
            "实测这些词会搜到 Logo、图标、甚至 1920 年代书里的插图；词太多（5 个以上）会搜不到任何结果。" +
            "要“现代商务感”就把 modern / interior / exterior 这类词带上；图廊（gallery）里每一张请给**不同**关键词。" +
            "用了检索照片时会**自动在稿末追加一页「图片来源」**（CC 许可要求署名，必须保留，不要删）；" +
            "命中团队图库的图不需要署名。" +
            "取不到图时会降级为自动题图并在 warnings 说明，不会失败。" +
            "注意：若部署网络访问不了 Wikimedia（如未配代理的内网），此功能会自动降级；此时不要反复重试。" +
            "【版式多样性】设计规范要求不要每页同一种版式，请主动轮换：示意/图/表/卡/时间轴交替，" +
            "同一份稿子里连续三页都是 content 会显得很平。" +
            "参数为 JSON：title(必填)、subtitle/author/date(可选)、" +
            "action(\"read\"+path：读取既有 pptx 的文本；\"qa\"+path：只自检不生成；" +
            "\"edit\"+path+ops：改动既有 pptx 的结构：删页/复制页/重排/替换文字/追加新页)、" +
            "template(既有 .pptx 路径：沿用该模板的母版/版式/配色出稿，不动原件)、" +
            "keepTemplateSlides(true 则保留模板原有页，默认清空只借其皮)、" +
            "style(sharp|soft|rounded|pill，可选，默认 soft；只影响页边距/间距/圆角，与 theme 正交)、" +
            "theme 可选以下 18 套命名调色板（按场景挑，比历史主题更好看）：" +
            "modern-wellness(医疗/健康/瑜伽)、business-authority(年报/金融/政企)、" +
            "nature-outdoors(户外/环保/农业)、vintage-academic(学术/历史/博物馆)、" +
            "soft-creative(母婴/甜品/幼教)、bohemian(婚礼/家居/有机)、" +
            "vibrant-tech(体育/健身房/创业路演)、craft-artisan(咖啡/手作/烘焙)、" +
            "tech-night(科技发布/天文/夜间经济，深色底)、education-charts(统计报告/教育/市场分析)、" +
            "forest-eco(景观/ESG/双碳)、elegant-fashion(时装/画廊/美妆)、" +
            "art-food(美食/展览/复古)、luxury-mysterious(珠宝/酒店/高端咨询/心理)、" +
            "pure-tech-blue(云/AI/水务/洁净能源)、coastal-coral(旅行/夏日活动/饮品)、" +
            "vibrant-orange-mint(儿童活动/快消/社交媒体)、platinum-white-gold(金融科技/品牌官网)；" +
            "不写 theme 时默认 business-authority；也可用历史主题 business|tech|warm|minimal|dark|vivid（老面孔，样式弱一点）；" +
            "themeColors({primary,secondary,accent,light,bg,text}，可选，覆盖预设)、" +
            "fontPair(命名字体配对：yahei(默认)|georgia-calibri|cambria-calibri|calibri-light|" +
            "trebuchet-calibri|arial-black-arial|impact-arial|palatino-garamond|consolas-calibri；" +
            "只换拉丁字面，中文仍走 fontCjk，避免汉字掉到 fallback)、" +
            "fontTitle/fontBody/fontCjk(可选，逐项覆盖)、" +
            "titleRule(true 才画“标题下强调线”；默认不画——那是 AI 生成稿的典型特征)、" +
            "outputPath(可选，.pptx 落盘路径)、" +
            "imageSearchApi(可选，覆盖照片检索端点；也可用环境变量 AGUI_PHOTO_API 指向镜像/代理)、" +
            "slides 数组（必填，至少一页）。slides 每项形如 " +
            "{\"type\":\"cover\",\"title\":\"…\",\"variant\":\"left|center|image|split\"}" +
            "（image/split 配 \"path\" 或 \"imageQuery\" 放图；无图时自动生成题图） / " +
            "{\"type\":\"toc\",\"title\":\"目录\",\"items\":[\"一、…\"],\"variant\":\"list|grid|sidebar\"} / " +
            "{\"type\":\"section\",\"title\":\"一、…\",\"subtitle\":\"…\",\"variant\":\"number|bar|full\"} / " +
            "{\"type\":\"content\",\"title\":\"…\",\"bullets\":[\"要点\"]} / " +
            "{\"type\":\"twoCol\",\"title\":\"…\",\"left\":{\"heading\":\"…\",\"bullets\":[…]},\"right\":{…}} / " +
            "{\"type\":\"table\",\"title\":\"…\",\"headers\":[…],\"rows\":[[…]]} / " +
            "{\"type\":\"kpi\",\"title\":\"…\",\"items\":[{\"value\":\"98%\",\"label\":\"可用性\"}]} / " +
            "{\"type\":\"stats\",\"title\":\"…\",\"items\":[{\"value\":\"3×\",\"label\":\"效率提升\"}],\"cols\":3} / " +
            "{\"type\":\"progress\",\"title\":\"…\",\"items\":[{\"label\":\"开发\",\"value\":72}]," +
            "\"max\":100,\"variant\":\"bar|ring\"}(进度/完成度/占比用这个) / " +
            "{\"type\":\"pyramid\",\"title\":\"…\",\"items\":[{\"title\":\"顶层\",\"text\":\"说明\"},…]}" +
            "（3~6 层，顶层最窄；适合分层策略/成熟度模型/价值层级） / " +
            "{\"type\":\"funnel\",\"title\":\"…\",\"items\":[{\"title\":\"触达\",\"text\":\"10000\"},…]}" +
            "（3~6 层，顶层最宽；适合转化率/逐步筛选，text 放数值） / " +
            "{\"type\":\"matrix\",\"title\":\"…\",\"xTitle\":\"难度\",\"yTitle\":\"价值\"," +
            "\"xLeft\":\"低\",\"xRight\":\"高\",\"items\":[左上,右上,左下,右下]}" +
            "（四象限；适合优先级/取舍/分类） / " +
            "{\"type\":\"cycle\",\"title\":\"…\",\"center\":\"持续改进\"," +
            "\"items\":[{\"title\":\"计划\",\"text\":\"定目标\"},…]}" +
            "（3~6 步环形闭环；适合迭代/PDCA） / " +
            "{\"type\":\"stack\",\"title\":\"…\",\"items\":[{\"title\":\"交互层\",\"text\":\"…\"},…]}" +
            "（纵向分层条；适合技术架构/能力分层） / " +
            "{\"type\":\"grid\",\"title\":\"…\",\"items\":[{\"title\":\"…\",\"text\":\"…\"}],\"cols\":2} / " +
            "{\"type\":\"timeline\",\"title\":\"…\",\"items\":[{\"title\":\"需求\",\"detail\":\"…\"}]} / " +
            "{\"type\":\"iconRows\",\"title\":\"…\",\"items\":[{\"icon\":\"rocket\",\"title\":\"…\",\"text\":\"…\"}]} / " +
            "{\"type\":\"quote\",\"text\":\"…\",\"cite\":\"…\"} / " +
            "{\"type\":\"chart\",\"title\":\"…\",\"chartType\":\"bar|line|pie|doughnut|scatter|radar\"," +
            "\"categories\":[…],\"series\":[{\"name\":\"…\",\"values\":[…]}],\"yLabel\":\"…\"}" +
            "（散点图用 series[].points:[[x,y],…]；雷达图用 categories 当各维度轴） / " +
            "{\"type\":\"image\",\"title\":\"…\",\"path\":\"…\",\"imageQuery\":\"…\",\"caption\":\"…\"," +
            "\"variant\":\"full|left|right|bleed|gallery\"}(left/right 是图文混排；bleed 半出血+叠字；" +
            "gallery 用 images:[{path|imageQuery,caption}] 放 2~4 张；left/right/bleed 可配 bullets 写文字侧；" +
            "path 与 imageQuery 二选一，path 优先) / " +
            "{\"type\":\"hero\",\"title\":\"…\",\"subtitle\":\"…\"}" +
            "（整页自动生成的抽象题图，适合章节引导页/无素材时的视觉休息页） / " +
            "{\"type\":\"summary\",\"title\":\"小结\",\"bullets\":[…],\"variant\":\"list|cta|split\"}" +
            "（cta 用 items 写行动项、contact 写联系方式；split 用 bullets+actions+contact） / " +
            "{\"type\":\"end\",\"title\":\"谢谢\",\"subtitle\":\"…\"}。" +
            "content 页也可用 \"layout\":\"timeline|grid|stats|iconRows|progress|pyramid|funnel|matrix|cycle|stack\" 指定子类型。" +
            "iconRows 的 icon 可直接用内置图标名（会画成真正的图标，比填字好看）：" +
            "check|cross|arrow|star|dot|warn|lock|user|chart|clock|gear|bulb|" +
            "money|target|rocket|shield|layers|globe|network|cloud|database|mail|phone|calendar|" +
            "flag|search|edit|file|pie|link|eye|heart|key|crown|map|cpu|package|award|briefcase|" +
            "users|code|gauge|filter|refresh|download；" +
            "填其它内容则当作 1~2 个字的短标记（或省略→用序号）。" +
            "图表默认为图片（不可在 PowerPoint 里改数据）；若用户需要“能编辑数据”的图表，" +
            "把 chartType 写成 \"bar-native\" / \"line-native\" / \"pie-native\"（或顶层 chartData:\"native\"），" +
            "会生成原生可编辑图表（环形/散点/雷达暂不支持原生，会自动降级为图片并在返回里说明）。" +
            "用户上传的文件以其附件 ID（att_xxx）传入 path/template 即可，平台会解析成真实路径。" +
            "任何一页都可加 \"notes\"（写入演讲者备注）。" +
            "【文字多也不会溢出】每页的标题与正文都按真实字形量高后自动缩字号；" +
            "要点页（content / summary 的 list 与 split / toc）实在装不下会自动分页" +
            "（标题带“（n/m）”，不丢任何一条），表格过长同理；" +
            "卡片、示意图层、封面这类结构固定的框缩到下限后会把放不下的部分截断并在 warnings 里说清楚。" +
            "所以你不必为“怕溢出”而把要点拆得很碎——正常分页即可；" +
            "但 warnings 里若报了“已按版面截掉”，说明那一处内容确实太多，请精简后重新生成。" +
            "返回 JSON 含生成的 .pptx 文件路径与 slides 页数，另带 qa（出稿后自检：" +
            "占位符/空页/只有标题/越界/文字放不进自己的框）" +
            "与 warnings（如图片缺失改用自动题图、文字过多已截断）；" +
            "**若 qa 报了问题，请先修内容再重新生成，不要直接把有问题的稿子交给用户**。" +
            "请据实告知用户产物路径，不要编造正文内容。"
        ),
    ];

    /// <summary>内置技能全部 id（供前端/诊断列示）。</summary>
    public static IEnumerable<string> SkillIds => Definitions.Select(d => d.SkillId);

    /// <summary>按发布配置决定是否播种（默认为 true；设 Agents:BuiltinPptxSkills=false 可关闭）。</summary>
    public static bool IsEnabled(bool? configured) => configured ?? true;

    /// <summary>构造一条内置技能定义；找不到嵌入资源时抛异常（属打包错误，应尽早暴露）。</summary>
    public static AgentSkillDefinition Build(string skillId, string resourceSuffix, string name, string description)
        => new()
        {
            SkillId = skillId,
            Name = name,
            Description = description,
            Kind = AgentSkillKind.Dotnet,
            ExecutionLocation = AgentSkillExecutionLocation.Server,
            // 与 SkillApi.BuildDef 对 dotnet 技能的策略一致：动态代码面最高 → 一律强制人工审批
            RequiresApproval = true,
            OwnerId = null, // 系统内置（非某用户创建）
            BuiltinVersion = Version, // 标记为“未改过的内置版” → 升级时可用新正文刷新
            Body = ReadResource(resourceSuffix),
        };

    /// <summary>
    /// 内置技能版本标识。<b>每次改动内置技能正文都应递增此值</b>，
    /// 以便已部署实例在升级时用新正文刷新旧的持久化快照。
    /// </summary>
    public const string Version = "2026-09-18.5";

    /// <summary>读取嵌入资源正文；换行统一为 \n（避免不同平台构建产物 CRLF 差异影响编译）。</summary>
    private static string ReadResource(string suffix)
    {
        var asm = typeof(BuiltinPptxSkills).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
        if (name is null)
            throw new InvalidOperationException(
                $"内置技能资源缺失：{suffix}。请确认 AguiGroupChat.Agents.csproj 的 EmbeddedResource 包含 BuiltinSkills/*.skill.txt。");
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"内置技能资源无法读取：{name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
