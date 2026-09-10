# docx 技能可行性验证（方案 A）

对「参考 MiniMax-AI/skills 的 minimax-docx 做法，能否移植到本平台」的**实测结论**。

## 验证方式

不是纸面推演：本目录的 `docx_report.cs` 已通过平台**真实执行路径**跑通 ——
`DotnetSkillHost`（`#r` 解析 → NuGet 还原 → Roslyn 编译 → ALC 装载 → 反射调用 `Run`），
即生产环境同一段代码，非替代实现。

## 验证结果（全部实测）

| 检查项 | 结果 |
|---|---|
| `#r "nuget: DocumentFormat.OpenXml"` 还原 | ✅ 成功（实测解析到 3.5.1.0） |
| Roslyn 编译 | ✅ 通过 |
| 执行并产出 .docx | ✅ 生成 3049 字节文件，13 个内容块 |
| zip 结构完整（PK 头 + 必需部件） | ✅ `[Content_Types].xml` / `word/document.xml` / `word/styles.xml` |
| XML 格式良好 | ✅ 5/5 部件解析通过 |
| OpenXML 元素顺序（防损坏） | ✅ `sectPr` 最后、`pPr`/`rPr` 居首、`tblPr→tblGrid→tr` |
| 标题样式含 `outlineLvl`（导航窗格/TOC 可用） | ✅ |
| 首行缩进（`firstLineChars` 东亚版式） | ✅ |
| 表格三线表 + 表头跨页重复 | ✅ 9 个单元格，`tblHeader` 就位 |

## 过程中发现的平台级注意事项

这两条是实测踩到的，对**所有** C# 技能作者都适用：

1. **`using System.IO` 不在预置 using 里。**
   `DotnetSkillHost.Preamble` 预置了 `System` / `Linq` / `Text.Json` / `Net.Http` 等，
   但**不含 `System.IO`**。用到 `Path` / `Directory` / `File` 必须自己写 `using System.IO;`，
   否则报 `CS0103: 当前上下文中不存在名称"Path"`。（本次开发正是卡在这里。）

2. **`#r "nuget: 包, 版本"` 的版本号是提示，不是约束。**
   实测写 `3.2.0`，解析器实际还原到 `3.5.1`（取最新可用）。需要精确锁版本时不能依赖该指令。

## 结论

**方案 A（单文件 C# 技能 + 内联排版规则）完全可行，且已跑通。**

但与原版 `minimax-docx` 的能力差距是结构性的，不在这一档：

- 原版的 13 套排版配方、XSD 校验闸、模板套用都依赖**伴生资产文件**（`Samples/*.cs`、`references/*.md`、`assets/*.xsd`）。
- 本平台 `AgentSkillDefinition` 只有单个 `Body: string`，**无法携带文件**；`Run(string input)` 也无资产句柄。
- 因此这些能力只能内联进源码，受 `Body` 体积与可维护性限制。

要补齐，需要平台改造（新增技能资产字段 + 执行时释放到沙箱 + 扩展入口签名）。详见评估结论。

## 复现方式

将 `docx_report.cs` 全文作为技能的 **Body** 内容（kind = `dotnet`，executionLocation = `server`），
调用时传入：

```json
{
  "title": "关于开展数字员工平台试运行工作的报告",
  "layout": "gongwen",
  "sections": [
    { "heading": "一、工作背景", "level": 1 },
    { "paragraph": "为提高业务办理效率，我单位启动平台试运行。" },
    { "table": { "headers": ["指标","试运行前","试运行后"], "rows": [["平均处理时长","42 分钟","28 分钟"]] } }
  ]
}
```

返回 `{"ok":true,"path":"...docx","blocks":N}`。

> 注：该技能为验证件，排版常量集中在文件顶部，便于按需调整。
> 若正式启用，建议按场景拆分为多个技能（公文 / 报告 / 合同），而非一个巨型技能。
