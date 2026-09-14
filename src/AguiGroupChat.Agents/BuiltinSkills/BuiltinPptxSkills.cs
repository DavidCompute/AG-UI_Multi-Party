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
            "表格、指标卡（KPI）、引言页、配图页、图表（柱状/折线/饼图/环形图）、小结与结束页。" +
            "当用户要求「做个 PPT」「出一套幻灯片 / 演示文稿」「把这份内容讲成一页页」「路演/汇报材料」时调用。" +
            "16:9 宽屏、统一主题配色与字体、除封面外每页带页码徽标；每页可附演讲者备注。" +
            "参数为 JSON：title(必填)、subtitle/author/date(可选)、" +
            "theme(business|tech|warm|minimal|dark|vivid，可选)、" +
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
            "{\"type\":\"quote\",\"text\":\"…\",\"cite\":\"…\"} / " +
            "{\"type\":\"chart\",\"title\":\"…\",\"chartType\":\"bar|line|pie|doughnut\",\"categories\":[…]," +
            "\"series\":[{\"name\":\"…\",\"values\":[…]}],\"yLabel\":\"…\"} / " +
            "{\"type\":\"image\",\"title\":\"…\",\"path\":\"…\",\"caption\":\"…\"} / " +
            "{\"type\":\"summary\",\"title\":\"小结\",\"bullets\":[…] } / " +
            "{\"type\":\"end\",\"title\":\"谢谢\",\"subtitle\":\"…\"}。" +
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
    public const string Version = "2026-09-14.1";

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
