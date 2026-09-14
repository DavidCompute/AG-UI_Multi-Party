using System.IO.Compression;
using System.Text.Json;
using AguiGroupChat.Agents.Tools;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 内置「Excel 工作簿（.xlsx）生成」技能（xlsx_book）。
///
/// 与内置 docx / pptx 技能同一条执行链路：平台自带运行时编译（Roslyn）后执行，
/// 不依赖 Node / Python / openpyxl。本测试直接跑<b>待入库的技能源码本体</b>
/// （tools/xlsx-skills/xlsx_book.cs），断言产物是合法 xlsx，且「公式优先 + 财务配色 +
/// 数字格式 + 合计行」等核心规则真的写进了 XML。
/// </summary>
[Collection(EnvVarCollection.Name)] // 改 AGUI_XLSX_OUT（进程级）→ 与其它改环境变量的用例串行
public sealed class XlsxBookSkillTests
{
    private static string SkillSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AguiGroupChat.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "tools", "xlsx-skills", "xlsx_book.cs");
        Assert.True(File.Exists(path), "找不到技能源文件：" + path);
        return File.ReadAllText(path);
    }

    /// <summary>NuGet 缓存跨用例共享，避免每个用例都重新还原 DocumentFormat.OpenXml。</summary>
    private static readonly Lazy<string> SharedCache = new(
        () => Path.Combine(Path.GetTempPath(), "agui-xlsx-skill-nuget-cache"));

    private static DotnetSkillHost NewHost()
        => new(NullLogger<DotnetSkillHost>.Instance, SharedCache.Value);

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "agui-xlsx-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>一份覆盖多工作表 / 公式 / 跨表引用 / 数字格式 / 合计 / 中文的完整工作簿。</summary>
    private static string FullWorkbookJson() => JsonSerializer.Serialize(new
    {
        title = "2026年第一季度经营分析",
        decimals = 2,
        currency = "¥",
        sheets = new object[]
        {
            new
            {
                name = "损益表",
                columns = new object[]
                {
                    new { header = "月份", type = "text", width = 14 },
                    new { header = "收入", type = "currency" },
                    new { header = "成本", type = "currency" },
                    new { header = "毛利", type = "currency", aggregate = "sum" },
                    new { header = "毛利率", type = "percent", aggregate = "average" },
                },
                rows = new object[]
                {
                    new object[] { "一月", 120000, 72000, "=B2-C2", "=D2/B2" },
                    new object[] { "二月", 150000, 88000, "=B3-C3", "=D3/B3" },
                    new object[] { "三月", 180000, 96000, "=B4-C4", "=D4/B4" },
                },
                totals = true,
                note = "单位：元；数据来源：财务系统",
            },
            new
            {
                name = "汇总",
                columns = new object[]
                {
                    new { header = "指标", type = "text" },
                    new { header = "金额", type = "currency" },
                    new { header = "占比", type = "percent" },
                },
                rows = new object[]
                {
                    new object[] { "总收入", "=SUM(损益表!B2:B4)", "=B2/参数!B2" },
                    new object[] { "总成本", "=SUM(损益表!C2:C4)", "=B3/参数!B2" },
                },
            },
            new
            {
                name = "参数",
                columns = new object[]
                {
                    new { header = "名称", type = "text" },
                    new { header = "值", type = "currency" },
                },
                rows = new object[] { new object[] { "收入口径", 2400000 } },
            },
        },
    });

    private static string RunSkill(string json, out string outDir)
    {
        outDir = TempDir();
        return RunSkillIn(json, outDir);
    }

    private static string RunSkillIn(string json, string outDir)
    {
        Environment.SetEnvironmentVariable("AGUI_XLSX_OUT", outDir);
        try
        {
            var result = NewHost().Run(SkillSource(), json, CancellationToken.None);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "xlsx-skill-last-result.json"), result);
            return result;
        }
        finally { Environment.SetEnvironmentVariable("AGUI_XLSX_OUT", null); }
    }

    [Fact]
    public void RealSkill_ProducesValidXlsxWithProduceFileMarker()
    {
        var result = RunSkill(FullWorkbookJson(), out _);
        Assert.DoesNotContain("编译失败", result);
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);

        // produce_file 是「前端可下载」的前提（网关据此入库为 att_xxx）
        var pf = doc.RootElement.GetProperty("produce_file");
        var path = pf.GetProperty("path").GetString()!;
        Assert.True(File.Exists(path), "产物不存在：" + path);
        Assert.EndsWith(".xlsx", path);
        Assert.Equal("2026年第一季度经营分析.xlsx", pf.GetProperty("name").GetString());
        Assert.True(pf.GetProperty("bytes").GetInt64() > 0);

        // 摘要：3 个工作表 / 6 行数据 / 5 列（不回灌表格数据）
        Assert.Equal(3, doc.RootElement.GetProperty("sheets").GetInt32());
        Assert.Equal(6, doc.RootElement.GetProperty("rows").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("columns").GetInt32());

        // 是结构完整的 xlsx：zip 容器 + workbook + 每表 worksheet + styles
        using var zip = ZipFile.OpenRead(path);
        Assert.Contains(zip.Entries, e => e.FullName == "[Content_Types].xml");
        Assert.Contains(zip.Entries, e => e.FullName == "xl/workbook.xml");
        Assert.Contains(zip.Entries, e => e.FullName == "xl/styles.xml");
        foreach (var n in new[] { 1, 2, 3 })
            Assert.Contains(zip.Entries, e => e.FullName == $"xl/worksheets/sheet{n}.xml");
        Assert.DoesNotContain(zip.Entries, e => e.FullName == "xl/worksheets/sheet4.xml");

        // 中文内容确实写进去了（工作表名在 workbook.xml，单元格文本在 sheetN.xml）
        var workbookXml = ReadEntry(zip, "xl/workbook.xml");
        Assert.Contains("损益表", workbookXml);
        Assert.Contains("汇总", workbookXml);
        var sheet1Xml = ReadEntry(zip, "xl/worksheets/sheet1.xml");
        Assert.Contains("月份", sheet1Xml);
        Assert.Contains("一月", sheet1Xml);
        Assert.Contains("单位：元", sheet1Xml);
    }

    [Fact]
    public void Formulas_AreWrittenIntoFormulaElement()
    {
        var result = RunSkill(FullWorkbookJson(), out _);
        using var doc = JsonDocument.Parse(result);
        var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

        using var zip = ZipFile.OpenRead(path);
        var sheet1Xml = ReadEntry(zip, "xl/worksheets/sheet1.xml");
        // 「算出来的」单元格必须是 <f>，不是写死的数值
        Assert.Contains("<f>B2-C2</f>", sheet1Xml);
        Assert.Contains("<f>D2/B2</f>", sheet1Xml);
        // 合计行 SUM 公式（合计列）
        Assert.Contains("<f>SUM(B2:B4)</f>", sheet1Xml);
        // 平均聚合（毛利率列）
        Assert.Contains("<f>AVERAGE(E2:E4)</f>", sheet1Xml);

        // 类型化读回确认：D2 是公式，B2 是硬编码数值
        using var sdoc = SpreadsheetDocument.Open(path, false);
        var wb = sdoc.WorkbookPart!;
        var d2 = FindCell(wb, "损益表", "D2");
        Assert.Equal("B2-C2", d2.CellFormula?.Text);
        Assert.Null(d2.CellValue);
        var b2 = FindCell(wb, "损益表", "B2");
        Assert.Null(b2.CellFormula);
        Assert.Equal("120000", b2.CellValue?.Text);

        // 合计行的缓存值：B 列全是硬编码数字 → 技能可算出 SUM 缓存值；
        // D 列数据是公式（引用其它单元格）→ 无法离线求值，缓存留空，由 Excel 打开时重算。
        var b5 = FindCell(wb, "损益表", "B5");
        Assert.Equal("SUM(B2:B4)", b5.CellFormula?.Text);
        Assert.Equal("450000", b5.CellValue?.Text); // 120000+150000+180000
        var d5 = FindCell(wb, "损益表", "D5");
        Assert.Equal("SUM(D2:D4)", d5.CellFormula?.Text);
        Assert.Null(d5.CellValue);
    }

    [Fact]
    public void FinancialColorStandard_InputBlueCalcBlackCrossGreen()
    {
        var result = RunSkill(FullWorkbookJson(), out _);
        using var doc = JsonDocument.Parse(result);
        var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

        // 硬编码输入 = 蓝
        Assert.Equal("FF0000FF", FontColorOf(path, "损益表", "B2"));
        Assert.Equal("FF0000FF", FontColorOf(path, "参数", "B2"));
        // 同表公式 = 黑
        Assert.Equal("FF000000", FontColorOf(path, "损益表", "D2"));
        Assert.Equal("FF000000", FontColorOf(path, "损益表", "D5")); // 合计 SUM
        // 跨表引用公式（含 "!"）= 绿
        Assert.Equal("FF00B050", FontColorOf(path, "汇总", "B2"));
        Assert.Equal("FF00B050", FontColorOf(path, "汇总", "C2"));
    }

    [Fact]
    public void NumberFormats_ThousandsPercentCurrency_AreApplied()
    {
        var result = RunSkill(FullWorkbookJson(), out _);
        using var doc = JsonDocument.Parse(result);
        var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

        // 货币：符号 + 千分位 + 统一 2 位小数
        var currency = NumberFormatCodeOf(path, "损益表", "B2");
        Assert.Contains("#,##0", currency);
        Assert.Contains("0.00", currency);
        Assert.Contains("¥", currency);

        // 百分比：0.00%（percent 列数值按小数存，如 0.25 → 25.00%）
        var percent = NumberFormatCodeOf(path, "损益表", "E2");
        Assert.Contains("%", percent);
        Assert.Contains("0.00", percent);

        // 文本列不套数字格式
        Assert.Equal("", NumberFormatCodeOf(path, "损益表", "A1"));
    }

    [Fact]
    public void TotalsRow_UsesSumFormulaAndAccountingTopBorder()
    {
        var result = RunSkill(FullWorkbookJson(), out _);
        using var doc = JsonDocument.Parse(result);
        var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

        using var sdoc = SpreadsheetDocument.Open(path, false);
        var wb = sdoc.WorkbookPart!;

        // 合计行首列标签
        var a5 = FindCell(wb, "损益表", "A5");
        Assert.Equal("合计", string.Concat(a5.Descendants<Text>().Select(t => t.Text)));

        // SUM 公式 + 会计式上框线
        Assert.Equal("SUM(B2:B4)", FindCell(wb, "损益表", "B5").CellFormula?.Text);
        Assert.True(HasThinTopBorder(path, "损益表", "B5"), "合计行应有上框线");
        Assert.True(HasThinTopBorder(path, "损益表", "C5"));
        // 数值行不应有上框线
        Assert.False(HasThinTopBorder(path, "损益表", "B2"));
    }

    [Fact]
    public void ChineseFileName_DedupesAndKeepsContent()
    {
        // 同一个输出目录跑两次 → 第二次必须换名（追加 -2），不得覆盖已有文件
        var dir = TempDir();
        var result1 = RunSkillIn(FullWorkbookJson(), dir);
        var result2 = RunSkillIn(FullWorkbookJson(), dir);

        using var d1 = JsonDocument.Parse(result1);
        using var d2 = JsonDocument.Parse(result2);
        var p1 = d1.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;
        var p2 = d2.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

        Assert.True(File.Exists(p1) && File.Exists(p2));
        Assert.NotEqual(p1, p2); // 同名不覆盖
        Assert.EndsWith("2026年第一季度经营分析.xlsx", p1);
        Assert.EndsWith("2026年第一季度经营分析-2.xlsx", p2);

        using var zip = ZipFile.OpenRead(p2);
        Assert.Contains("损益表", ReadEntry(zip, "xl/workbook.xml"));
    }

    [Fact]
    public void EmptySheets_FailsWithReadableMessage()
    {
        var result = RunSkill(JsonSerializer.Serialize(new { title = "空报表", sheets = Array.Empty<object>() }), out _);
        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("sheets", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void InvalidJson_FailsWithReadableMessage()
    {
        foreach (var bad in new[] { "这不是 JSON", "{\"title\":\"x\",\"sheets\":[", "{\"title\":\"x\",\"sheets\":[}", "{\"title\":\"x\",\"sheets\":\"oops\"}" })
        {
            var result = RunSkill(bad, out _);
            using var doc = JsonDocument.Parse(result);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), result);
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
        }
    }

    [Fact]
    public void UnwritablePath_FailsWithReadableMessage()
    {
        // 用一个「已存在的文件」当目录 → Directory.CreateDirectory 必然失败
        var blocker = Path.Combine(TempDir(), "blocker");
        File.WriteAllText(blocker, "x");
        var json = JsonSerializer.Serialize(new
        {
            title = "写入失败",
            outputPath = Path.Combine(blocker, "out.xlsx"),
            sheets = new object[]
            {
                new { name = "表1", headers = new[] { "名称" }, rows = new object[] { new object[] { "甲" } } },
            },
        });
        var result = RunSkill(json, out _);
        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), result);
        Assert.Contains("输出路径", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Analyze_ReturnsStructureSummary()
    {
        var created = RunSkill(FullWorkbookJson(), out _);
        using var cd = JsonDocument.Parse(created);
        var path = cd.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

        var analysis = RunSkill(JsonSerializer.Serialize(new { action = "analyze", path }), out _);
        using var ad = JsonDocument.Parse(analysis);
        Assert.True(ad.RootElement.GetProperty("ok").GetBoolean(), analysis);
        Assert.Equal("analyze", ad.RootElement.GetProperty("action").GetString());
        Assert.Equal(3, ad.RootElement.GetProperty("sheets").GetInt32());
        // analyze 的 rows 是“读到的物理行数”（含表头/合计/注释）：6 + 3 + 2 = 11
        Assert.Equal(11, ad.RootElement.GetProperty("rows").GetInt32());

        var details = ad.RootElement.GetProperty("sheetDetails").EnumerateArray().ToList();
        Assert.Equal(3, details.Count);
        var sheet1 = details.First(s => s.GetProperty("name").GetString() == "损益表");
        // 表头 + 3 数据 + 合计 + 注释 = 6 行
        Assert.Equal(6, sheet1.GetProperty("rows").GetInt32());
        Assert.Contains("月份", sheet1.GetProperty("headers").EnumerateArray().Select(h => h.GetString()));
    }

    [Fact]
    public void Analyze_MissingFile_FailsWithReadableMessage()
    {
        var json = JsonSerializer.Serialize(new { action = "analyze", path = Path.Combine(TempDir(), "nope.xlsx") });
        var result = RunSkill(json, out _);
        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("找不到", doc.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// 用 OpenXML 官方校验器过一遍 schema。结构不合法的话 Excel 会提示“文件已损坏 / 需要修复”。
    /// 只断言 <b>Schema 类错误</b>为空（提示类不影响打开）。
    /// </summary>
    [Fact]
    public void ProducedXlsx_PassesOpenXmlSchemaValidation()
    {
        var result = RunSkill(FullWorkbookJson(), out _);
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), result);
        var path = doc.RootElement.GetProperty("produce_file").GetProperty("path").GetString()!;

        using var sdoc = SpreadsheetDocument.Open(path, false);
        var errors = new OpenXmlValidator().Validate(sdoc)
            .Where(e => e.ErrorType == ValidationErrorType.Schema)
            .Select(e => $"{e.Description} @ {e.Path?.XPath}")
            .Take(20)
            .ToList();
        Assert.True(errors.Count == 0, "OpenXML 校验未通过：\n" + string.Join("\n", errors));
    }

    // ===== 读回工具 =====
    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.Entries.First(e => e.FullName == name);
        using var r = new StreamReader(entry.Open());
        return r.ReadToEnd();
    }

    private static Cell FindCell(WorkbookPart wb, string sheetName, string cellRef)
    {
        var sheet = wb.Workbook!.Sheets!.Elements<Sheet>().First(s => s.Name?.Value == sheetName);
        var wsp = (WorksheetPart)wb.GetPartById(sheet.Id!.Value!);
        return wsp.Worksheet!.Descendants<Cell>().First(c => c.CellReference?.Value == cellRef);
    }

    private static string FontColorOf(string path, string sheetName, string cellRef)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        var styles = wb.WorkbookStylesPart!.Stylesheet!;
        var xfs = styles.CellFormats!.Elements<CellFormat>().ToList();
        var fonts = styles.Fonts!.Elements<Font>().ToList();
        var idx = (int)(FindCell(wb, sheetName, cellRef).StyleIndex?.Value ?? 0);
        var fontId = (int)(xfs[idx].FontId?.Value ?? 0);
        return fonts[fontId].Color?.Rgb?.Value ?? "";
    }

    private static string NumberFormatCodeOf(string path, string sheetName, string cellRef)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        var styles = wb.WorkbookStylesPart!.Stylesheet!;
        var xfs = styles.CellFormats!.Elements<CellFormat>().ToList();
        var numFmts = styles.NumberingFormats?.Elements<NumberingFormat>()
            .ToDictionary(n => n.NumberFormatId!.Value, n => n.FormatCode!.Value ?? "")
            ?? new Dictionary<uint, string>();
        var idx = (int)(FindCell(wb, sheetName, cellRef).StyleIndex?.Value ?? 0);
        var id = xfs[idx].NumberFormatId?.Value ?? 0;
        return numFmts.TryGetValue(id, out var code) ? code : "";
    }

    private static bool HasThinTopBorder(string path, string sheetName, string cellRef)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        var styles = wb.WorkbookStylesPart!.Stylesheet!;
        var xfs = styles.CellFormats!.Elements<CellFormat>().ToList();
        var borders = styles.Borders!.Elements<Border>().ToList();
        var idx = (int)(FindCell(wb, sheetName, cellRef).StyleIndex?.Value ?? 0);
        var borderId = (int)(xfs[idx].BorderId?.Value ?? 0);
        return borders[borderId].TopBorder?.Style?.Value == BorderStyleValues.Thin;
    }
}
