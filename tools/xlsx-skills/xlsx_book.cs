#r "nuget: DocumentFormat.OpenXml, 3.2.0"

// ============================================================================
// xlsx_book —— Excel 工作簿（.xlsx）生成 / 读取分析
//
// 【何时使用】
//   1) action=create（默认）：用户要从零做**表格 / 报表 / 财务模型 / 数据导出**时调用：
//      「做个 Excel」「出一张报表」「把这些数据整理成表格」「做个损益表 / 预算表」。
//   2) action=analyze：用户上传了 / 指出了已有 .xlsx，要**看结构、做摘要**时调用。
//
// 【设计参考】MiniMax-AI/skills 的 minimax-xlsx 的「公式优先 + 财务配色 + 小数位统一 +
//   聚合直算 + CREATE/ANALYZE 分流」思想；实现为**纯 .NET**（DocumentFormat.OpenXml 的
//   SpreadsheetML），与平台其它内置技能同一条 Roslyn 执行链路，不依赖 Node / Python / openpyxl。
//
// 【三条硬规则（照搬 minimax-xlsx）】
//   * 公式优先（Formula-First）：凡是“算出来的”单元格一律写成 Excel 公式（<f>），不写死数值。
//     用户在 rows 里写 "=B2-C2" 即为公式；合计行由本技能自动生成 SUM/AVERAGE/COUNTA 公式。
//   * 财务配色标准：硬编码输入 = 蓝（0000FF）；公式 / 计算结果 = 黑（000000）；
//     跨表引用公式（含 "!"）= 绿（00B050）。
//   * 小数位统一：顶层 "decimals"（或列级 "decimals"）一经指定，所有数值单元格统一按该小数位呈现。
//   * 聚合直算：合计 / 平均等直接对数据区做公式，不重复推导中间值。
//
// 【为什么不做图表】与内置 pptx / docx 技能同口径：不发 ChartPart（DrawingML 图表 schema 复杂、
//   旧版 Office 易报“不可读内容”）。本技能只做结构化表格；需要图表请用 pptx_deck / docx_report。
//   因此**不引入 ImageSharp 依赖**（宁可少依赖）。
//
// 入口：public static string Run(string input) -> JSON
//
//   input = {
//     "action": "create" | "analyze",     // 可选，默认 create（analyze 也可写 read / inspect）
//     "title": "2026年第一季度经营分析",   // create 必填（工作簿名 / 默认文件名，可用中文）
//     "outputPath": "/app/docs/x.xlsx",   // 可选，直接指定落盘路径（建议不要传，交给默认目录）
//     "decimals": 2,                      // 可选，统一小数位（列级 decimals 覆盖它）
//     "currency": "¥",                    // 可选，货币格式符号，默认 ¥
//     "fontName": "微软雅黑",              // 可选，工作簿字体
//     "sheets": [                         // create 必填，至少一个工作表
//       {
//         "name": "损益表",               // 工作表名（可中文；自动去非法字符 / 截断 31 字 / 重名加 (2)）
//         "columns": [                    // 表头 + 列定义。也可简单写 "headers": ["月份","收入"]
//           { "header": "月份", "type": "text",   "width": 14 },
//           { "header": "收入", "type": "currency" },
//           { "header": "成本", "type": "currency" },
//           { "header": "毛利", "type": "currency", "aggregate": "sum" },
//           { "header": "毛利率", "type": "percent", "aggregate": "average" }
//         ],
//         "rows": [                        // 单元格取值：
//           ["一月", 120000, 72000, "=B2-C2", "=D2/B2"],   // 数字=硬编码输入；"=…"=公式
//           ["二月", 150000, 88000, "=B3-C3", "=D3/B3"]
//         ],
//         "totals": true,                  // true | ["收入","成本"] | {"label":"合计","columns":[…]}
//         "totalsLabel": "合计",           // 可选，合计行首列标签，默认 “合计”
//         "note": "单位：元",              // 可选，表下注释行
//         "freeze": true,                  // 可选，默认有表头即冻结首行
//         "autoFilter": true               // 可选，默认有表头即开启自动筛选
//       }
//     ],
//     "path": "/app/docs/既有文件.xlsx"     // analyze 必填：要分析的文件路径
//   }
//
//   create 返回  = { ok, action, scene, path, sheets, rows, columns, produce_file:{path,name,bytes}, message }
//   analyze 返回 = { ok, action, scene, path, sheets, rows, columns, sheetDetails:[{name,rows,columns,headers,sample}], message }
//   只返回**摘要**，绝不回灌表格数据（平台 12,000 字符输出上限）。
// ============================================================================

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
// 命名空间别名：SpreadsheetML 的类型名（Cell / Row / Font / Color …）极易与自定义类型重名
using S = DocumentFormat.OpenXml.Spreadsheet;

public class Skill
{
    private const string SceneName = "xlsx";
    private const string NS_S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const int MaxCellChars = 32767;   // Excel 单元格文本上限

    // ===== 财务配色标准（ARGB）=====
    private const string ColorInput = "FF0000FF"; // 硬编码输入 = 蓝
    private const string ColorCalc = "FF000000";  // 公式 / 计算结果 = 黑
    private const string ColorCross = "FF00B050"; // 跨表引用公式 = 绿

    // 字体 id（与 StyleBook 中 fonts 顺序一致）
    private const int FontDefault = 0;
    private const int FontHeader = 1;
    private const int FontInput = 2;
    private const int FontCalc = 3;
    private const int FontCross = 4;
    private const int FontTotal = 5;

    // 填充 id
    private const int FillNone = 0;
    private const int FillGray = 1;   // gray125（Excel 必备的内置填充占位）
    private const int FillHeader = 2;
    private const int FillTotal = 3;

    // 边框 id
    private const int BorderNone = 0;
    private const int BorderTop = 1;  // 合计行上框线（会计式）

