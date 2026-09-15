# pptx 演示文稿技能（内置）

PowerPoint（`.pptx`）生成技能。**纯 .NET 实现**（`DocumentFormat.OpenXml` 的 PresentationML），
与平台其它技能同一条执行链路（Roslyn 编译 → 执行），**不依赖 Node / Python / PptxGenJS**。

> 设计参考 [MiniMax-AI/skills · pptx-generator](https://github.com/MiniMax-AI/skills/blob/main/skills/pptx-generator/SKILL.md)
> 的「页面类型体系 + 主题对象 + 设计系统」组织方式；图表处理沿用本仓库内置 docx 技能的既有决策。

## 产出的技能

| skillId | 名称 | 说明 |
|---|---|---|
| `pptx_deck` | 演示文稿生成（PPT） | 封面 / 目录 / 章节分隔 / 内容 / 两栏 / 表格 / 指标卡 / 大数字 / 网格卡 / 时间轴 / 图标行 / 引言 / 图片 / 图表 / 小结 / 结束页 |

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
| `stats` | 大数字看板（无卡片底，数字更大） | `title` `items[{value,label}]` `cols` |
| `grid` | 网格卡片（2/3 列，左侧色条） | `title` `items[{title,text}]` `cols` |
| `timeline` | 时间轴 / 流程（序号圆 + 连接线，最多 6 步） | `title` `items[{title,detail}]` |
| `iconRows` | 图标行（彩色圆 + 标题 + 说明，最多 6 行） | `title` `items[{icon,title,text}]` |
| `quote` | 引言/金句 | `text` `cite` |
| `image` | 配图 | `title` `path` `caption` |
| `chart` | 图表（柱/折线/饼/环形） | `title` `chartType` `categories[]` `series[{name,values}]` `yLabel` |
| `summary` | 小结 | `title` `bullets[]` |
| `end` | 结束页 | `title` `subtitle` |

`content` 页还可用 `"layout":"timeline|grid|stats|iconRows"` 直接指定子类型（少记几个 type）。
`items` 里的字符串项会被当作第一个字段（允许 `items:["要点一", …]` 这种简写）。

任何一页都可加 `notes`，写入**演讲者备注**。

### 要点页的两种写法

`bullets` 里的一项若写成「小标题：说明」，会自动排成**加粗小标题 + 次级说明**两行，内容页更有层次：

```json
{ "type": "content", "title": "核心能力",
  "bullets": ["协作：多角色同场会商", "记忆：RAG 长期记忆并可治理", "交付：直接产出可下载文件"] }
```

> 设计规范建议**不要每页都用同一种版式**。提纲阶段就为每页选定合适的页型并轮换，
> 同一份演示稿里连续三页都是 `content` 会显得很平。

## 主题与版式风格

### 18 套命名调色板（推荐）

移植自 MiniMax pptx-generator 的 `design-system.md`。每套只给 **5 个色值**，
**角色（主色/底色/强调/浅底/正文）由亮度与彩度自动推出**（见 `DeriveTheme`）——
调色板是“设计语言”、角色映射是“渲染规则”，分开才好在 18 套上一致地成立。

| theme | 适用场景 |
|---|---|
| `modern-wellness` | 医疗 / 健康 / 咨询 / 护肤 / 瑜伽 |
| `business-authority` | 年报 / 财务分析 / 企业介绍 / 政府 |
| `nature-outdoors` | 户外装备 / 环保 / 农业 / 历史文化 |
| `vintage-academic` | 学术讲座 / 历史回顾 / 博物馆 / 老字号 |
| `soft-creative` | 母婴 / 甜品 / 女装 / 幼教 |
| `bohemian` | 婚礼策划 / 家居 / 有机食品 / 慢生活 |
| `vibrant-tech` | 体育赛事 / 健身房 / 创业路演 / 青年教育 |
| `craft-artisan` | 咖啡馆 / 手作 / 传统文化 / 烘焙 |
| `tech-night` | 科技发布 / 天文 / 夜间经济 / 豪华汽车（**深色底**） |
| `education-charts` | 统计报告 / 教育 / 市场分析 / 通用商务 |
| `forest-eco` | 景观设计 / ESG / 双碳 / 植物 |
| `elegant-fashion` | 高级时装 / 画廊 / 美妆 / 杂志风 |
| `art-food` | 美食纪录 / 艺术展 / 民族风 / 复古餐厅 |
| `luxury-mysterious` | 珠宝 / 酒店管理 / 高端咨询 / 心理 |
| `pure-tech-blue` | 云 / AI / 水务海洋 / 医院 / 洁净能源 |
| `coastal-coral` | 旅行 / 夏日活动 / 饮品 / 海洋 |
| `vibrant-orange-mint` | 儿童活动 / 促销海报 / 快消 / 社媒 |
| `platinum-white-gold` | Agent 产品 / 企业官网 / 金融科技 / 奢侈品牌 |

也可以用历史主题名 `business`（默认）/ `tech` / `warm` / `minimal` / `dark` / `vivid`
（**色值原样保留**，老调用方产出不变），或用 `themeColors` 逐项覆盖：
`primary`（标题/主色）、`secondary`（辅色/层级说明）、`accent`（强调色/徽标底）、
`light`（浅底/卡片）、`bg`（页面底色）、`text`（正文色）；另有 `fontTitle` / `fontBody`。

> 深色主题（如 `tech-night`、`dark`）的 `primary` 取**亮色**——它同时用作深色底上的标题色与反色块填充。

### 4 种版式风格（`style`，与主题正交）

同一套内容只换圆角与留白就能变成 4 种气质；它**只影响页边距 / 间距 / 圆角**，
与 `theme` 可自由组合（如 `tech-night` + `pill`）。

| style | 气质 | 圆角 | 页边距 | 适用 |
|---|---|---|---|---|
| `sharp` | 几何、高密度、严谨 | 直角 | 0.65" | 数据报表 / 表格 |
| `soft`（默认） | 适度圆角、舒适留白 | 0.05" | 0.92" | 通用商务 |
| `rounded` | 大圆角、舒展 | 0.15" | 1.10" | 产品介绍 / 市场 |
| `pill` | 胶囊圆角、大留白 | 0.30" | 1.33" | 品牌发布 / 高端 |

### 可读性兜底（18 套都过 WCAG）

调色板是从设计文档搬进来的，色值本身不保证“当正文色看得清”——比如某套的次深色是饱和红、
某套全是浅粉。所以派生后统一过一道兜底：正文 ≥ 4.5:1、主色/副色/强调 ≥ 3.0:1，
并额外把强调色调到“黑或白至少一个能读清”（它是页码徽标/序号圆的底色）。
18 套逐对断言的钉子见单测 `NamedPalette_RendersAndKeepsTextReadable`；
实际生效的色值会回显在返回 JSON 的 `palette` 字段里，便于排障。

> 深色主题（如 `dark`）的 `primary` 取**亮色**——它同时用作深色底上的标题色与反色块填充。

## 版式约定

- **16:9 宽屏**：12192000 × 6858000 EMU（13.333" × 7.5"）。
- 左右安全边距 0.916"，内容宽 10515600 EMU。
- 除封面与结束页外，每页统一：页面底色 → 标题 → 标题下强调线 → 内容 → **右下角页码徽标**。
- 表格用「表头填主色 + 隔行浅色」自绘，不依赖主题部件里的表格样式（兼容性最好）。

## 图表：图片（默认）还是原生可编辑

**默认渲成 PNG**（与内置 docx 技能同口径）：DrawingML 图表的 `ChartPart` schema 复杂，
容易产出旧版 PowerPoint 打不开、或提示「不可读内容」的文件；渲成图片视觉可控、兼容性最好。
代价是图表不可在 PowerPoint 里直接改数据。

**需要“拿回去继续改数据”时**，把 `chartType` 写成 `bar-native` / `line-native` / `pie-native`
（或顶层 `chartData:"native"`），就会产出真正的 `ChartPart`：

- 同时**嵌入一份数据工作簿**（`SpreadsheetDocument` 生成），否则「编辑数据」拿不到表格；
- 支持 `bar` / `line` / `pie`；`doughnut` 等会自动降级为图片，并在返回 JSON 的
  `nativeChartFallback` 里**如实说明**（不静默降级）；
- 返回 JSON 的 `nativeCharts` 报出实际生成了几张原生图表。

> **为什么默认不开**：ChartSpace 的子元素顺序是 schema 强制的（如 `catAx` 必须
> `axId→scaling→delete→axPos…`），顺序错了 PowerPoint 就报“需要修复”，而单部件校验器不一定拦得住。
> 实测已踩到一个：图表调色板常量带 `#`（ImageSharp 接受），但 `srgbClr/@val` 是 `xsd:hexBinary`，
> 带 `#` 就不合法——现在在输出层统一去 `#`（`BareHex`）。
>
> 单测会验证部件存在、关系存在、嵌入工作簿可打开、ChartSpace 缓存值与输入一致、整包过 schema 校验；
> **但仍建议发布前用 PowerPoint 真开一次**——这是本项目唯一无法靠自动化完全覆盖的风险点。

图表渲染失败（如容器缺字体）时会**降级为要点页**列出数据，不让整页失败。

## 读取既有 pptx / 套模板

### 读取（`action: "read"`）

```json
{ "action": "read", "path": "/app/docs/某份.pptx" }
```

按放映顺序返回每页文本：`slideTexts[]`（逐页 `texts[]` + `notes`）与拼好的 `text`。
只取文本，**不还原版式与图片**（返回里也这么写）。用途：「把这份 PPT 改一改 / 总结一下 / 照着它再出一份」。

### 套模板（`template`）

```json
{ "title": "Q3 汇报", "template": "/app/docs/公司模板.pptx",
  "outputPath": "/app/docs/Q3汇报.pptx",
  "slides": [ { "type": "cover", "title": "Q3 汇报" } ] }
```

- **绝不写原件**：先把模板复制到 `outputPath`，再改副本；`template` 与 `outputPath` 相同时直接报错。
- 保留模板的**母版 / 版式**，并从其主题读出**配色与字体**（未显式传 `theme`/`themeColors` 时生效）。
- 默认**清空模板原有页面**（只借它的“皮”）；传 `"keepTemplateSlides": true` 则追加在后面。
- 清空时会**连同幻灯片部件一起删除**：只删 `SlideId` 的话 `ppt/slides/slideN.xml` 还会留在包里
  （占体积、文本仍能被搜到），实测表现为“看着像清空失败”。
- 模板取色不一定合口味，所以取出后仍过一遍可读性守卫（见上）。

### 用户上传的文件怎么传给技能

模型只看得到附件 ID（`att_xxx`），看不到服务器路径，而技能吃的是**路径**。
平台在调用 **dotnet 技能**前会把入参里形如 `att_xxx` 的字符串换成真实路径
（`SkillRunner.ResolveAttachments`，解析器由 `AgentCatalog` 从 `AttachmentStore` 注入）：

```json
{ "action": "read", "path": "att_1a2b3c4d" }
{ "template": "att_1a2b3c4d", "slides": [ … ] }
```

解析不到的 ID 原样保留（技能会报「找不到文件」），解析器抛异常也不会把调用搞挂。

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
7. **图表不越界**（`ChartOverflowTests`，报告落在临时目录 `pptx-chart-bbox.txt`）：200 字标题 +
   30 个 20 字分类 + 4 个长名系列 + 40 项饼图图例的压力输入，从产物里抠出图表 PNG，
   用自带的纯 C# PNG 解码器**逐像素求墨迹包围盒**，断言四周仍留空边。实测修复前柱/折线图
   右侧贴边（`R0`）、饼图墨迹铺到 `(0,0)-(1399,799)`；修复后为 `L20 T25 R35 B51` /
   （饼图）`L40 T23 R40 B26`（画布 1400×800）。
8. **饼图真的是实心圆盘**（`Pptx_PieChart_DrawsFilledDisk`）：断言彩色像素占比 ≥ 20%
   （真圆盘约 29%）。仅靠“没越界”拦不住画坏的扇区（见下方几何坑）。

```bash
# 单测
dotnet test tests/AguiGroupChat.Hub.Tests/AguiGroupChat.Hub.Tests.csproj --filter "FullyQualifiedName~PptxDeckSkillTests|FullyQualifiedName~ChartOverflowTests"
# 技能正文的本地编译自检（技能不在任何 csproj 里，只有运行时才编译）
python tools/check-skill.py tools/pptx-skills/pptx_deck.cs
# 独立结构检查（对照 OOXML 必备部件规则，不依赖 .NET）
python tools/verify_office_package.py 某个.pptx
# 实盘图表几何（真容器 + 真的 Roslyn 编译执行，量像素；见下）
PYTHONIOENCODING=utf-8 python tools/verify_chart_geometry.py
# 实盘设计系统（18 套调色板 / 4 种 style / 新页型：回显配色 + 版面不越界 + 包结构）
PYTHONIOENCODING=utf-8 python tools/verify_design_system_live.py
# 实盘读取/套模板/原生图表（真上传取 att_xxx，再作 path/template 传给技能）
PYTHONIOENCODING=utf-8 python tools/verify_template_live.py
```

> **为什么需要 `check-skill.py`**：技能正文是由 DotnetSkillHost 在**运行时**用 Roslyn 编译的，
> `dotnet build` 看不到它们。改完只能靠跑测试间接发现编译错误，而且报错被包在测试失败里。
> 该脚本用 .NET 10 的 file-based app 在本地先编一遍（`#r "nuget: …"` 转成 `#:package …@…`），
> 几秒就能拿到和运行时一致的 CS 错误。

`tools/verify_chart_geometry.py` 不经模型，直接打 `/ag-ui/skills/{id}/run` 试运行通路，
对产物里的每张图表 PNG 量两件事：**墨迹包围盒四周是否留边**（越界检查）与
**彩色像素占比**（饼图圆盘应约 27%；只查越界拦不住“填成弓形”的坏扇区）。
实盘实测（容器，字体 `Noto Sans CJK SC`）：柱/折线 `L20 T26 R35 B51`、
压力饼图 `L41 T24 R62 B26` 实心度 `28.96%`、4 项饼图 PPT `28.81%` / Word `26.81%`，全部通过。

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
- `SixLabors.ImageSharp.Drawing` 的 `PathBuilder.AddArc` 重载很容易看错：
  `AddArc(PointF center, rx, ry, rotation, startAngle, sweepAngle)` 与
  `AddArc(float x, float y, rx, ry, startAngle, sweepAngle, rotation)` 的**前两个参数都是圆心**，
  不是包围盒左上角；也**没有** `(x, y, w, h, …)` 那种“包围盒 + 宽高”的语义。
  搞错会算出巨大圆形并铺满/冲出画布（实测饼图墨迹铺到 `(0,0)`）。
  **本仓库的饼图已不再用 `AddArc`**，改用 `MoveTo` + `LineTo` 显式围扇形（见下）。
  `EllipsePolygon` 用 `(PointF, float)` 最稳。
- **饼图扇区必须自己围「圆心 → 弧 → 圆心」**：`PathBuilder.AddArc` 只是往当前图形里追加一段弧，
  既不先移到圆心也不补两条半径。只写 `AddArc`（哪怕再加 `CloseFigure`）得到的是「弧 + 弦」
  围成的**弓形**，面积远小于扇形——4 项饼图约 14%、40 项时只剩 0.8% 的彩色像素，
  看着就像整张图没画。正确做法：`MoveTo(圆心)` → 沿弧 `LineTo` 走一圈 → `CloseFigure()`；
  本仓库用每 2° 一段的多边形逼近（弦高远小于 1px，肉眼不可见）。
  回归测试：`Pptx_PieChart_DrawsFilledDisk`。
- **图表要按可用空间自适应，不能信任固定坐标**：长标题/多系列/多分类/大数值都会让文字与图例
  画出画布。本仓库的做法：标题先缩字号再不超宽裁剪；Y 轴刻度文案先量宽再定左边距
  （钳在 72–200px）并**右对齐**到轴线左侧；图例按宽度**换行**（最多 3 行）、每项按宽裁剪、
  放不下的补“…等 N 项”，并将**占用的行数提前计入顶部留白**；分类标签按**槽宽**裁剪、
  过密时**隔位显示**（`stride = ceil(52/slot)`），起点用 `ClampX` 夹在绘图区内。
- **页面正文也会缩**：`BulletBody` 先把要点整理成数据 → 按字数估算高度定缩放（下限 0.6）
  → 再生 XML，避免要点过多时直接溢出页面。
- 平台预置 `using` 不含 `System.IO`，需自行 `using`（本文件已含）。
- 换行统一 `\n`；正文由同步脚本统一处理。
- **页型的 `case` 字面量必须全小写**：`RenderSlide` 先做了 `type.ToLowerInvariant()`，
  写成 `case "twoCol"` 就永远匹配不上，会**静默回落**成默认要点页（实测踩到：两栏页型从来没生效过）。
  回归由单测 `EveryDocumentedSlideType_IsActuallyWired` 钉住（把每种页型与“不存在的页型”产出对比）。
- **`EstimateHeightEmu` 的 `lineSpacing` 单位是「倍率」**（1.25 = 1.25 倍行距），不是百分数。
  实测踩到：这里曾写成 `/100.0`，而调用方一律传倍率 → 估出来的高度只有真实值的 1%，
  `need <= boxH` 永远成立、缩放系数永远是 1.0，**「缩字号」实际上是死代码**。
  改估算公式时请同步核对所有调用方传的单位。
- **图表里的 `SixLabors.Fonts` 必须钉在 `1.0.1`（不要删）**：ImageSharp 2.1.5 对它的依赖是
  `>= 1.0.0`，NuGet 解析器取**最低满足版**，会落到 1.0.0。**而 1.0.0 的 shaping 会对 CJK 字体
  错误地套用竖排（`vert`）字形替换** —— 破折号 `—` `–` 被画成**竖线**，`（）「」『』【】《》`
  被旋转 90°（而正文汉字与 `、。：；！？` 正常，所以看起来像“部分符号方向错了”）。
  实测：**同一份字体用 FreeType/PIL 渲染是横排正确的**，所以不是字体、也不是我们的排版代码
  （图表代码里没有任何旋转）。显式声明 `#r "nuget: SixLabors.Fonts, 1.0.1"` 即修复（仍为 Apache-2.0，
  不受 Six Labors 从 2.0 起的 Split License 影响）。回归由单测
  `ChartSkill_PinsSixLaborsFontsAtLeast101` 钉住。
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

- 图表**默认是图片**，不可在 PowerPoint 内改数据；需要可改数据请用原生图表（`*-native`，见上）。
- **不做任意编辑既有 pptx**：支持的是「读取文本」（`action:read`）与「套模板重出一份」（`template`），
  不是在 XML 层改既有页的版式/内容。
- 不支持动画、切换、SmartArt、母版多版式。
- 没有图标字体/图标素材：`iconRows` 的 `icon` 只能填 1~2 个字（或省略用序号）。
- 没有半出血图 / 图左文右这类图文混排版式：`image` 页是整块图。
- 原生图表只支持 `bar` / `line` / `pie`（scatter / bubble / radar 不支持）。
- `image` 页的图片走 ImageSharp 读取以计算等比尺寸；ImageSharp 不支持的格式（如 svg/emf）会报可读错误。
- 表格列宽均分（不按内容自适应），列多时字号不会自动再缩。
- 自适应用的是**每条内容的宽度估算**（按字号 × 字符数的近似量），不是真实排版度量：
  极端混排（大量全角/半角、超长英文单词）下仍可能留白过多或裁得略早。
- 正文缩字号已覆盖**全部页型**（`content` / `summary` / `toc` / `twoCol` / `table` / `kpi` /
  `stats` / `grid` / `timeline` / `iconRows`）。
- **表格装不下时会减行并明说**：缩到字号下限（约 9pt）仍装不下的行不画出去，
  末行换成「… 另有 N 行未显示」。一张幻灯片本来就装不下「30 行 × 每格折 4 行」这种东西，
  硬画出去只会得到看不见的表；宁可少显示 + 告诉你少了多少。
- 图表图例最多 3 行，超出以“…等 N 项”代替（不是分页）。
- `action:read` 只取文本，不还原版式与图片；页码徽标（如 `02`）也会作为文本被取出，属噪声。
