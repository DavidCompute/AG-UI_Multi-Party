using System.Reflection;

namespace AguiGroupChat.Agents.BuiltinSkills;

/// <summary>
/// 内置「文档生成」技能（Word / .docx）——开箱即用，无需手工导入。
///
/// 三个场景各一个技能（公文 / 通知公告 / 工作报告），正文以<b>嵌入资源</b>形式随程序集分发
/// （见 BuiltinSkills/*.cs 与 .csproj 的 EmbeddedResource）。正文由
/// <c>tools/docx-skills/generate.mjs</c> 生成 —— 改排版规则请改生成器后重新生成并同步过来，
/// 不要直接手改本目录下的 .cs（它们只是技能的“正文载荷”，不参与平台自身编译）。
///
/// 播种方式与 <see cref="OrgBuiltinSeeds"/> 一致：仅在技能库中不存在该 id 时写入，
/// 因此不会覆盖用户改过的版本；同时它们不是 appsettings 种子，用户可在界面上删除。
/// </summary>
public static class BuiltinDocxSkills
{
    /// <summary>内置技能 id → 嵌入资源名后缀。</summary>
    public static readonly (string SkillId, string ResourceSuffix, string Name, string Description)[] Definitions =
    [
        (
            "docx_gongwen",
            "docx_gongwen.skill.txt",
            "公文生成（Word）",
            "生成规范排版的党政机关公文 Word 文档（通知 / 通报 / 请示 / 批复 / 报告）。" +
            "当用户要求「拟一份通知」「起草公文」「写个请示/批复」，或明确要求产出 .docx 公文时调用。" +
            "三号仿宋正文、黑体层次标题、22pt 小标宋大标题、固定行距 28pt、A4 公文页边距、页脚页码。" +
            "参数为 JSON：title(标题)、subtitle/author/date(可选)、outputPath(可选，.docx 落盘路径)、" +
            "sections 数组，每项可以是 heading(小节标题,level 1-3) / paragraph(段落) / numbered(编号列表) / " +
            "bullets(项目符号) / table({headers,rows}) / image({path,widthCm,caption,alt}) / " +
            "chart({type:bar|line|pie,categories,series|values,title,caption}) / toc(目录) / pageBreak。" +
            "返回 JSON 含生成的 docx 文件路径，请据实告知用户文件位置，不要编造正文内容。"
        ),
        (
            "docx_notice",
            "docx_notice.skill.txt",
            "通知公告生成（Word）",
            "生成简洁的通知 / 公告 / 事项说明 / 操作指引类 Word 文档，版式清爽、优先单页呈现。" +
            "当用户要求「写个通知」「出个公告」「说明一下并给我文档」，且不需要公文体例时调用。" +
            "微软雅黑标题与正文、不缩进、行距紧凑、无页码。" +
            "参数为 JSON：title、subtitle/author/date(可选)、outputPath(可选)、sections 数组，" +
            "每项可为 heading / paragraph / bullets / numbered / quote(引用强调) / table / image / chart / toc / pageBreak。" +
            "返回 JSON 含生成的 docx 文件路径。"
        ),
        (
            "docx_report",
            "docx_report.skill.txt",
            "工作报告生成（Word）",
            "生成工作报告 / 工作总结 / 调研报告 / 实施方案类 Word 文档，支持分章节与数据图表。" +
            "当用户要求「写份工作总结」「出个报告/方案」「把数据整理成报告」，或内容需要分章节、含表格或图表时调用。" +
            "黑体标题 + 宋体正文、首行缩进 2 字符、行距 20pt、页脚页码。" +
            "参数为 JSON：title、subtitle/author/date(可选)、outputPath(可选)、sections 数组，" +
            "每项可为 heading(level 1-3) / paragraph / bullets / numbered / quote / table({headers,rows}) / " +
            "image({path,widthCm,caption}) / chart({type:bar|line|pie,title,categories,series,values,caption}) / toc(目录) / pageBreak。" +
            "返回 JSON 含生成的 docx 文件路径。"
        ),
    ];

    /// <summary>内置技能全部 id（供前端/诊断列示）。</summary>
    public static IEnumerable<string> SkillIds => Definitions.Select(d => d.SkillId);

    /// <summary>按发布配置决定是否播种（默认为 true；设 Agents:BuiltinDocxSkills=false 可关闭）。</summary>
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
    /// 内置技能版本标识。<b>每次改动内置技能正文（改 generate.mjs 后重新生成）都应递增此值</b>，
    /// 以便已部署实例在升级时用新正文刷新旧的持久化快照。
    /// </summary>
    public const string Version = "2026-09-10.1";

    /// <summary>读取嵌入资源正文；换行统一为 \n（避免不同平台构建产物 CRLF 差异影响编译）。</summary>
    private static string ReadResource(string suffix)
    {
        var asm = typeof(BuiltinDocxSkills).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
        if (name is null)
            throw new InvalidOperationException(
                $"内置技能资源缺失：{suffix}。请确认 AguiGroupChat.Agents.csproj 的 EmbeddedResource 包含 BuiltinSkills/*.cs。");
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"内置技能资源无法读取：{name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