    // ===== 入口 =====
    public static string Run(string input)
    {
        try
        {
            using var reqDoc = JsonDocument.Parse(ExtractJson(input));
            var root = reqDoc.RootElement;
            var action = (Str(root, "action") ?? "create").Trim().ToLowerInvariant();
            if (action is "analyze" or "read" or "inspect" or "summary")
                return Analyze(root);
            return Create(root);
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"action\":\"error\",\"scene\":" + Js(SceneName)
                + ",\"message\":" + Js("执行失败：" + ex.GetType().Name + "：" + ex.Message) + "}";
        }
    }

    // ===== 能力一：从零生成工作簿 =====
    private static string Create(JsonElement root)
    {
        var title = Str(root, "title") ?? "工作簿";
        var models = ParseSheets(root);
        NormalizeSheetNames(models);

        string path;
        try
        {
            path = ResolveOutputPath(root, title);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("输出路径不可写：" + ex.Message);
        }

        var styles = new StyleBook(Str(root, "fontName") ?? "微软雅黑", ResolveXlsxTheme(root));
        var totalRows = 0;
        var maxCols = 0;
        foreach (var m in models)
        {
            totalRows += m.Rows.Count;
            maxCols = Math.Max(maxCols, m.Cols.Count);
        }

        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new S.Workbook();
            var sheetsEl = wbPart.Workbook.AppendChild(new S.Sheets());

            uint sheetId = 1;
            foreach (var m in models)
            {
                var wsPart = wbPart.AddNewPart<WorksheetPart>();
                // 工作表 XML 在这里生成：期间会向 styles 登记所需的 numFmt / xf（故必须先于 styles.ToXml()）
                wsPart.Worksheet = new S.Worksheet(WorksheetXml(m, styles));
                wsPart.Worksheet.Save();
                sheetsEl.Append(new S.Sheet
                {
                    Name = m.Name,
                    SheetId = sheetId++,
                    Id = wbPart.GetIdOfPart(wsPart),
                });
            }

            var stylePart = wbPart.AddNewPart<WorkbookStylesPart>();
            stylePart.Stylesheet = new S.Stylesheet(styles.ToXml());
            stylePart.Stylesheet.Save();

            // 公式的缓存值可能缺失 / 近似 → 要求 Excel/WPS 打开时全量重算（SUM 等一定会算对）
            wbPart.Workbook.AppendChild(new S.CalculationProperties { FullCalculationOnLoad = true });
            wbPart.Workbook.Save();
        }

