# xlsx 表格技能（内置）

Excel 工作簿（`.xlsx`）生成 / 读取分析技能。**纯 .NET 实现**（`DocumentFormat.OpenXml` 的
SpreadsheetML），与平台其它技能同一条执行链路（Roslyn 编译 → 执行），**不依赖 Node / Python / openpyxl**。

> 设计参考 [MiniMax-AI/skills · minimax-xlsx](https://github.com/MiniMax-AI/skills) 的
> 「公式优先（Formula-First） + 财务配色标准 + 小数位统一 + 聚合直算 + CREATE/ANALYZE 分流」思想；
> 实现落法与内置 pptx / docx 技能保持一致（raw XML 组装 → OpenXML SDK 保存 → 官方校验器过 schema）。

## 产出的技能

| skillId | 名称 | 说明 |
|---|---|---|
| `xlsx_book` | 表格生成（Excel） | 多表工作簿 + 公式 + 数字格式 + 合计行 + 跨表引用；亦可读取分析既有 xlsx |

## 两种 action

`action` 缺省为 `create`。

| action | 用途 | 关键参数 |
|---|---|---|
| `create`（默认） | 从零生成工作簿并落盘 | `title` `sheets` `decimals` `currency` `fontName` `outputPath` |
| `analyze`（别名 `read` / `inspect`） | 读取既有 xlsx，返回结构摘要 | `path` |

## create 参数 schema

```jsonc
{
  "action": "create",                    // 可选，默认 create
  "title": "2026年第一季度经营分析",      // 必填：工作簿名 / 默认文件名（可用中文）
  "outputPath": "/app/docs/x.xlsx",      // 可选：直接指定落盘路径（建议不传，交给默认目录）
  "decimals": 2,                         // 可选：统一小数位（列级 decimals 覆盖它）
  "currency": "¥",                       // 可选：货币格式符号，默认 ¥
  "fontName": "微软雅黑",                 // 可选：工作簿字体
  "sheets": [ /*** 必填，至少一个 ***/ ]
}
```

### sheets[]（工作表）

| 字段 | 类型 | 说明 |
|---|---|---|
| `name` | string | 工作表名（可中文；自动去 `[]:*?/\`、截断 31 字、重名自动加 `(2)`） |
| `columns` | array | 列定义（表头 + 类型 + 宽度），见下 |
| `headers` | array | `columns` 的简写：字符串数组（不定义类型/宽度） |
| `rows` | array[] | 二维数据；每行是一维数组，见「单元格取值」 |
| `totals` | bool \| array \| object | 合计行：`true` / `["收入","成本"]` / `{"label":"合计","columns":[…]}` |
| `totalsLabel` | string | 合计行首列标签，默认 `合计` |
| `note` | string | 表下注释行（放在合计行之后） |
| `freeze` | bool | 冻结首行，默认「有表头即冻结」 |
| `autoFilter` | bool | 自动筛选，默认「有表头即开启」 |

### columns[]（列）

| 字段 | 类型 | 说明 |
|---|---|---|
| `header` | string | 表头文字（也可写 `name` / `label`） |
| `type` | string | `auto`（默认）\| `text` \| `number` \| `currency` \| `percent` \| `date` |
| `width` | number | 列宽（字符数）；缺省按表头与样本自适应（CJK 记 2 宽，夹在 3~80） |
| `decimals` | int | 列级小数位，覆盖顶层 `decimals` |
| `aggregate` | string | 合计行聚合方式：`sum`（默认）\| `average` \| `count` \| `none` |

### 单元格取值（rows 里每一项）

| 写法 | 含义 |
|---|---|
| `120000` | **硬编码输入**（蓝色 `0000FF`） |
| `"=B2-C2"` | **Excel 公式**，写入 `<f>`（同表引用 = 黑色 `000000`；含 `!` 的跨表引用 = 绿色 `00B050`） |
| `{"f":"B2*C2"}` | 公式的对象写法（等价于 `"=B2*C2"`） |
| `{"v":100}` | 显式数值 |
| `"一月"` / `"备注"` | 文本（inline string，中文安全，单格上限 32767 字符） |
| `"25%"` | 当列 `type=percent` 时按百分比解析 → 存 `0.25` |
| `"1,200"` / `"¥1,200"` | 当列 `type=number/currency` 时解析为数值 `1200` |
| `null` | 空单元格（不写 `<c>`） |

> `percent` 列的数值按**小数**给：`0.25` 在 `0.00%` 格式下显示为 `25.00%`；也可直接写字符串 `"25%"`。

## 示例

```json
{
  "title": "2026年第一季度经营分析",
  "decimals": 2,
  "currency": "¥",
  "sheets": [
    {
      "name": "损益表",
      "columns": [
        { "header": "月份", "type": "text", "width": 14 },
        { "header": "收入", "type": "currency" },
        { "header": "成本", "type": "currency" },
        { "header": "毛利", "type": "currency", "aggregate": "sum" },
        { "header": "毛利率", "type": "percent", "aggregate": "average" }
      ],
      "rows": [
        ["一月", 120000, 72000, "=B2-C2", "=D2/B2"],
        ["二月", 150000, 88000, "=B3-C3", "=D3/B3"],
        ["三月", 180000, 96000, "=B4-C4", "=D4/B4"]
      ],
      "totals": true,
      "note": "单位：元；数据来源：财务系统"
    },
    {
      "name": "汇总",
      "columns": [
        { "header": "指标", "type": "text" },
        { "header": "金额", "type": "currency" },
        { "header": "占比", "type": "percent" }
      ],
      "rows": [
        ["总收入", "=SUM(损益表!B2:B4)", "=B2/参数!B2"],
        ["总成本", "=SUM(损益表!C2:C4)", "=B3/参数!B2"]
      ]
    },
    {
      "name": "参数",
      "headers": ["名称", "值"],
      "rows": [["收入口径", 2400000]]
    }
  ]
}
```

生成的文件（默认目录，见下）：`<dir>/2026年第一季度经营分析.xlsx`。

返回（**只回摘要，不回灌表格数据**）：

```json
{
  "ok": true, "action": "create", "scene": "xlsx",
  "path": "/app/docs/2026年第一季度经营分析.xlsx",
  "sheets": 3, "rows": 6, "columns": 5,
  "produce_file": { "path": "…", "name": "2026年第一季度经营分析.xlsx", "bytes": 12345 },
  "message": "已生成 Excel 工作簿：…（3 个工作表 / 6 行数据）"
}
```

## 三条硬规则（照搬 minimax-xlsx）

1. **公式优先（Formula-First）**：凡是「算出来的」单元格一律写成 Excel 公式（`<f>`），**不写死数值**。
   `rows` 里以 `=` 开头的字符串即公式；合计行由技能自动生成 `SUM` / `AVERAGE` / `COUNTA`。
   工作簿写入 `<calcPr fullCalcOnLoad="1"/>`，打开时全量重算。
2. **财务配色标准**：
   - 硬编码输入 = **蓝** `0000FF`
   - 公式 / 计算结果 = **黑** `000000`
   - 跨表引用公式（含 `!`）= **绿** `00B050`
3. **小数位统一**：`decimals`（顶层或列级）一经指定，该列所有数值单元格统一按该小数位呈现
   （`currency` → `"¥"#,##0.00`、`percent` → `0.00%`、`number` → `#,##0.00`）。
4. **聚合直算**：合计 / 平均等直接对数据区做公式，不重复推导中间值。

## 专业排版

- **表头样式**：深蓝底（`1F3864`）+ 白色加粗 + 居中 + 自动换行。
- 列宽自适应 / 可显式指定；**冻结首行**（有表头时默认开启）。
- **自动筛选**（有表头时默认开启，范围覆盖到数据末行、不含合计行）。
- 数字格式：千分位 / 百分比 / 货币（按小数位统一）。
- **合计行上框线**（会计式）：整行细上框线 + 浅灰底 + 加粗。

## 落盘与下载（produce_file）

不传 `outputPath` 时，文件名取 `title`（保留中文），**同名自动追加 `-2` / `-3`，不覆盖已有文件**。
输出目录按优先级：

| 顺序 | 目录 |
|---|---|
| 1 | `$AGUI_XLSX_OUT` |
| 2 | `$AGUI_DOCX_OUT`（Docker 下为 `/app/docs`，带命名卷 → 默认落这里，容器重建不丢） |
| 3 | `$AGUI_PPTX_OUT` |
| 4 | `<用户主目录>/agui-xlsx` |
| 5 | `<临时目录>/agui-xlsx` |

返回 JSON 含 `produce_file` 标记 → 网关把文件登记为附件（`att_xxx`）并挂到当前消息，
**前端可直接点击下载**（`.xlsx` 已在 `AttachmentStore` 上传白名单与 MIME 推断表内）。

## analyze（读取 / 分析既有工作簿）

```json
{ "action": "analyze", "path": "/app/docs/既有文件.xlsx" }
```

返回各工作表的名字、行列数、表头与前几行样例（每表最多 12 列 × 前 4 行，单元格截断 40 字）：

```json
{
  "ok": true, "action": "analyze", "scene": "xlsx", "path": "…",
  "sheets": 3, "rows": 11, "columns": 5,
  "sheetDetails": [
    { "name": "损益表", "rows": 6, "columns": 5,
      "headers": ["月份","收入","成本","毛利","毛利率"],
      "sample": [["一月","120000","72000","=B2-C2","=D2/B2"]] }
  ],
  "message": "已读取工作簿：…"
}
```

> `create.rows` = **数据行数**（不含表头/合计/注释）；`analyze.rows` = **读到的物理行数**（含表头）。

## 文件说明

```
tools/xlsx-skills/
├── xlsx_book.cs        ← 技能正文（唯一需要维护的地方）
├── sync-builtin.mjs    ← 同步到平台内置副本（嵌入资源）
└── README.md
```

内置副本：`src/AguiGroupChat.Agents/BuiltinSkills/xlsx_book.skill.txt`

### ⚠️ 改完记得同步到内置副本

```bash
node tools/xlsx-skills/sync-builtin.mjs
```

内置目录下文件名必须是 `xlsx_book.skill.txt`：MSBuild 会把 `*.cs.txt` 里的 `cs` 当作文化区后缀
（Culture=cs），嵌入名被改写、运行时按名字找不到。脚本已代你处理命名。

改动正文后，还应递增 `BuiltinXlsxSkills.Version`，让已部署实例在升级时刷新旧快照。

## 实测验证

`tests/AguiGroupChat.Hub.Tests/XlsxBookSkillTests.cs` 走**真实执行链路**
（`DotnetSkillHost`：`#r` 解析 → NuGet 还原 → Roslyn 编译 → ALC 装载 → 反射调用 `Run`），
`BuiltinXlsxSkillsTests.cs` 覆盖内置播种/恢复/开关。实测（2026-09-14，.NET 10.0.11）：

| 测试过滤 | 结果 |
|---|---|
| `FullyQualifiedName~XlsxBook` | 总计 12，失败 0，成功 12 |
| `FullyQualifiedName~BuiltinXlsxSkills` | 总计 9，失败 0，成功 9 |
| `FullyQualifiedName~Builtin`（含 docx / pptx） | 总计 36，失败 0，成功 36 |

覆盖点：

1. 多表工作簿产出，`produce_file` 标记、sheet 数 / 行数 / 列数正确；
2. zip 结构完整（`[Content_Types].xml` / `xl/workbook.xml` / `xl/styles.xml` / `xl/worksheets/sheetN.xml`）；
3. **公式确实写进 `<f>`**（`B2-C2`、`SUM(B2:B4)`、`AVERAGE(E2:E4)`），且合计缓存值算得对；
4. **财务配色**：硬编码=蓝、同表公式=黑、跨表公式=绿（读回 `styles.xml` + cell `s` 断言）；
5. **数字格式**：千分位 / 百分比 / 货币（统一 2 位小数）；
6. **合计行**：SUM 公式 + 会计式上框线；
7. 中文文件名 / 中文内容正确，**同名去重** `-2`；
8. 错误路径：空 sheets / 非法 JSON / 目标不可写 → 返回可读中文错误而非崩溃；
9. `analyze` 返回结构摘要；文件不存在时报可读错误；
10. **通过 OpenXML 官方 schema 校验**（`OpenXmlValidator`，Schema 类错误为 0 → Excel / WPS 能正常打开）。

另外用 **openpyxl 3.1.5（独立实现，非 OpenXML SDK）** 复核过产物：8 个 XML 部件全部格式良好、
可正常 `load_workbook`，公式 / 数字格式 / 配色 / 冻结 / 自动筛选均如预期。

```bash
dotnet test tests/AguiGroupChat.Hub.Tests/AguiGroupChat.Hub.Tests.csproj --filter "FullyQualifiedName~XlsxBook"
dotnet test tests/AguiGroupChat.Hub.Tests/AguiGroupChat.Hub.Tests.csproj --filter "FullyQualifiedName~BuiltinXlsxSkills"
```

## 依赖说明

技能声明一个 NuGet 包（版本号是提示非锁）：

```
#r "nuget: DocumentFormat.OpenXml, 3.2.0"
```

- **刻意不引入 ImageSharp**：本技能不做图表（见「已知边界」），少一个依赖少一份风险。
- 与内置 docx / pptx 不同，本技能**不需要系统字体**（不做文本/图表渲染），精简容器也能跑。

## 平台约束（写这类技能必读）

1. **预置 using 不含 `System.IO`** —— 用到 `Path` / `Directory` / `File` 必须自己写 `using System.IO;`（已含）。
2. **`#r "nuget: 包, 版本"` 的版本号是提示，不是锁** —— 实测写 `3.2.0` 会还原到 `3.5.x`。
3. **入口必须是 `public static string Run(string input)`**（同步，不接受 `Task<string>`）。
4. **10 秒执行上限 & 12,000 字符输出上限** —— 本技能只返回摘要（路径 / sheet 数 / 行数），不回灌表格数据。
5. **`dotnet` 技能一律强制人工审批**（平台安全策略）。
6. SpreadsheetML 的类型名（`Cell` / `Row` / `Font` / `Color` …）极易与自定义类型重名，
   正文用 `using S = DocumentFormat.OpenXml.Spreadsheet;` 别名，**改代码时勿删别名**。
7. `SpreadsheetDocumentType` 在根命名空间 `DocumentFormat.OpenXml`（3.x 起），不在 `Packaging`。

## 平台接线（部署前必做）

`BuiltinXlsxSkills` 只是“正文 + 定义”，要开箱即用还需在平台接线（与 `BuiltinPptxSkills` 同款）：

1. `AgentOptions` 增加 `public bool? BuiltinXlsxSkills { get; set; }`；
2. `AgentSkillCatalog` 构造函数中按 `BuiltinXlsxSkills.IsEnabled(options?.BuiltinXlsxSkills)` 播种，
   并把 `SkillId` 记入 `_builtinSkills`；
3. `AgentSkillCatalog.RestoreAll` 中把 `BuiltinXlsxSkills.Definitions` 也放进 `freshBuiltins`（升级刷新）。

（`BuiltinXlsxSkillsTests` 依赖以上接线才能编译 / 通过。）

## 已知边界

- ❌ **不做图表**（柱状 / 折线 / 饼图等）：不发 `ChartPart`（DrawingML 图表 schema 复杂、旧版 Office 易报“不可读内容”）。
  需要图表请用 `pptx_deck` / `docx_report`，或把数据表放在 xlsx、图表放在 pptx。
- ❌ **不支持编辑 / 追加既有 xlsx**：`analyze` 只读结构摘要，不修改原文件，也不支持打开既有文件续写。
- ❌ 不支持条件格式、数据透视表、数据验证、合并单元格、图表、图片、批注、多级表头。
- ⚠️ 合计行只做单层聚合（`sum` / `average` / `count`），不做小计分组、不做同比 / 环比派生。
- ⚠️ 冻结仅支持“冻结首行”，不支持冻结多行 / 冻结列。
- ⚠️ 公式的**缓存值**只在“区间内全是硬编码数字”时才算得出；引用其它公式时留空，
  由 `<calcPr fullCalcOnLoad="1"/>` 让 Excel / WPS 打开时重算。
- ⚠️ 若模型自作主张传了 `outputPath`（不经 `AGUI_XLSX_OUT` 接管），文件会落在该路径；
  仍可下载，但该目录若是容器内非挂载路径，重建后即丢。提示词里最好明确「不要传 outputPath」。

## 维护提醒

正文约 4.7 万字符（~49KB）。超时只计 `Run` 执行、不含还原与编译，实测单次生成（3 表 / 多公式 / 多格式）远低于 10s。
若继续叠加特性（原生可交互图表、条件格式、透视表），请重新评估是否撞到 10s 超时，或再拆技能。
