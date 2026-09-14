# pptx 演示文稿技能（内置）

PowerPoint（`.pptx`）生成技能。**纯 .NET 实现**（`DocumentFormat.OpenXml` 的 PresentationML），
与平台其它技能同一条执行链路（Roslyn 编译 → 执行），**不依赖 Node / Python / PptxGenJS**。

> 设计参考 [MiniMax-AI/skills · pptx-generator](https://github.com/MiniMax-AI/skills/blob/main/skills/pptx-generator/SKILL.md)
> 的「页面类型体系 + 主题对象 + 设计系统」组织方式；图表处理沿用本仓库内置 docx 技能的既有决策。

## 产出的技能

| skillId | 名称 | 说明 |
|---|---|---|
| `pptx_deck` | 演示文稿生成（PPT） | 封面 / 目录 / 章节分隔 / 内容 / 两栏 / 表格 / 指标卡 / 引言 / 图片 / 图表 / 小结 / 结束页 |

## 页面类型（slides[].type）

| type | 用途 | 关键字段 |
|---|---|---|
| `cover` | 封面 | `title` `subtitle` `author` `date` |
| `toc` | 目录 | `title` `items[]` |
| `section` | 章节分隔（带大号序号） | `title` `subtitle` |
| `content` | 要点页 | `title` `bullets[]` |
| `twoCol` | 两栏对比 | `title` `left{heading,bullets}` `right{…}` |
| `table` | 表格 | `title` `headers[]` `rows[][]` |
| `kpi` | 指标卡（一行最多 4 张） | `title` `items[{value,label}]` |
| `quote` | 引言/金句 | `text` `cite` |
| `image` | 配图 | `title` `path` `caption` |
| `chart` | 图表（柱/折线/饼/环形） | `title` `chartType` `categories[]` `series[{name,values}]` `yLabel` |
| `summary` | 小结 | `title` `bullets[]` |
| `end` | 结束页 | `title` `subtitle` |

任何一页都可加 `notes`，写入**演讲者备注**。

### 要点页的两种写法

`bullets` 里的一项若写成「小标题：说明」，会自动排成**加粗小标题 + 次级说明**两行，内容页更有层次：

```json
{ "type": "content", "title": "核心能力",
  "bullets": ["协作：多角色同场会商", "记忆：RAG 长期记忆并可治理", "交付：直接产出可下载文件"] }
```

## 主题

`theme` 预设：`business`（默认）/ `tech` / `warm` / `minimal` / `dark` / `vivid`。

也可用 `themeColors` 逐项覆盖：`primary`（标题/主色）、`secondary`（辅色/正文强调）、`accent`（强调色）、
`light`（浅底/卡片）、`bg`（页面底色）、`text`（正文色）；另有 `fontTitle` / `fontBody`。

> 深色主题（如 `dark`）的 `primary` 取**亮色**——它同时用作深色底上的标题色与反色块填充。

## 版式约定

- **16:9 宽屏**：12192000 × 6858000 EMU（13.333" × 7.5"）。
- 左右安全边距 0.916"，内容宽 10515600 EMU。
- 除封面与结束页外，每页统一：页面底色 → 标题 → 标题下强调线 → 内容 → **右下角页码徽标**。
- 表格用「表头填主色 + 隔行浅色」自绘，不依赖主题部件里的表格样式（兼容性最好）。

## 图表：为什么不发 ChartPart

与内置 docx 技能同口径：**用 ImageSharp 把图表渲成 PNG，再按图片嵌入**。
DrawingML 图表的 `ChartPart` schema 复杂，容易产出旧版 PowerPoint 打不开、或提示「不可读内容」的文件。
渲成图片视觉可控、兼容性最好；代价是图表不可在 PowerPoint 里直接改数据（要改请改 JSON 重新生成）。

图表渲染失败（如容器缺字体）时会**降级为要点页**列出数据，不让整页失败。

## 落盘与下载

不传 `outputPath` 时，文件名取 `title`（保留中文），输出目录按顺序取：

| 顺序 | 目录 |
|---|---|
| 1 | `$AGUI_PPTX_OUT` |
| 2 | `$AGUI_DOCX_OUT`（复用同一可下载目录，Docker 下为 `/app/docs`） |
| 3 | `<用户主目录>/agui-pptx` |
| 4 | `<临时目录>/agui-pptx` |

返回 JSON 含 `produce_file` 标记 → 网关把文件登记为附件（`att_xxx`）并挂到当前消息，
**前端可直接点击下载**（`.pptx` 已在 `AttachmentStore` 上传白名单与 MIME 推断表内）。

## 文件说明

```
tools/pptx-skills/
├── pptx_deck.cs        ← 技能正文（唯一需要维护的地方）
├── sync-builtin.mjs    ← 同步到平台内置副本（嵌入资源）
└── README.md
```

内置副本：`src/AguiGroupChat.Agents/BuiltinSkills/pptx_deck.skill.txt`

### ⚠️ 改完记得同步到内置副本

```bash
node tools/pptx-skills/sync-builtin.mjs
```

内置目录下文件名必须是 `pptx_deck.skill.txt`：MSBuild 会把 `*.cs.txt` 里的 `cs` 当作文化区后缀
（Culture=cs），嵌入名被改写、运行时按名字找不到。脚本已代你处理命名。

改动正文后，还应递增 `BuiltinPptxSkills.Version`，让已部署实例在升级时刷新旧快照。

## 实测验证

`tests/AguiGroupChat.Hub.Tests/PptxDeckSkillTests.cs` 走**真实执行链路**
（`DotnetSkillHost`：`#r` 解析 → NuGet 还原 → Roslyn 编译 → ALC 装载 → 反射调用 `Run`），断言：

1. 覆盖 12 种页型的 11 页演示稿能产出，且 `produce_file` 标记与页数正确；
2. zip 结构完整（`[Content_Types].xml` / `presentation.xml` / 母版 / 版式 / 每页 `slideN.xml`）；
3. **通过 OpenXML 官方 schema 校验**（`OpenXmlValidator`，错误为 0）；
4. **OPC 跨部件必备关系齐备**（`Package_HasAllRequiredCrossPartRelationships`：母版有主题、版式回指母版、有备注页就有备注母版并由 presentation 关联、备注页回指幻灯片）——见下方“为什么单靠 schema 校验不够”；
5. `themeColors` / `fontTitle` 覆盖确实写进了 XML；
6. `slides` 为空时报可读错误；演讲者备注写进 `notesSlides`。

```bash
# 单测
dotnet test tests/AguiGroupChat.Hub.Tests/AguiGroupChat.Hub.Tests.csproj --filter "FullyQualifiedName~PptxDeckSkillTests"
# 独立结构检查（对照 OOXML 必备部件规则，不依赖 .NET）
python tools/verify_office_package.py 某个.pptx
```

### 为什么单靠 schema 校验不够（重要）

`OpenXmlValidator` 只校验**单个部件内部的 schema**，**不校验跨部件的必备关系**。而 PowerPoint 报“需要修复”
最常见的原因恰恰是**包结构缺部件/缺关系**，校验器一条都不报。实测踩到：

- 幻灯片母版**没有关联主题部件**（theme）：`p:sldMaster` 必须拥有主题（颜色/字体由它提供）；
- 有 `notesSlide` **却没有 `notesMaster`**：规范要求备注页关联备注母版，且 `presentation.xml` 要有
  `notesMasterIdLst`；
- 版式**没有回指母版**的关系。

这三处当时都被 `OpenXmlValidator` 放行，但**每一份产物**用 PowerPoint 打开都会提示修复。
修复后可用 `tools/verify_office_package.py` 独立复核（它按 OOXML 规则查必备部件与关系）。
教训：**写 OOXML 生成器时，除了 schema 校验，一定要单独验证包的部件与关系完整性。**

## 平台约束（写这类技能必读）

- 技能正文里 `#r "nuget: ..."` 的版本号是**提示**非强制（写 3.2.0 实际会还原到 3.5.x）。
- **`PresentationDocumentType` 在根命名空间 `DocumentFormat.OpenXml`**，不在 `Packaging`（3.x 的变化）；
  `PresentationDocument` / `SlidePart` / `SlideMasterPart` 等仍在 `Packaging`。
- `SixLabors.ImageSharp.Drawing` 的 `PathBuilder.AddArc` **没有 6 参重载**，用 7 参
  `(x, y, w, h, startAngle, sweepAngle, rotationAngle)`；`EllipsePolygon` 用 `(PointF, float)` 最稳。
- 平台预置 `using` 不含 `System.IO`，需自行 `using`（本文件已含）。
- 换行统一 `\n`；正文由同步脚本统一处理。
- **包结构三件套别忘了**（详见上方“为什么单靠 schema 校验不够”）：① 幻灯片母版挂主题；
  ② 有备注页必须有备注母版并由 `presentation.xml` 关联；③ 版式回指母版。缺任一项，OpenXML 校验器不报错，
  但 PowerPoint 会判“需要修复”。
- **图表字体必须验字形覆盖，不能只按“族名命中”就选定**：图表是 ImageSharp 渲成的 PNG，字形缺失时
  ImageSharp 会画出**空心方框（notdef）**——用户看到的就是“中文乱码”。实测踩到：候选名单前几位
  （Microsoft YaHei / SimHei / SimSun / Arial）在 Linux 容器里都不存在，第一个命中的是 `DejaVu Sans`
  （**纯拉丁**），于是图表的中文标题 / 分类标签 / 系列名全变空框，而英文坐标数字正常，很易误判为“渲染错乱”。
  现在选中候选后会用 `Font.TryGetGlyphs` 逐个验常用汉字，只采用**真的含中文字形**的字体
  （容器里会正确选到 `Noto Sans CJK SC`），并在返回 JSON 里报出 `chartFont` / `chartFontCjk` 便于排障。
  **新增/调整字体名单时请保留字形验证**——否则在服务器上会静默回退到拉丁字体。

## 已知边界

- 图表是**图片**，不可在 PowerPoint 内改数据（见上）。
- 不支持从模板/既有 pptx 编辑（只做从零生成）；不支持动画、切换、SmartArt、母版多版式。
- `image` 页的图片走 ImageSharp 读取以计算等比尺寸；ImageSharp 不支持的格式（如 svg/emf）会报可读错误。
- 表格列宽均分（不按内容自适应），列多时字号不会自动再缩。