        return "{\"ok\":true,\"action\":\"create\",\"scene\":" + Js(SceneName)
            + ",\"path\":" + Js(path)
            + ",\"sheets\":" + models.Count
            + ",\"rows\":" + totalRows
            + ",\"columns\":" + maxCols
            + ProduceMarker(path)
            + ",\"message\":" + Js("已生成 Excel 工作簿：" + path
                + "（" + models.Count + " 个工作表 / " + totalRows + " 行数据）") + "}";
    }

    // ===== 能力二：读取 / 分析既有工作簿 =====
    private static string Analyze(JsonElement root)
    {
        var raw = Str(root, "path") ?? Str(root, "file") ?? Str(root, "outputPath");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("analyze 需要 path：请给出要分析的 .xlsx 文件路径。");

        string full;
        try { full = Path.GetFullPath(raw!.Trim()); }
        catch (Exception ex) { throw new InvalidOperationException("path 非法：" + ex.Message); }
        if (!File.Exists(full))
            throw new InvalidOperationException("找不到要分析的文件：" + full);

        var sheetCount = 0;
        var totalRows = 0;
        var maxCols = 0;
        var details = new List<string>();

        using (var doc = SpreadsheetDocument.Open(full, false))
        {
            var wbPart = doc.WorkbookPart
                ?? throw new InvalidOperationException("不是有效的 Excel 工作簿（缺 WorkbookPart）：" + full);
            var shared = wbPart.SharedStringTablePart?.SharedStringTable?
                .Elements<S.SharedStringItem>()
                .Select(s => string.Concat(s.Descendants<S.Text>().Select(t => t.Text)))
                .ToList() ?? new List<string>();

            foreach (var sheet in wbPart.Workbook?.Sheets?.Elements<S.Sheet>() ?? Enumerable.Empty<S.Sheet>())
            {
                sheetCount++;
                if (wbPart.GetPartById(sheet.Id!) is not WorksheetPart wsp) continue;
                var rows = wsp.Worksheet?.Descendants<S.Row>().ToList() ?? new List<S.Row>();
                var cols = 0;
                foreach (var r in rows) cols = Math.Max(cols, r.Elements<S.Cell>().Count());
                totalRows += rows.Count;
                maxCols = Math.Max(maxCols, cols);

                if (details.Count >= 12) continue; // 摘要上限，避免超出返回字符预算

                var headers = rows.Count > 0
                    ? rows[0].Elements<S.Cell>().Select(c => SafeCellText(c, shared)).ToList()
                    : new List<string>();
                var sample = new List<string>();
                foreach (var r in rows.Take(4))
                {
                    var vals = r.Elements<S.Cell>().Take(10)
                        .Select(c => TruncateTo(SafeCellText(c, shared), 40))
                        .Select(Js);
                    sample.Add("[" + string.Join(",", vals) + "]");
                }
                details.Add("{\"name\":" + Js(sheet.Name?.Value ?? "") + ",\"rows\":" + rows.Count
                    + ",\"columns\":" + cols
                    + ",\"headers\":[" + string.Join(",", headers.Take(10).Select(h => Js(TruncateTo(h, 40)))) + "]"
                    + ",\"sample\":[" + string.Join(",", sample) + "]}");
            }
        }

        var json = "{\"ok\":true,\"action\":\"analyze\",\"scene\":" + Js(SceneName)
            + ",\"path\":" + Js(full)
            + ",\"sheets\":" + sheetCount
            + ",\"rows\":" + totalRows
            + ",\"columns\":" + maxCols
            + ",\"sheetDetails\":[" + string.Join(",", details) + "]"
            + ",\"message\":" + Js("已读取工作簿：" + full + "（" + sheetCount + " 个工作表 / " + totalRows + " 行）") + "}";

        // 兜底：结构明细过大时砍掉明细，只留计数摘要
        if (json.Length > 11500)
            json = "{\"ok\":true,\"action\":\"analyze\",\"scene\":" + Js(SceneName)
                + ",\"path\":" + Js(full)
                + ",\"sheets\":" + sheetCount + ",\"rows\":" + totalRows + ",\"columns\":" + maxCols
                + ",\"sheetDetails\":[],\"message\":" + Js("工作簿结构较大，明细已省略：" + full) + "}";
        return json;
    }

    /// <summary>produce_file 标记：告诉平台“这个文件可挂到对话里供下载”（网关扫到后登记为 att_xxx 附件）。</summary>
    private static string ProduceMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var fi = new FileInfo(path);
            return ",\"produce_file\":{\"path\":" + Js(fi.FullName)
                + ",\"name\":" + Js(fi.Name)
                + ",\"bytes\":" + fi.Length + "}";
        }
        catch { return ""; /* 标记失败不影响主返回 */ }
    }

    // ===== 输入模型 =====
    private sealed class CellVal
    {
        public bool IsEmpty = true;
        public bool IsText;
        public string Text = "";
        public bool IsNumber;
        public double Num;
        public bool IsBool;
        public bool Bool;
        public string? Formula;   // 不含前导 "="
        public double? Cache;     // 仅自算的聚合公式填缓存值；用户公式留空 → 由 fullCalcOnLoad 重算
    }

    private sealed class ColSpec
    {
        public string Header = "";
        public string Type = "auto";      // auto | text | number | currency | percent | date
        public int? Decimals;
        public double? Width;
        public string? Aggregate;        // sum | average | count | none
        public int DecimalsResolved;
        public string? FmtCode;          // null = General
    }

    private sealed class SheetModel
    {
        public string Name = "";
        public List<ColSpec> Cols = new();
        public List<CellVal[]> Rows = new();
        public bool HasHeader;
        public bool Freeze;
        public bool AutoFilter;
        public List<int> AggregateCols = new();
        public bool HasTotalRow;
        public string TotalLabel = "合计";
        public string? Note;
    }

    private static List<SheetModel> ParseSheets(JsonElement root)
    {
        var list = new List<SheetModel>();
        if (root.TryGetProperty("sheets", out var sv) && sv.ValueKind == JsonValueKind.Array)
        {
            var ordinal = 0;
            foreach (var s in sv.EnumerateArray())
            {
                ordinal++;
                if (s.ValueKind != JsonValueKind.Object) continue;
                list.Add(ParseSheet(s, root, ordinal));
            }
        }
        if (list.Count == 0)
            throw new InvalidOperationException("sheets 为空：请提供至少一个工作表（含 name / columns / rows）。");
        return list;
    }

    private static SheetModel ParseSheet(JsonElement el, JsonElement root, int ordinal)
    {
        var m = new SheetModel { Name = Str(el, "name") ?? ("Sheet" + ordinal) };

        // 1) 列定义：columns（对象 / 字符串数组）优先，其次 headers（字符串数组）
        if (el.TryGetProperty("columns", out var cv) && cv.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cv.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.String)
                    m.Cols.Add(new ColSpec { Header = c.GetString() ?? "" });
                else if (c.ValueKind == JsonValueKind.Object)
                    m.Cols.Add(new ColSpec
                    {
                        Header = Str(c, "header") ?? Str(c, "name") ?? Str(c, "label") ?? "",
                        Type = (Str(c, "type") ?? "auto").Trim().ToLowerInvariant(),
                        Decimals = IntOrNull(c, "decimals"),
                        Width = DoubleOrNull(c, "width"),
                        Aggregate = Str(c, "aggregate"),
                    });
            }
        }
        else if (el.TryGetProperty("headers", out var hv) && hv.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in hv.EnumerateArray())
                m.Cols.Add(new ColSpec
                {
                    Header = h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : h.ToString(),
                });
        }

        // 2) 数据行
        if (el.TryGetProperty("rows", out var rv) && rv.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rv.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Array) continue;
                var row = new List<CellVal>();
                var ci = 0;
                foreach (var c in r.EnumerateArray())
                {
                    row.Add(ParseCell(c, ci < m.Cols.Count ? m.Cols[ci] : null));
                    ci++;
                }
                if (row.Count > 0) m.Rows.Add(row.ToArray());
            }
        }

        // 3) 列数不足时补齐（columns 缺省则按最宽数据行推导）
        var width = 0;
        foreach (var r in m.Rows) width = Math.Max(width, r.Length);
        while (m.Cols.Count < width) m.Cols.Add(new ColSpec());
        if (m.Cols.Count == 0)
            throw new InvalidOperationException("工作表「" + m.Name + "」既无 columns 也无 rows：请至少提供表头或数据行。");

        // 行内补空单元格，保证按下标访问安全
        for (var i = 0; i < m.Rows.Count; i++)
        {
            if (m.Rows[i].Length >= m.Cols.Count) continue;
            var padded = new CellVal[m.Cols.Count];
            Array.Copy(m.Rows[i], padded, m.Rows[i].Length);
            for (var j = m.Rows[i].Length; j < padded.Length; j++) padded[j] = new CellVal();
            m.Rows[i] = padded;
        }

        m.HasHeader = m.Cols.Any(c => !string.IsNullOrWhiteSpace(c.Header));
        for (var i = 0; i < m.Cols.Count; i++) ResolveColumn(m.Cols[i], i, m.Rows, root);

        m.Freeze = BoolOrNull(el, "freeze") ?? m.HasHeader;
        m.AutoFilter = BoolOrNull(el, "autoFilter") ?? m.HasHeader;
        m.TotalLabel = Str(el, "totalsLabel") ?? "合计";
        m.Note = Str(el, "note") ?? Str(el, "footnote");

        ResolveTotals(m, el);
        return m;
    }

    private static CellVal ParseCell(JsonElement v, ColSpec? col)
    {
        var cell = new CellVal();
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                var s = v.GetString() ?? "";
                if (s.StartsWith("=", StringComparison.Ordinal) && s.Length > 1)
                {
                    cell.Formula = s.Substring(1);
                    cell.IsEmpty = false;
                }
                else if (s.Length > 0)
                {
                    cell.IsText = true;
                    cell.Text = s;
                    cell.IsEmpty = false;
                }
                break;
            case JsonValueKind.Number:
                cell.IsNumber = true;
                cell.Num = v.GetDouble();
                cell.IsEmpty = false;
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                cell.IsBool = true;
                cell.Bool = v.GetBoolean();
                cell.IsEmpty = false;
                break;
            case JsonValueKind.Object:
                var f = Str(v, "f") ?? Str(v, "formula");
                if (!string.IsNullOrWhiteSpace(f))
                {
                    cell.Formula = f!.TrimStart('=');
                    cell.IsEmpty = false;
                }
                else if (v.TryGetProperty("v", out var vv)) return ParseCell(vv, col);
                else if (v.TryGetProperty("value", out var vv2)) return ParseCell(vv2, col);
                break;
            default:
                break; // null / 其它 → 空单元格
        }

        // 按列类型做轻量数值转换（百分比字符串 "12.5%" → 0.125；"1,200" → 1200）
        var t = col?.Type ?? "auto";
        if (cell.IsText)
        {
            var txt = cell.Text.Trim();
            if (t == "percent" && txt.EndsWith("%", StringComparison.Ordinal))
            {
                var numPart = txt.TrimEnd('%').Trim();
                if (double.TryParse(numPart, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                    return new CellVal { IsNumber = true, Num = p / 100.0, IsEmpty = false };
            }
            else if ((t == "number" || t == "currency") && TryParseNumber(txt, out var d))
            {
                return new CellVal { IsNumber = true, Num = d, IsEmpty = false };
            }
        }
        return cell;
    }

    private static bool TryParseNumber(string s, out double d)
    {
        var t = (s ?? "").Trim();
        var neg = false;
        if (t.StartsWith("(", StringComparison.Ordinal) && t.EndsWith(")", StringComparison.Ordinal))
        {
            neg = true;
            t = t.Substring(1, t.Length - 2);
        }
        t = t.Replace(",", "").Replace(" ", "").Replace("¥", "").Replace("￥", "")
             .Replace("$", "").Replace("€", "").Replace("£", "");
        d = 0;
        if (t.Length == 0 || !double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return false;
        if (neg) d = -d;
        return true;
    }

    /// <summary>解析列的数字格式：显式列级 decimals &gt; 顶层 decimals &gt; 类型默认值。</summary>
    private static void ResolveColumn(ColSpec c, int idx, List<CellVal[]> rows, JsonElement root)
    {
        var globalDec = IntOrNull(root, "decimals");
        var sym = CurrencySymbol(root);

        var hasFormula = false;
        var anyFrac = false;
        foreach (var r in rows)
        {
            var cell = r[idx];
            if (cell.Formula is not null) hasFormula = true;
            else if (cell.IsNumber && cell.Num != Math.Floor(cell.Num)) anyFrac = true;
        }

        int dec;
        if (c.Decimals.HasValue) dec = c.Decimals.Value;
        else if (globalDec.HasValue) dec = globalDec.Value;
        else if (c.Type is "currency" or "percent") dec = 2;
        else if (c.Type == "number") dec = (anyFrac || hasFormula) ? 2 : 0;
        else dec = 0;
        if (dec < 0) dec = 0;
        if (dec > 8) dec = 8;
        c.DecimalsResolved = dec;

        // 顶层指定了小数位时，auto 数值列也统一按数字格式呈现（“所有数值统一按该格式”）
        var type = c.Type == "auto" && globalDec.HasValue ? "number" : c.Type;
        c.FmtCode = FormatCode(type, dec, sym);
    }

    private static string? FormatCode(string type, int dec, string sym)
    {
        var zeros = new string('0', dec);
        var dot = dec > 0 ? "." + zeros : "";
        return type switch
        {
            "currency" => "\"" + sym + "\"#,##0" + dot,
            "percent" => dec > 0 ? "0." + zeros + "%" : "0%",
            "number" => "#,##0" + dot,
            "date" => "yyyy-mm-dd",
            _ => null, // auto / text / general
        };
    }

    private static string CurrencySymbol(JsonElement root)
    {
        var s = (Str(root, "currency") ?? Str(root, "currencySymbol") ?? "¥").Trim().Replace("\"", "");
        if (s.Length == 0) s = "¥";
        if (s.Length > 4) s = s.Substring(0, 4);
        return s;
    }

    // ===== 合计 / 聚合 =====
    private static void ResolveTotals(SheetModel m, JsonElement el)
    {
        if (!el.TryGetProperty("totals", out var tv)) return;
        if (tv.ValueKind is JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined) return;

        var target = new List<int>();
        if (tv.ValueKind == JsonValueKind.True)
        {
            for (var i = 0; i < m.Cols.Count; i++) if (IsNumericCol(m, i)) target.Add(i);
        }
        else if (tv.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tv.EnumerateArray())
            {
                var idx = ColIndex(t, m);
                if (idx >= 0 && !target.Contains(idx)) target.Add(idx);
            }
        }
        else if (tv.ValueKind == JsonValueKind.Object)
        {
            var lbl = Str(tv, "label");
            if (!string.IsNullOrWhiteSpace(lbl)) m.TotalLabel = lbl!;
            if (tv.TryGetProperty("columns", out var cvs) && cvs.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in cvs.EnumerateArray())
                {
                    var idx = ColIndex(t, m);
                    if (idx >= 0 && !target.Contains(idx)) target.Add(idx);
                }
            }
            else
            {
                for (var i = 0; i < m.Cols.Count; i++) if (IsNumericCol(m, i)) target.Add(i);
            }
        }
        else return;

        target = target.Where(i => !string.Equals(m.Cols[i].Aggregate, "none", StringComparison.OrdinalIgnoreCase)).ToList();
        if (target.Count == 0 || m.Rows.Count == 0) return;
        m.AggregateCols = target;
        m.HasTotalRow = true;
    }

    private static bool IsNumericCol(SheetModel m, int i)
    {
        var t = m.Cols[i].Type;
        if (t is "number" or "currency" or "percent") return true;
        if (t is "text" or "date") return false;
        foreach (var r in m.Rows)
        {
            var c = r[i];
            if (c.IsNumber || c.Formula is not null) return true;
        }
        return false;
    }

    private static int ColIndex(JsonElement t, SheetModel m)
    {
        if (t.ValueKind == JsonValueKind.Number)
        {
            var i = (int)t.GetDouble();
            return i >= 0 && i < m.Cols.Count ? i : -1;
        }
        if (t.ValueKind != JsonValueKind.String) return -1;
        var s = (t.GetString() ?? "").Trim();
        for (var i = 0; i < m.Cols.Count; i++)
            if (string.Equals(m.Cols[i].Header, s, StringComparison.OrdinalIgnoreCase)) return i;
        // 允许用列字母（"C"）或 A1 列字母指定
        if (s.Length is > 0 and <= 3 && s.All(char.IsLetter))
        {
            var idx = 0;
            foreach (var ch in s.ToUpperInvariant()) idx = idx * 26 + (ch - 'A' + 1);
            idx--;
            if (idx >= 0 && idx < m.Cols.Count) return idx;
        }
        return -1;
    }

    /// <summary>聚合公式的缓存值：仅当区间内全是硬编码数值（无公式引用）时可算，否则留空由 Excel 重算。</summary>
    private static double? ComputeCache(string func, SheetModel m, int ci)
    {
        double sum = 0;
        var count = 0;
        foreach (var r in m.Rows)
        {
            var c = r[ci];
            if (c.Formula is not null) return null;
            if (c.IsNumber) { sum += c.Num; count++; }
        }
        return func switch
        {
            "COUNTA" => count,
            "AVERAGE" => count > 0 ? sum / count : 0,
            _ => sum,
        };
    }

    // ===== 工作表 XML =====
    private static string WorksheetXml(SheetModel m, StyleBook st)
    {
        var colCount = m.Cols.Count;
        var dataStart = m.HasHeader ? 2 : 1;
        var dataEnd = m.Rows.Count > 0 ? dataStart + m.Rows.Count - 1 : (m.HasHeader ? 1 : 0);
        var totalRow = m.HasTotalRow && m.Rows.Count > 0 ? dataEnd + 1 : 0;
        var noteRow = string.IsNullOrWhiteSpace(m.Note) ? 0 : (totalRow > 0 ? totalRow : dataEnd) + 1;
        var lastRow = Math.Max(Math.Max(totalRow, noteRow), Math.Max(dataEnd, 1));
        var lastCol = ColName(colCount);

        var rowsXml = new StringBuilder();

        // 表头行
        if (m.HasHeader)
        {
            rowsXml.Append("<row r=\"1\" ht=\"20\" customHeight=\"1\">");
            for (var i = 0; i < colCount; i++)
                rowsXml.Append(CellXml(ColName(i + 1) + "1",
                    st.Xf(0, FontHeader, FillHeader, BorderNone, "center"), TextCell(m.Cols[i].Header)));
            rowsXml.Append("</row>");
        }

        // 数据行：数字=蓝（硬编码），公式=黑（同表）/ 绿（跨表）
        for (var ri = 0; ri < m.Rows.Count; ri++)
        {
            var r = dataStart + ri;
            rowsXml.Append("<row r=\"").Append(r).Append("\">");
            for (var ci = 0; ci < colCount; ci++)
            {
                var cell = m.Rows[ri][ci];
                if (cell.IsEmpty) continue;
                var col = m.Cols[ci];
                int numFmt;
                int font;
                if (cell.Formula is not null)
                {
                    numFmt = st.NumFmt(col.FmtCode);
                    font = cell.Formula.Contains('!') ? FontCross : FontCalc;
                }
                else if (cell.IsNumber)
                {
                    numFmt = st.NumFmt(col.FmtCode);
                    font = FontInput;
                }
                else
                {
                    numFmt = 0;
                    font = FontDefault;
                }
                rowsXml.Append(CellXml(ColName(ci + 1) + r, st.Xf(numFmt, font, FillNone, BorderNone, ""), cell));
            }
            rowsXml.Append("</row>");
        }

        // 合计行：SUM / AVERAGE / COUNTA 公式 + 上框线
        if (totalRow > 0)
        {
            var agg = new HashSet<int>(m.AggregateCols);
            var labelCol = -1;
            for (var i = 0; i < colCount; i++) if (!agg.Contains(i)) { labelCol = i; break; }

            rowsXml.Append("<row r=\"").Append(totalRow).Append("\" ht=\"18\" customHeight=\"1\">");
            for (var ci = 0; ci < colCount; ci++)
            {
                var reference = ColName(ci + 1) + totalRow;
                var col = m.Cols[ci];
                if (agg.Contains(ci))
                {
                    var kind = (col.Aggregate ?? "sum").Trim().ToLowerInvariant();
                    var func = kind switch { "average" => "AVERAGE", "count" => "COUNTA", _ => "SUM" };
                    var range = ColName(ci + 1) + dataStart + ":" + ColName(ci + 1) + dataEnd;
                    var cell = new CellVal
                    {
                        Formula = func + "(" + range + ")",
                        Cache = ComputeCache(func, m, ci),
                        IsEmpty = false,
                    };
                    rowsXml.Append(CellXml(reference,
                        st.Xf(st.NumFmt(col.FmtCode), FontTotal, FillTotal, BorderTop, ""), cell));
                }
                else
                {
                    var cell = ci == labelCol ? TextCell(m.TotalLabel) : new CellVal();
                    rowsXml.Append(CellXml(reference,
                        st.Xf(0, FontTotal, FillTotal, BorderTop, ""), cell));
                }
            }
            rowsXml.Append("</row>");
        }

        // 表下注释
        if (noteRow > 0)
        {
            rowsXml.Append("<row r=\"").Append(noteRow).Append("\">");
            rowsXml.Append(CellXml("A" + noteRow,
                st.Xf(0, FontDefault, FillNone, BorderNone, ""), TextCell(m.Note!)));
            rowsXml.Append("</row>");
        }

        var sb = new StringBuilder();
        sb.Append("<worksheet xmlns=\"").Append(NS_S).Append("\">");
        sb.Append("<dimension ref=\"A1:").Append(lastCol).Append(lastRow).Append("\"/>");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
        if (m.Freeze && m.HasHeader)
            sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>")
              .Append("<selection pane=\"bottomLeft\" activeCell=\"A2\" sqref=\"A2\"/>");
        sb.Append("</sheetView></sheetViews>");
        sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");
        if (colCount > 0)
        {
            sb.Append("<cols>");
            for (var i = 0; i < colCount; i++)
            {
                var w = m.Cols[i].Width ?? AutoWidth(m, i);
                if (w < 3) w = 3;
                if (w > 80) w = 80;
                sb.Append("<col min=\"").Append(i + 1).Append("\" max=\"").Append(i + 1)
                  .Append("\" width=\"").Append(w.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>").Append(rowsXml).Append("</sheetData>");
        if (m.AutoFilter && m.HasHeader && m.Rows.Count > 0 && dataEnd >= 1)
            sb.Append("<autoFilter ref=\"A1:").Append(lastCol).Append(dataEnd).Append("\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static CellVal TextCell(string s)
        => new() { IsText = true, Text = s ?? "", IsEmpty = false };

    private static string CellXml(string reference, int style, CellVal c)
    {
        if (c.Formula is not null)
        {
            var cache = c.Cache.HasValue ? "<v>" + Num(c.Cache.Value) + "</v>" : "";
            return "<c r=\"" + reference + "\" s=\"" + style + "\"><f>" + Xml(c.Formula) + "</f>" + cache + "</c>";
        }
        if (c.IsNumber)
            return "<c r=\"" + reference + "\" s=\"" + style + "\"><v>" + Num(c.Num) + "</v></c>";
        if (c.IsBool)
            return "<c r=\"" + reference + "\" s=\"" + style + "\" t=\"b\"><v>" + (c.Bool ? "1" : "0") + "</v></c>";
        if (c.IsText && c.Text.Length > 0)
            return "<c r=\"" + reference + "\" s=\"" + style + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">"
                + Xml(TruncateTo(c.Text, MaxCellChars)) + "</t></is></c>";
        // 空单元格：仍写出样式（合计行需要靠它把上框线连起来）
        return "<c r=\"" + reference + "\" s=\"" + style + "\"/>";
    }

    private static string Num(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) d = 0;
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>列宽自适应：取表头与样本单元格的最宽显示宽度（CJK 记 2），夹在 [3, 80]。</summary>
    private static double AutoWidth(SheetModel m, int col)
    {
        double w = 8;
        if (!string.IsNullOrWhiteSpace(m.Cols[col].Header)) w = Math.Max(w, DisplayWidth(m.Cols[col].Header) + 2);
        var seen = 0;
        foreach (var r in m.Rows)
        {
            if (seen >= 50) break;
            var c = r[col];
            if (c.IsText && c.Text.Length > 0) w = Math.Max(w, DisplayWidth(c.Text) + 2);
            seen++;
        }
        return Math.Round(Math.Min(w, 80), 1);
    }

    private static int DisplayWidth(string s)
    {
        var w = 0;
        foreach (var c in s) w += c > 0x2E80 ? 2 : 1; // 粗略：CJK / 全角记双宽
        return w;
    }

    private static string TruncateTo(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? "") : s.Substring(0, max);

    /// <summary>1-based 列序号 → 列字母（1→A，27→AA）。</summary>
    private static string ColName(int index)
    {
        var sb = new StringBuilder();
        var n = index;
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)('A' + n % 26));
            n /= 26;
        }
        return sb.ToString();
    }

    // ===== 样式表（动态登记 numFmt / xf）=====
    // ===== 配色（设计系统：18 套命名调色板）=====

    /// <summary>
    /// 表格配色。默认值与改造前完全一致（不传 theme 就是原来的观感）。
    ///
    /// <para>
    /// 哪些东西由品牌色控制、哪些不控，是个取舍：<b>表头底 / 合计线 / 正文色</b>跟着 palette 走
    /// （这是“看起来是不是这家公司的表”的部分）；而<b>输入蓝、跨表引用绿</b>是 Excel 多年的约定，
    /// 与品牌无关，保持固定——把它们也染成品牌色反而会让熟表格的人看错。
    /// </para>
    /// </summary>
    private sealed class XlsxTheme
    {
        public string Header = "1F3864";    // 表头底色
        public string OnHeader = "FFFFFF";  // 表头上的字
        public string Band = "F2F2F2";      // 合计行底色
        public string Rule = "808080";      // 合计行上框线
        public string Ink = "000000";       // 正文
    }

    /// <summary>18 套命名调色板（与 pptx 同一套，来自 design-system.md）。每套只给 5 色，角色由亮度/彩度推出。</summary>
    private static readonly (string Name, string C1, string C2, string C3, string C4, string C5, bool Dark)[] XlsxPalettes =
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

    private static XlsxTheme ResolveXlsxTheme(JsonElement root)
    {
        var th = new XlsxTheme();
        var raw = (Str(root, "theme") ?? "").Trim();
        if (raw.Length == 0) return th;              // 不传 = 保持改造前的观感

        // 也允许直接给一个十六进制主色：theme: "#2F6B4F"
        var direct = HexOrNull(raw);
        if (direct is not null) return WithHeader(th, direct);

        var key = raw.ToLowerInvariant();
        if (string.Equals(key, "business", StringComparison.Ordinal))
            return WithHeader(th, "1F3864");         // 历史默认名
        foreach (var p in XlsxPalettes)
            if (p.Name == key)
            {
                var all = new[] { p.C1, p.C2, p.C3, p.C4, p.C5 }
                    .Select(c => c.TrimStart('#').ToUpperInvariant()).ToArray();
                // 表头用“最深色”：它最经得起反白字，也最像公司色
                var darkest = all.OrderBy(RelLumX).First();
                return WithHeader(th, darkest);
            }
        return th;                                    // 认不出就安安静静用默认
    }

    /// <summary>6 位 RGB → 8 位 ARGB（SpreadsheetML 的 font.rgb / fill.fgColor 都是 ARGB）。</summary>
    private static string ArgB(string hex)
        => hex.Length == 8 ? hex : "FF" + hex;

    private static XlsxTheme WithHeader(XlsxTheme th, string header)
    {
        th.Header = header;
        th.OnHeader = OnColorX(header);              // 表头字保证看得清
        th.Band = MixX(header, "FFFFFF", 0.92);      // 合计行：主色的极淡版
        th.Rule = MixX(header, "FFFFFF", 0.62);      // 合计线：主色的中淡版
        th.Ink = "1A1A1A";                           // 正文保持深灰（跟着品牌色走反而难读）
        return th;
    }

    // ---- 颜色工具（WCAG 相对亮度）----

    private static string? HexOrNull(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v.Trim().TrimStart('#').ToUpperInvariant();
        return s.Length == 6 && s.All(Uri.IsHexDigit) ? s : null;
    }

    private static int[] RgbX(string hex) =>
        [Convert.ToInt32(hex.Substring(0, 2), 16), Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16)];

    private static double RelLumX(string hex)
    {
        var c = RgbX(hex);
        double Ch(int v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c[0]) + 0.7152 * Ch(c[1]) + 0.0722 * Ch(c[2]);
    }

    private static double ContrastX(string a, string b)
    {
        var la = RelLumX(a); var lb = RelLumX(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string MixX(string a, string b, double t)
    {
        var ca = RgbX(a); var cb = RgbX(b);
        var sb = new StringBuilder(6);
        for (var i = 0; i < 3; i++) sb.Append(((int)Math.Round(ca[i] + (cb[i] - ca[i]) * t)).ToString("X2"));
        return sb.ToString();
    }

    /// <summary>该底色上最易读的文字色（表头反白字的根据）。</summary>
    private static string OnColorX(string bg)
        => ContrastX("FFFFFF", bg) >= ContrastX("1A1A1A", bg) ? "FFFFFF" : "1A1A1A";

    private sealed class StyleBook
    {
        private readonly string _font;
        private readonly XlsxTheme _t;
        private readonly List<string> _numFmtXml = new();
        private readonly Dictionary<string, int> _numFmtIds = new(StringComparer.Ordinal);
        private readonly List<string> _xfXml = new();
        private readonly Dictionary<string, int> _xfIds = new(StringComparer.Ordinal);
        private int _nextNumFmtId = 164; // 164 起为自定义 numFmt（内置 0~163）

        public StyleBook(string fontName, XlsxTheme theme)
        {
            _font = Xml(fontName);
            _t = theme;
            _xfIds["0|0|0|0|"] = 0;
            _xfXml.Add("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
        }

        public int NumFmt(string? code)
        {
            if (string.IsNullOrEmpty(code) || code == "General") return 0;
            if (_numFmtIds.TryGetValue(code!, out var id)) return id;
            id = _nextNumFmtId++;
            _numFmtIds[code!] = id;
            _numFmtXml.Add("<numFmt numFmtId=\"" + id + "\" formatCode=\"" + Xml(code!) + "\"/>");
            return id;
        }

        public int Xf(int numFmtId, int fontId, int fillId, int borderId, string align)
        {
            var key = numFmtId + "|" + fontId + "|" + fillId + "|" + borderId + "|" + align;
            if (_xfIds.TryGetValue(key, out var idx)) return idx;

            var sb = new StringBuilder();
            sb.Append("<xf numFmtId=\"").Append(numFmtId).Append("\" fontId=\"").Append(fontId)
              .Append("\" fillId=\"").Append(fillId).Append("\" borderId=\"").Append(borderId).Append("\" xfId=\"0\"");
            if (numFmtId != 0) sb.Append(" applyNumberFormat=\"1\"");
            if (fontId != 0) sb.Append(" applyFont=\"1\"");
            if (fillId != 0) sb.Append(" applyFill=\"1\"");
            if (borderId != 0) sb.Append(" applyBorder=\"1\"");
            if (string.IsNullOrEmpty(align)) sb.Append("/>");
            else sb.Append(" applyAlignment=\"1\"><alignment horizontal=\"").Append(align)
                   .Append("\" vertical=\"center\" wrapText=\"1\"/></xf>");

            idx = _xfXml.Count;
            _xfXml.Add(sb.ToString());
            _xfIds[key] = idx;
            return idx;
        }

        public string ToXml()
        {
            var sb = new StringBuilder();
            sb.Append("<styleSheet xmlns=\"").Append(NS_S).Append("\">");

            sb.Append("<numFmts count=\"").Append(_numFmtXml.Count).Append("\">");
            foreach (var f in _numFmtXml) sb.Append(f);
            sb.Append("</numFmts>");

            // 0 默认 / 1 表头(反色) / 2 输入(蓝) / 3 计算(黑) / 4 跨表(绿) / 5 合计(黑粗)
            // 输入蓝 / 跨表绿是 Excel 长期约定（与品牌无关），保持固定；品牌色体现在表头底与合计线。
            // 【单位】font 的 rgb 需要 8 位 ARGB；主题色存的是 6 位 RGB，这里补 FF 前缀。
            sb.Append("<fonts count=\"6\">")
              .Append(Font(false, ArgB(_t.Ink)))
              .Append(Font(true, ArgB(_t.OnHeader)))
              .Append(Font(false, ColorInput))
              .Append(Font(false, ArgB(_t.Ink)))
              .Append(Font(false, ColorCross))
              .Append(Font(true, ArgB(_t.Ink)))
              .Append("</fonts>");

            sb.Append("<fills count=\"4\">")
              .Append("<fill><patternFill patternType=\"none\"/></fill>")
              .Append("<fill><patternFill patternType=\"gray125\"/></fill>")
              .Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF").Append(_t.Header).Append("\"/><bgColor indexed=\"64\"/></patternFill></fill>")
              .Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF").Append(_t.Band).Append("\"/><bgColor indexed=\"64\"/></patternFill></fill>")
              .Append("</fills>");

            sb.Append("<borders count=\"2\">")
              .Append("<border><left/><right/><top/><bottom/><diagonal/></border>")
              .Append("<border><left/><right/><top style=\"thin\"><color rgb=\"FF").Append(_t.Rule).Append("\"/></top><bottom/><diagonal/></border>")
              .Append("</borders>");

            sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");

            sb.Append("<cellXfs count=\"").Append(_xfXml.Count).Append("\">");
            foreach (var x in _xfXml) sb.Append(x);
            sb.Append("</cellXfs>");

            sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
            sb.Append("<dxfs count=\"0\"/>");
            sb.Append("<tableStyles count=\"0\" defaultTableStyle=\"TableStyleMedium2\" defaultPivotStyle=\"PivotStyleLight16\"/>");
            sb.Append("</styleSheet>");
            return sb.ToString();
        }

        private string Font(bool bold, string argb)
        {
            var sb = new StringBuilder("<font>");
            if (bold) sb.Append("<b/>");
            sb.Append("<sz val=\"11\"/>");
            sb.Append("<color rgb=\"").Append(argb).Append("\"/>");
            sb.Append("<name val=\"").Append(_font).Append("\"/>");
            sb.Append("</font>");
            return sb.ToString();
        }
    }

    // ===== 工作表名规整 =====
    private static void NormalizeSheetNames(List<SheetModel> models)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < models.Count; i++)
        {
            var n = SanitizeSheetName(models[i].Name);
            if (string.IsNullOrWhiteSpace(n)) n = "Sheet" + (i + 1);
            if (!used.Add(n))
            {
                var stem = n;
                var k = 2;
                do
                {
                    var tag = "(" + k + ")";
                    n = TruncateTo(stem, 31 - tag.Length) + tag;
                    k++;
                } while (!used.Add(n));
            }
            models[i].Name = n;
        }
    }

    private static string SanitizeSheetName(string? raw)
    {
        var sb = new StringBuilder();
        foreach (var c in (raw ?? "").Trim())
        {
            if (c is '[' or ']' or ':' or '*' or '?' or '/' or '\\') continue;
            if (char.IsControl(c)) continue;
            sb.Append(c);
        }
        var name = sb.ToString().Trim('\'').Trim();
        if (name.Length > 31) name = name.Substring(0, 31).TrimEnd();
        return name;
    }

    // ===== 落盘 =====
    private static string ResolveOutputPath(JsonElement root, string title)
    {
        var outPath = Str(root, "outputPath");
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            var p = Path.GetFullPath(outPath!.Trim());
            if (string.IsNullOrEmpty(Path.GetExtension(p))) p += ".xlsx";
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            return p;
        }
        var d = DefaultOutputDir();
        Directory.CreateDirectory(d);
        return UniquePath(d, SafeFileNameFromTitle(title));
    }

    /// <summary>默认输出目录：AGUI_XLSX_OUT &gt; AGUI_DOCX_OUT（Docker 下为 /app/docs，带命名卷）&gt; AGUI_PPTX_OUT &gt; 主目录/agui-xlsx &gt; 临时目录/agui-xlsx。</summary>
    private static string DefaultOutputDir()
    {
        foreach (var envName in new[] { "AGUI_XLSX_OUT", "AGUI_DOCX_OUT", "AGUI_PPTX_OUT" })
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
                var d = Path.Combine(home, "agui-xlsx");
                Directory.CreateDirectory(d);
                return d;
            }
        }
        catch { /* 无主目录 → 临时目录 */ }
        var tmp = Path.Combine(Path.GetTempPath(), "agui-xlsx");
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
        // 连续空白折叠为单个空格
        var sb = new StringBuilder();
        var lastSpace = false;
        foreach (var c in b.ToString())
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace && lastSpace) continue;
            sb.Append(isSpace ? ' ' : c);
            lastSpace = isSpace;
        }
        var name = sb.ToString().Trim().TrimEnd('.');
        if (name.Length == 0) name = "workbook";
        if (name.Length > 80) name = name.Substring(0, 80).TrimEnd();
        var upper = name.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL" || upper.StartsWith("COM", StringComparison.Ordinal)
            || upper.StartsWith("LPT", StringComparison.Ordinal))
            name = "_" + name;
        return name;
    }

    private static string UniquePath(string dir, string baseName)
    {
        var candidate = Path.Combine(dir, baseName + ".xlsx");
        if (!File.Exists(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(dir, baseName + "-" + i + ".xlsx");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, baseName + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx");
    }

    // ===== 通用 =====
    private static string Xml(string? s)
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

    private static string Safe(string? s) => (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

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

    private static int? IntOrNull(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String
            && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j)) return j;
        return null;
    }

    private static double? DoubleOrNull(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    private static bool? BoolOrNull(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>读回单元格文本（共享字符串 / 内联字符串 / 布尔 / 数值 / 公式缓存值）。</summary>
    private static string SafeCellText(S.Cell c, List<string> shared)
    {
        var t = c.DataType?.Value;
        if (t == S.CellValues.SharedString && int.TryParse(c.InnerText, out var i) && i >= 0 && i < shared.Count)
            return shared[i];
        if (t == S.CellValues.InlineString)
            return string.Concat(c.Descendants<S.Text>().Select(x => x.Text));
        if (t == S.CellValues.Boolean) return c.InnerText == "1" ? "TRUE" : "FALSE";
        var v = c.CellValue?.Text ?? c.InnerText;
        var f = c.CellFormula?.Text;
        return string.IsNullOrEmpty(v) && !string.IsNullOrEmpty(f) ? "=" + f : v;
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
