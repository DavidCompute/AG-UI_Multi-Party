using System.Reflection;

namespace AguiGroupChat.Agents.BuiltinSkills;

/// <summary>
/// 内置「Excel 表格生成」技能（Excel / .xlsx）——开箱即用，无需手工导入。
///
/// 设计参考 MiniMax-AI/skills 的 minimax-xlsx 的「公式优先（Formula-First） + 财务配色标准 +
/// 小数位统一 + 聚合直算 + CREATE / ANALYZE 分流」思想，但实现为<b>纯 .NET</b>
/// （DocumentFormat.OpenXml 的 SpreadsheetML），与平台其它技能同一条 Roslyn 执行链路，
/// 不依赖 Node / Python / openpyxl。
///
/// 三条硬规则：
/// <list type="bullet">
///   <item><b>公式优先</b>：凡是“算出来的”单元格一律写成 Excel 公式（<c>&lt;f&gt;</c>），不写死数值；
///     合计 / 平均等由技能自动生成 <c>SUM</c> / <c>AVERAGE</c> / <c>COUNTA</c>。</item>
///   <item><b>财务配色</b>：硬编码输入 = 蓝（0000FF）、公式 / 计算结果 = 黑（000000）、跨表引用公式 = 绿（00B050）。</item>
///   <item><b>小数位统一</b>：顶层 <c>decimals</c> 一经指定，所有数值单元格统一按该小数位呈现。</item>
/// </list>
///
/// 刻意<b>不做图表</b>（不发 ChartPart，也不引入 ImageSharp）：与内置 pptx / docx 技能同口径，
/// 避开 DrawingML 图表 schema 风险；需要图表请用 <c>pptx_deck</c> / <c>docx_report</c>。
///
/// 正文以<b>嵌入资源</b>形式随程序集分发（BuiltinSkills/xlsx_book.skill.txt）。
/// 技能正文的维护源是 <c>tools/xlsx-skills/xlsx_book.cs</c>，改完用
/// <c>node tools/xlsx-skills/sync-builtin.mjs</c> 同步过来，不要直接手改本目录下的 .skill.txt。
///
/// 播种方式与 <see cref="BuiltinPptxSkills"/> 一致：仅在技能库中不存在该 id 时写入，
/// 不覆盖用户改过的版本；用户可在界面上删除。
/// </summary>
public static class BuiltinXlsxSkills
{
    /// <summary>内置技能 id → 嵌入资源名后缀。</summary>
    public static readonly (string SkillId, string ResourceSuffix, string Name, string Description)[] Definitions =
    [
        (
            "xlsx_book",
            "xlsx_book.skill.txt",
            "表格生成（Excel）",
            "生成 Excel 工作簿（.xlsx）：多工作表、表头样式、列宽、冻结首行、自动筛选、" +
            "千分位/百分比/货币数字格式、合计行（会计式上框线）、跨表引用公式。" +
            "遵循「公式优先」：凡是算出来的单元格写成 Excel 公式而非硬编码数值。" +
            "当用户要求「做个 Excel」「出一张表格 / 报表」「把数据整理成表格」「做个损益表 / 预算表 / 财务模型」，" +
            "或要把数据导出成 .xlsx 时调用。也支持读取/分析既有 xlsx 的结构摘要（action=analyze）。" +
            "参数为 JSON：action(create|analyze，可选，默认 create)、" +
            "title(create 必填，工作簿名与默认文件名，可用中文)、outputPath(可选)、" +
            "decimals(可选，统一小数位)、currency(可选，货币符号，默认 ¥)、fontName(可选)、" +
            "sheets 数组（create 必填，至少一个）。sheets 每项形如 " +
            "{\"name\":\"损益表\",\"columns\":[{\"header\":\"月份\",\"type\":\"text\",\"width\":14}," +
            "{\"header\":\"收入\",\"type\":\"currency\"},{\"header\":\"成本\",\"type\":\"currency\"}," +
            "{\"header\":\"毛利\",\"type\":\"currency\",\"aggregate\":\"sum\"}," +
            "{\"header\":\"毛利率\",\"type\":\"percent\",\"aggregate\":\"average\"}]," +
            "\"rows\":[[\"一月\",120000,72000,\"=B2-C2\",\"=D2/B2\"],[\"二月\",150000,88000,\"=B3-C3\",\"=D3/B3\"]]," +
            "\"totals\":true,\"totalsLabel\":\"合计\",\"note\":\"单位：元\"}。" +
            "列 schema：header(表头)、type(text|number|currency|percent|date|auto，默认 auto)、" +
            "width(列宽，可选)、decimals(列级小数位，可选，覆盖顶层)、" +
            "aggregate(sum|average|count|none，合计行用什么聚合，默认 sum)。" +
            "rows 是二维数组：数字=硬编码输入；字符串以 \"=\" 开头即公式（如 \"=B2-C2\"、\"=SUM(损益表!B2:B4)\"）；" +
            "也可写 {\"f\":\"B2*C2\"} 或 {\"v\":100}。跨表引用请直接用表名，如 \"=损益表!B2*1.1\"。" +
            "totals 可为 true（自动对所有数值列求和）、[列名/列号] 或 {\"label\":\"合计\",\"columns\":[…]}；" +
            "percent 列的数值按**小数**给（0.25 显示为 25.00%），也可写字符串 \"25%\"。" +
            "analyze 用法：{\"action\":\"analyze\",\"path\":\"/app/docs/已有.xlsx\"}，返回各工作表的名字、行列数、" +
            "表头与前几行样例。返回 JSON 含生成文件路径（produce_file）与 sheets/rows 摘要，" +
            "请据实告知用户文件位置，不要编造表格内容，也不要把整表数据复述回对话。"
        ),
    ];

    /// <summary>内置技能全部 id（供前端/诊断列示）。</summary>
    public static IEnumerable<string> SkillIds => Definitions.Select(d => d.SkillId);

    /// <summary>按发布配置决定是否播种（默认为 true；设 Agents:BuiltinXlsxSkills=false 可关闭）。</summary>
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
        var asm = typeof(BuiltinXlsxSkills).Assembly;
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
