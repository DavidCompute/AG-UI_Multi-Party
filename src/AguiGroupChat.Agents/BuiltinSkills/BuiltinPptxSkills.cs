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
            "表格、指标卡（KPI）、大数字看板、网格卡片、时间轴/流程、图标行、引言页、配图页、" +
            "图表（柱状/折线/饼图/环形图）、小结与结束页。" +
            "当用户要求「做个 PPT」「出一套幻灯片 / 演示文稿」「把这份内容讲成一页页」「路演/汇报材料」时调用。" +
            "16:9 宽屏、统一主题配色与字体、除封面外每页带页码徽标；每页可附演讲者备注。" +
            "参数为 JSON：title(必填)、subtitle/author/date(可选)、" +
            "action(\"read\" + path：只读取既有 pptx 的文本，不生成文件)、" +
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
            "也可用历史主题 business|tech|warm|minimal|dark|vivid；" +
            "themeColors({primary,secondary,accent,light,bg,text}，可选，覆盖预设)、" +
            "fontTitle/fontBody(可选)、outputPath(可选，.pptx 落盘路径)、" +
            "slides 数组（必填，至少一页）。slides 每项形如 " +
            "{\"type\":\"cover\",\"title\":\"…\",\"subtitle\":\"…\"} / " +
            "{\"type\":\"toc\",\"title\":\"目录\",\"items\":[\"一、…\"]} / " +
            "{\"type\":\"section\",\"title\":\"一、…\",\"subtitle\":\"…\"} / " +
            "{\"type\":\"content\",\"title\":\"…\",\"bullets\":[\"要点\"]} / " +
            "{\"type\":\"twoCol\",\"title\":\"…\",\"left\":{\"heading\":\"…\",\"bullets\":[…]},\"right\":{…}} / " +
            "{\"type\":\"table\",\"title\":\"…\",\"headers\":[…],\"rows\":[[…]]} / " +
            "{\"type\":\"kpi\",\"title\":\"…\",\"items\":[{\"value\":\"98%\",\"label\":\"可用性\"}]} / " +
            "{\"type\":\"stats\",\"title\":\"…\",\"items\":[{\"value\":\"3×\",\"label\":\"效率提升\"}],\"cols\":3} / " +
            "{\"type\":\"grid\",\"title\":\"…\",\"items\":[{\"title\":\"…\",\"text\":\"…\"}],\"cols\":2} / " +
            "{\"type\":\"timeline\",\"title\":\"…\",\"items\":[{\"title\":\"需求\",\"detail\":\"…\"}]} / " +
            "{\"type\":\"iconRows\",\"title\":\"…\",\"items\":[{\"icon\":\"1\",\"title\":\"…\",\"text\":\"…\"}]} / " +
            "{\"type\":\"quote\",\"text\":\"…\",\"cite\":\"…\"} / " +
            "{\"type\":\"chart\",\"title\":\"…\",\"chartType\":\"bar|line|pie|doughnut\",\"categories\":[…]," +
            "\"series\":[{\"name\":\"…\",\"values\":[…]}],\"yLabel\":\"…\"} / " +
            "{\"type\":\"image\",\"title\":\"…\",\"path\":\"…\",\"caption\":\"…\"} / " +
            "{\"type\":\"summary\",\"title\":\"小结\",\"bullets\":[…] } / " +
            "{\"type\":\"end\",\"title\":\"谢谢\",\"subtitle\":\"…\"}。" +
            "content 页也可用 \"layout\":\"timeline|grid|stats|iconRows\" 指定子类型。" +
            "图表默认为图片（不可在 PowerPoint 里改数据）；若用户需要“能编辑数据”的图表，" +
            "把 chartType 写成 \"bar-native\" / \"line-native\" / \"pie-native\"（或顶层 chartData:\"native\"），" +
            "会生成原生可编辑图表（环形图暂不支持原生，会自动降级为图片并在返回里说明）。" +
            "用户上传的文件以其附件 ID（att_xxx）传入 path/template 即可，平台会解析成真实路径。" +
            "注意：设计规范建议**不要每页都用同一种版式**，请在大纲阶段就为每页选定合适的页型并轮换。" +
            "任何一页都可加 \"notes\"（写入演讲者备注）。" +
            "返回 JSON 含生成的 .pptx 文件路径与 slides 页数，请据实告知用户，不要编造正文内容。"
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
    public const string Version = "2026-09-15.1";

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
