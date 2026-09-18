using System.Reflection;

namespace AguiGroupChat.Agents.BuiltinSkills;

/// <summary>
/// 内置「PDF 文档生成」技能（.pdf）——开箱即用，无需手工导入。
///
/// 设计参考 MiniMax-AI/skills 的 minimax-pdf：由<b>文档类型</b>决定配色 / 字体 / 留白
/// （设计令牌贯穿每一页），内容以<b>内容块（block）</b>而非纯文本组织
/// （标题 / 段落 / 列表 / callout / 引用 / 表格 / 图片 / 代码 / 分隔线 / 图注 / 分页 /
/// 留白 / 图表 / 目录），强调色由文档语义选出，也可显式覆盖。
///
/// 实现为<b>纯 .NET</b>：PDFsharp 6.2.4 + 自定义字体解析器（按族名分发 + glyf 字体子集嵌入）。
/// PDFsharp 已是平台依赖（随 TPA 分发，见 <c>AguiGroupChat.Hub</c> 的 PDFsharp 引用），
/// 所以技能正文<b>不需要 <c>#r "nuget: PdfSharp"</c></b>——避免一次多余联网还原。
/// 图表用 <c>XGraphics</c> 矢量绘制，不引入 ImageSharp / SkiaSharp（后者依赖原生库，
/// 平台的引用解析器只解析托管程序集）。
///
/// 路由：CREATE（blocks 从零生成）/ REFORMAT（markdown / text 重排）。
/// <b>不做 FILL（填表单域）</b>：PDFsharp 不支持 AcroForm 填写。
///
/// 正文以<b>嵌入资源</b>形式随程序集分发（BuiltinSkills/pdf_doc.skill.txt）。
/// 技能正文的维护源是 <c>tools/pdf-skills/pdf_doc.cs</c>，改完用
/// <c>node tools/pdf-skills/sync-builtin.mjs</c> 同步过来，不要直接手改本目录下的 .skill.txt。
///
/// 播种方式与 <see cref="BuiltinPptxSkills"/> 一致：仅在技能库中不存在该 id 时写入，
/// 不覆盖用户改过的版本；用户可在界面上删除。
/// </summary>
public static class BuiltinPdfSkills
{
    /// <summary>内置技能 id → 嵌入资源名后缀。</summary>
    public static readonly (string SkillId, string ResourceSuffix, string Name, string Description)[] Definitions =
    [
        (
            "pdf_doc",
            "pdf_doc.skill.txt",
            "PDF 文档生成",
            "生成排版精良的 PDF 文档（.pdf），也可把已有 Markdown / 纯文本重排成 PDF。" +
            "当用户要求「出个 PDF」「导出成 PDF」「做份正式报告 / 方案 / 白皮书 / 简历 / 一页纸」「打印级文档」" +
            "「把这份 Markdown 排版成 PDF」时调用。按文档类型（report 深色满版封面 / proposal 左右分割 / " +
            "resume 巨大首字母 / academic 浅底古典衬线 / minimal 白底细条 / editorial 幽灵字母 / " +
            "magazine 暖色居中堆叠 / terminal 近黑网格荧光绿）套用一套配色与留白令牌，A4 版心、页眉页脚与页码、" +
            "可选带页码目录（两趟渲染）。中文用系统/容器 CJK 字体按族名分发并子集嵌入（PDF 里是 CID 字体，换机器不丢字）。" +
            "参数为 JSON：title(可选，缺省取第一个 h1)、subtitle/author/date(可选)、" +
            "docType(report|proposal|resume|academic|minimal|editorial|magazine|terminal，可选，默认 report)、" +
            "accent(6 位十六进制，可选，覆盖强调色)、" +
            "accentRole(legal 深藏青|tech 钢蓝|eco 森林绿|academic 深青|finance 藏蓝|creative 砖红|health 青绿|luxury 棕金，可选)、" +
            "cover(true|false，可选，默认 true)、toc(true 或 {\"title\":\"目录\",\"items\":[…] }，可选)、" +
            "pageSize(A4|Letter，可选)、marginMm(可选)、" +
            "colors({ink,bg,panel,rule,muted,coverBg,coverInk}，可选)、" +
            "fontPath(可选，显式字体的 .ttf/.ttc 路径)、outputPath(可选，.pdf 落盘路径)、" +
            "blocks 数组（必填，至少一块）或 markdown / text 字符串（重排模式，二者只取其一，blocks 优先）。" +
            "参数缺省时不要传 outputPath，让文件落到默认可下载目录。" +
            "blocks 每项形如 " +
            "{\"type\":\"h1\",\"text\":\"一、概述\"} / {\"type\":\"h2\",\"text\":\"…\"} / {\"type\":\"h3\",\"text\":\"…\"} / " +
            "{\"type\":\"p\",\"text\":\"正文，支持 **粗体**、*斜体* 与 `等宽` 标记\"} / " +
            "{\"type\":\"list\",\"ordered\":false,\"items\":[\"要点一\",\"要点二\"]} / " +
            "{\"type\":\"callout\",\"kind\":\"info|warn|success|danger\",\"title\":\"重点\",\"text\":\"…\"} / " +
            "{\"type\":\"quote\",\"text\":\"…\",\"cite\":\"出处\"} / " +
            "{\"type\":\"table\",\"headers\":[\"维度\",\"说明\"],\"rows\":[[\"成本\",\"低\"]],\"caption\":\"表 1\"} / " +
            "{\"type\":\"image\",\"path\":\"/app/docs/a.png\",\"caption\":\"图 1\",\"widthMm\":120} 或 " +
            "{\"type\":\"image\",\"imageQuery\":\"现代化机房 服务器机柜\",\"caption\":\"图 1\",\"widthMm\":120}" +
            "（imageQuery = 从平台「图库」按语义检索自动配图，推荐；带 imageQuery 时不要再写 path；PDF 只嵌 PNG/JPEG） / " +
            "{\"type\":\"chart\",\"chartType\":\"bar|line|pie|doughnut\",\"title\":\"增长趋势\",\"categories\":[\"Q1\",\"Q2\"]," +
            "\"series\":[{\"name\":\"营收\",\"values\":[120,260]}],\"yLabel\":\"万元\",\"heightMm\":170} / " +
            "{\"type\":\"code\",\"language\":\"csharp\",\"code\":\"var x = 1;\"} / " +
            "{\"type\":\"divider\"} / {\"type\":\"caption\",\"text\":\"图注\"} / " +
            "{\"type\":\"pagebreak\"} / {\"type\":\"spacer\",\"heightMm\":12} / " +
            "{\"type\":\"toc\",\"title\":\"目录\",\"items\":[\"一、概述\"]}。" +
            "返回 JSON 含生成的 .pdf 文件路径、页数、块数、所用字体与 produce_file 下载标记（path/name/bytes），" +
            "请据实告知用户，不要编造正文内容。" +
            "注意：不支持填写已有 PDF 表单域（不做 AcroForm 填充），也不支持套用用户上传的 PDF 模板。"
        ),
    ];

    /// <summary>内置技能全部 id（供前端/诊断列示）。</summary>
    public static IEnumerable<string> SkillIds => Definitions.Select(d => d.SkillId);

    /// <summary>按发布配置决定是否播种（默认为 true；设 Agents:BuiltinPdfSkills=false 可关闭）。</summary>
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
    public const string Version = "2026-09-18.1";

    /// <summary>读取嵌入资源正文；换行统一为 \n（避免不同平台构建产物 CRLF 差异影响编译）。</summary>
    private static string ReadResource(string suffix)
    {
        var asm = typeof(BuiltinPdfSkills).Assembly;
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
