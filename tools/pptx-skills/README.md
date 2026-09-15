# pptx 演示文稿技能（内置）

PowerPoint（`.pptx`）生成技能。**纯 .NET 实现**（`DocumentFormat.OpenXml` 的 PresentationML），
与平台其它技能同一条执行链路（Roslyn 编译 → 执行），**不依赖 Node / Python / PptxGenJS**。

> 设计参考 [MiniMax-AI/skills · pptx-generator](https://github.com/MiniMax-AI/skills/blob/main/skills/pptx-generator/SKILL.md)
> 的「页面类型体系 + 主题对象 + 设计系统」组织方式；图表处理沿用本仓库内置 docx 技能的既有决策。
>
> 与参考实现的根本差别：它是**提示词层**技能（模型为每页手写一个 JS 文件，版式多样性来自“现场写代码”），
> 而我们是**确定性 JSON 渲染器**（`slides[]` → C# 渲染）。所以参考里的“无限版式”在这里必须**枚举**成
> 有限的页型与 `variant`——好处是产出稳定、可单测、不依赖 Node；代价是多样性有上限。
>
> 已从参考对齐的部分：多版式变体（封面/目录/分隔/小结）、图文混排（图左文右 / 半出血叠字 / 图廊）、
> 散点与雷达图、JSON 内嵌可编辑图表 + 额外数据工作簿、字体配对、颜色可读性守卫、
> 出稿后 QA 自检、结构级编辑既有稿。

## 产出的技能

| skillId | 名称 | 说明 |
|---|---|---|
| `pptx_deck` | 演示文稿生成（PPT） | 封面 / 目录 / 章节分隔 / 内容 / 两栏 / 表格 / 指标卡 / 大数字 / 进度仪表 / 网格卡 / 时间轴 / 图标行 / 引言 / 配图（含图文混排） / 图表 / 小结 / 结束页；支持读取既有稿、套模板、出稿后自检、原地编辑既有稿 |

## 页面类型（slides[].type）与版式变体（variant）

大多数页型可用 `variant` 换版式（设计文档给每个页型都列了多种排法）。**变体是枚举实现的**：
写错的变体名会静默回落成默认版式，所以单测逐个变体比对页面 XML，确保真的长得不一样。

| type | 用途 | variant | 关键字段 |
|---|---|---|---|
| `cover` | 封面 | `left`(默认) / `center` / `image`(背景图+蒙层) / `split`(左文右图) | `title` `subtitle` `author` `date` `path` |
| `toc` | 目录 | `list`(默认) / `grid`(两列卡片) / `sidebar`(侧栏) | `title` `items[]` |
| `section` | 章节分隔 | `number`(默认) / `bar`(左侧色块) / `full`(水印序号) | `title` `subtitle` |
| `content` | 要点页 | 见下方 `layout` | `title` `bullets[]` |
| `twoCol` | 两栏对比 | — | `title` `left{heading,bullets}` `right{…}` |
| `table` | 表格（过长自动分页） | — | `title` `headers[]` `rows[][]` |
| `kpi` | 指标卡（一行最多 4 张） | — | `title` `items[{value,label}]` |
| `stats` | 大数字看板（无卡片底） | — | `title` `items[{value,label}]` `cols` |
| `progress` | 进度 / 仪表 | `bar`(默认) / `ring`(环形) | `title` `items[{label,value}]` `max`(默认 100) |
| `grid` | 网格卡片（2/3 列，左侧色条） | — | `title` `items[{title,text}]` `cols` |
| `timeline` | 时间轴 / 流程（最多 6 步） | — | `title` `items[{title,detail}]` |
| `iconRows` | 图标行（最多 6 行） | — | `title` `items[{icon,title,text}]` |
| `quote` | 引言/金句 | — | `text` `cite` |
| `image` | 配图 / 图文混排 | `full`(默认) / `left`(图左文右) / `right`(文左图右) / `bleed`(半出血叠字) / `gallery`(2~4 张) | `title` `path` `caption` `heading` `bullets[]` `images[{path,caption}]` |
| `chart` | 图表（见下方图表节） | — | `title` `chartType` `categories[]` `series[]` `yLabel` `xLabel` |
| `summary` | 小结 / 收尾 | `list`(默认) / `cta`(行动项) / `split`(左回顾右行动) | `title` `bullets[]` `items[]` `actions[]` `contact` |
| `end` | 结束页 | — | `title` `subtitle` |

`content` 页可用 `"layout":"timeline|grid|stats|iconRows|progress"` 指定子类型（少记几个 type）。
`items` 里的字符串项会被当作第一个字段（允许 `items:["要点一", …]` 这种简写）。

任何一页都可加 `notes`，写入**演讲者备注**。

### 内置图标（`iconRows` 的 `icon`）

填内置图标名会画成**真正的图标**（ImageSharp 画的 PNG，颜色自动跟主题的“圆底色上的字”色）。
共 **45 个**，分两层：

- 基础语义（12）：`check` `cross` `arrow` `star` `dot` `warn` `lock` `user` `chart` `clock` `gear` `bulb`
- 业务语义（33）：`money` `target` `rocket` `shield` `layers` `globe` `network` `cloud` `database`
  `mail` `phone` `calendar` `flag` `search` `edit` `file` `pie` `link` `eye` `heart` `key` `crown`
  `map` `cpu` `package` `award` `briefcase` `users` `code` `gauge` `filter` `refresh` `download`

填其它内容则当作 1~2 个字的短标记；省略则用序号。

> 为什么自己画而不是用图标字体/react-icons：技能是**单个编译单元**，不能携带字体/素材文件，
> 也不能假设宿主装了某个图标字体；ImageSharp 已为图表引入，画几何图形是顺手的事。
> 新增图标用**归一化坐标**（`N(x,y)`，0~1）写，少算错；
> 回归由单测 `AllDocumentedIcons_ProduceNonBlankPngs` 钉住——它逐个量墨迹占比，
> 因为名字写错/漏 case 只会得到一张**全透明 PNG**（圆里空空的），而“生成成功”完全看不出来。

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
`light`（浅底/卡片）、`bg`（页面底色）、`text`（正文色）。

### 字体：命名字体配对 + 东亚字体分开

**拉丁字面与中文字面是两回事**——这是这一块最容易被搞错的地方。

- `fontPair`：命名字体配对（移植自 design-system.md 的 Font Pairings），
  如 `georgia-calibri` / `cambria-calibri` / `trebuchet-calibri` / `arial-black-arial` /
  `impact-arial` / `palatino-garamond` / `consolas-calibri` / `calibri-light` / `yahei`(默认)。
  设计规范明确要求“别一路 Arial 到底”——标题选有性格的字面、正文配干净的字面。
- `fontCjk`：**东亚字体**（写入 `a:ea`）。汉字走它，拉丁字母走 `fontTitle`/`fontBody`。
  不分开写的话，拿 Georgia 去排汉字会整段落到 fallback（甚至缺字）。
- `fontTitle` / `fontBody`：直接指定（会盖掉 `fontPair`）。若给的是中文字体
  （如 `宋体` / `SimSun` / `Noto Sans CJK`），会自动把它当作 `fontCjk`。
  取值顺序是**先看标题字体、命中就不再看正文字体**——否则 `fontTitle:"宋体"`
  会被默认的正文字体（微软雅黑）反手盖掉（实测踩到，回归由 `CjkFont_FollowsExplicitChineseFontFace` 钉住）。

> 服务器（Linux 容器）通常没有 Georgia / Calibri，所以这些只影响写入 PPT 的**字体名**，
> 在装了字体的 PowerPoint/Windows 上才看得到差异；图表（服务端渲成 PNG）仍只用容器里真实存在的字体。

### 标题下不要强调线（默认不画）

设计规范（pitfalls.md）把“标题下一条短线”列为 **AI 生成稿的典型特征**，要求用留白或背景色做层级。
所以**默认不画**；需要旧观感的调用方传 `"titleRule": true` 才加回来。

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
- 除封面与结束页外，每页统一：页面底色 → 标题 → 内容 → **右下角页码徽标**（标题下不画强调线，见上）。
- 表格用「表头填主色 + 隔行浅色」自绘，不依赖主题部件里的表格样式（兼容性最好）。
- **图片按框裁切（cover 语义）**：图片型页与图文混排页的框不一定是原图比例，
  服务端先用 ImageSharp 裁到目标比例再嵌（`AddCoverImage`）——直接拉伸会变形。

## 图表

支持六种：`bar` / `line` / `pie` / `doughnut` / `scatter`（散点）/ `radar`（雷达）。

| 图型 | 数据写法 | 适用 |
|---|---|---|
| `bar` `line` `pie` `doughnut` | `categories[]` + `series[{name,values[]}]` | 分类对比 / 趋势 / 占比 |
| `scatter` | `series[{name,points:[[x,y],…]}]`（也接受 `[{x,y}]`） | 相关性 / 分布（分类轴折线图讲不了） |
| `radar` | `categories[]` 当各维度轴 + `series[{name,values[]}]` | 多维能力对比 |

散点图的数据结构与其它图不同构，所以在“values 为空”校验之前就分流；缺 `points` 时报可读错误
（不是假装出了一张空图），回归由 `ScatterChart_WithoutPoints_FailsReadably` 钉住。

### 图片（默认）还是原生可编辑

**默认渲成 PNG**（与内置 docx 技能同口径）：DrawingML 图表的 `ChartPart` schema 复杂，
容易产出旧版 PowerPoint 打不开、或提示「不可读内容」的文件；渲成图片视觉可控、兼容性最好。
代价是图表不可在 PowerPoint 里直接改数据。

**需要“拿回去继续改数据”时**，把 `chartType` 写成 `bar-native` / `line-native` / `pie-native`
（或顶层 `chartData:"native"`），就会产出真正的 `ChartPart`：

- 同时**嵌入一份数据工作簿**（`SpreadsheetDocument` 生成），否则「编辑数据」拿不到表格；
- 支持 `bar` / `line` / `pie`；`doughnut` / `scatter` / `radar` 会降级为图片，并在返回 JSON 的
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

## 进度 / 仪表页（`progress`）

进度、完成度、占比这类表达，用数字卡片（`kpi`）说不清“已走到哪”。

```json
{ "type": "progress", "title": "项目进度", "items": [
  { "label": "需求确认", "value": 100 }, { "label": "开发", "value": 72 } ] }
```

- `variant: "bar"`（默认）：横向进度条（轨道 + 按比例的前景条 + 右侧百分比）。
- `variant: "ring"`：环形仪表（最多 4 个），环内标注百分比。
- `max` 默认 100；`value` 可写数字或 `"72%"` 这种字符串。

> **环形的画法有坑**：中心必须**透明**。最初的实现是“中心→弧→中心”围成封闭扇形，
> 再用底色填内圆挖空——结果 PNG 中心被填成**不透明底色**，在别的底色页面上就是一个白饼。
> 现在改成用**开放折线描边**画弧（`PolyOpen`），墨迹占比 0.65 → ~0.27。
> 回归由 `ProgressRing_IsHollowRingNotDisk` 钉住（量墨迹占比，上界 0.45 就是为了拦住“实心饼”）。

## 读取 / 自检 / 原地编辑既有 pptx

### 读取（`action: "read"`）

```json
{ "action": "read", "path": "/app/docs/某份.pptx" }
```

按放映顺序返回每页文本：`slideTexts[]`（逐页 `texts[]` + `notes`）与拼好的 `text`。
只取文本，**不还原版式与图片**（返回里也这么写）。用途：「把这份 PPT 改一改 / 总结一下 / 照着它再出一份」。

### 自检（`action: "qa"`）

```json
{ "action": "qa", "path": "/app/docs/某份.pptx" }
```

跑出稿后自检，返回 `issues[]`（`{slide, kind, detail}`）与 `issueCount`。检查四类：

| kind | 含义 |
|---|---|
| `placeholder` | 还留着占位符/未完成标记（`lorem` / `TODO` / `占位` / `待补充` / `xxxx`…） |
| `empty` | 该页除页码外没有任何文字 |
| `emptyBody` | 正文是**自动填充的空状态文案**（如“（本页暂无要点）”）——看着有字，实际没内容 |
| `titleOnly` | 内容页只剩标题、没有正文（**仅当知道页型时才判**，见下） |
| `overflow` | 形状/文字框画到画布外 |

**生成与编辑都会自动跑一遍**（结果在返回的 `qa` 字段），因为“模型声称写完了”与“文件里真有内容”是两件事。

两个“不误报”的细节（都是实测踩出来的）：

- **有图/图表的页不算 `titleOnly`**：图上文字本来抽不出来（散点图/雷达图页曾被误报）。
- **外部文件（`action:qa`）不判 `titleOnly`**：没有页型信息，而封面/结束页天然只有一句话，误报会淹没真问题。

### 原地编辑（`action: "edit"`）

改既有稿的**结构**：删页 / 重排 / 复制页 / 替换文字 / 追加新页。
（以前只能“读文本”与“套模板重出一份”，用户说“把第 5 页删了、第 2 页换到最前面”是做不到的。）

```json
{ "action": "edit", "path": "/app/docs/原稿.pptx", "outputPath": "/app/docs/改后.pptx",
  "ops": [
    { "op": "delete",      "slides": [3, 5] },
    { "op": "reorder",     "order": [1, 3, 2, 4] },
    { "op": "duplicate",   "slide": 2, "count": 2 },
    { "op": "replaceText", "slides": [1, 2], "map": { "旧文案": "新文案" } },
    { "op": "append",      "slides": [ { "type": "content", "title": "…", "bullets": ["…"] } ] }
  ] }
```

语义与安全：

- **绝不改原件**：先把 `path` 复制到 `outputPath` 再在副本上动刀；
  `outputPath` 缺省为 `<原名>_edited.pptx`；与源文件相同时<b>直接报错</b>。
- 页号是 **1 起算的当前顺序**，op 逐个顺序执行（上一个 op 改完的页序就是下一个 op 看到的）。
- `delete` 不能把页删光（至少留一页）；`reorder` 必须是 1..N 的一个完整排列；越界都报可读错误。
- `duplicate` **只支持不带图表的页**：含图表的页需要一并克隆 ChartPart 与它嵌入的工作簿、
  并重写图表内部的 r:id，很容易产出“需要修复”的文件——因此**宁可明确报错**，也不静默破坏。
  图片关系会正常克隆并重写 id；备注页不跟着复制（会报 `warnings`）。
- `append` 用本技能的渲染器画新页，沿用既有稿的版式。

### 套模板（`template`）

```json
{ "title": "Q3 汇报", "template": "/app/docs/公司模板.pptx",
  "outputPath": "/app/docs/Q3汇报.pptx",
  "slides": [ { "type": "cover", "title": "Q3 汇报" } ]
}
```

- **绝不写原件**：先把模板复制到 `outputPath`，再改副本；`template` 与 `outputPath` 相同时直接报错。
- 保留模板的**母版 / 版式**，并从其主题读出**配色与字体**（未显式传 `theme`/`themeColors` 时生效）；
  模板若单独指定了东亚字体，也会一并采用（很多中文模板是 “Arial + 微软雅黑” 这种搭配）。
- 默认**清空模板原有页面**（只借它的“皮”）；传 `"keepTemplateSlides": true` 则追加在后面。
- 清空时会**连同幻灯片部件一起删除**：只删 `SlideId` 的话 `ppt/slides/slideN.xml` 还会留在包里
  （占体积、文本仍能被搜到），实测表现为“看着像清空失败”。
- 模板取色不一定合口味，所以取出后仍过一遍可读性守卫（见上）。
- 保留模板原有页时不做依赖页型的自检（序号与页型对不上，宁可少报也不要错报）。

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

## 版面预览：把产物渲成图（LibreOffice）

我们自己的生成器与自检只能验证**我们写出的 XML**，验不了“别的渲染器看到的是什么”。
把产物丢给 LibreOffice 渲一遍，相当于请了**第二个独立裁判**，能拦住我们拦不住的：

- **文字溢出文本框**：我们按字号×字数估算高度，估错了自己看不出来，渲出来就露馅；
- **字体/字形缺失**：容器里没某个字体时，渲出的图会变成方块/空白；
- **版本兼容性**：包结构有问题时 LibreOffice 直接打不开——比“PowerPoint 提示需要修复”早一步被抳住。

渲染工具不属于运行时依赖，所以**默认不装**（会多 131 个包、镜像大数百 MB）。开启：

```bash
# .env 里设 AGUI_PREVIEW_TOOLS=true，然后重建
AGUI_PREVIEW_TOOLS=true   # 默认 false
docker compose build web && docker compose up -d web
```

用法（本机装了 soffice + pdftoppm 时自动走本机，不依赖容器）：

```bash
# 渲一份“全部页型与全部 variant”的样张——做版式回归时最常用
python tools/preview-pptx.py --sample

# 渲任意产物（可从容器拉：docker cp agui-group-chat-web:/app/docs/x.pptx .）
python tools/preview-pptx.py x.pptx --pages 1-6 --dpi 130

# 自动检查（不靠眼睛）：页数 / 空白页 / 关键词是否真的渲染出来
python tools/preview-pptx.py --sample --check --expect "版式样张,环形仪表,雷达图"
```

`--check` 做三件事（都是“无需人眼”的部分）：

1. **页数**对得上（也能发现整份文件打不开）；
2. **每页都有内容**（不是空白页）——测“墨迹”时要**先取全图出现最多的颜色当底色**再比，
   否则浅彩色底（如 `CAF0F8`）会把整页都当成墨迹，检查就没意义了；
3. **关键词真的渲染出来了**（用 `pdftotext` 抽文本）——这条能拦住字体缺失/内容被裁掉。

> `--margins`（检查内容是否贴边/溢出）**默认不开**：封面、章节页、`bleed`、背景图页
> 本来就是满版设计，会天然贴到四条边；开了之后误报会淹没真问题。
>
> 产物落在 `tools/.preview/<名字>/`（已 gitignore），同时写出 `report.txt` 便于回看。

> 提醒：这套工具的价值一半在**自动检查**，另一半在**给人看图**——
> 生成物是不是好看、版式是不是贴切，仍然得人扫一眼。

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
9. **45 个内置图标都真画出东西**（`AllDocumentedIcons_ProduceNonBlankPngs`）：名字写错/漏 case
   只会得到一张**全透明 PNG**（圆里空空的），而“生成成功”看不出来——所以逐个量墨迹占比。
10. **环形仪表是环不是饼**（`ProgressRing_IsHollowRingNotDisk`）：量墨迹占比（上界 0.45）。
11. **同页型的各变体真的长得不一样**（`VariantsOfSameType_ProduceDifferentPages`）：
   把变体的页面 XML 两两比较（先把页码徽标抹平，否则比的是页号）；
   没实现的变体会**静默回落**到默认版式，光看“标题在不在”拦不住。
12. **字体配对不会把汉字丢给拉丁字体**（`FontPair_SetsLatinFacesAndKeepsEastAsianFont` /
   `CjkFont_FollowsExplicitChineseFontFace`）。
13. **出稿自检**（`Qa_FlagsPlaceholdersAndEmptyBody` / `Qa_PassesOnAHealthyDeck`）。
14. **原地编辑**（`Edit_DeleteReorderDuplicateReplaceAndAppend` 等）：删/重排/复制/替文字/追加，
   并用 `action:read` 把结果读回来逐页核对；另验证“不得覆盖原件”“不得删光”“图表页复制要报错”。

```bash
# 单测
# 注意：PptxDeckSkillTests 是真实编译+运行技能，一次约 6 分钟；调单个用例更快：
dotnet test tests/AguiGroupChat.Hub.Tests/AguiGroupChat.Hub.Tests.csproj --filter "FullyQualifiedName~VariantsOfSameType"
# 技能正文的本地编译自检（技能不在任何 csproj 里，只有运行时才编译）
python tools/check-skill.py tools/pptx-skills/pptx_deck.cs
# 本地「编译+运行」一份技能（不依赖容器，快速迭代渲染效果）
python tools/run-skill.py tools/pptx-skills/pptx_deck.cs --json '{"title":"T","slides":[{"type":"cover"}]}'
# 独立结构检查（对照 OOXML 必备部件规则，不依赖 .NET）
python tools/verify_office_package.py 某个.pptx
# 实盘图表几何（真容器 + 真的 Roslyn 编译执行，量像素；见下）
PYTHONIOENCODING=utf-8 python tools/verify_chart_geometry.py
# 实盘设计系统（18 套调色板 / 4 种 style / 页型：回显配色 + 版面不越界 + 包结构）
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
- **原生图表只支持 `bar` / `line` / `pie`**（`doughnut` / `scatter` / `radar` 会降级为图片并在返回里说明）。
- **不做任意 XML 级编辑**：支持的是「读取文本」（`action:read`）、「自检」（`action:qa`）、
  「结构级编辑」（`action:edit`：删/复制/重排/替文字/追加）与「套模板重出一份」（`template`）；
  仍**不支持**在 XML 层改既有页的版式/颜色/图表数据。
- `action:edit` 的 `duplicate` **不支持含图表的页**（见上，宁可报错也不产出坏文件）。
- 不支持动画、切换、SmartArt、母版多版式。
- 图标只有内置的 45 个几何图形，没有图标字体/外部图标库；名字不在列表里就当 1~2 字的短标记。
- `image` 的 `bleed` 是“右侧半出血 + 左侧叠字”，不支持任意方向的出血/挖空。
- `image` 页的图片走 ImageSharp 读取以计算尺寸与裁剪；Sprite 不支持的格式（如 svg/emf）会报可读错误。
- 表格列宽均分（不按内容自适应），列多时字号不会自动再缩。
- 自适应用的是**每条内容的宽度估算**（按字号 × 字符数的近似量），不是真实排版度量：
  极端混排（大量全角/半角、超长英文单词）下仍可能留白过多或裁得略早。
- 正文缩字号已覆盖**全部页型**（`content` / `summary` / `toc` / `twoCol` / `table` / `kpi` /
  `stats` / `grid` / `timeline` / `iconRows` / `progress`）。
- **表格过长会自动分页**：在渲染前先按“字号下限下一页能放几行”切块，每块出一页 table 页，
  标题带「（n/m）」；**不丢行**。万一某页仍装不下（极端单元格），末行会换成
  「… 另有 N 行未显示」——总之不静默丢数据。
- 图表图例最多 3 行，超出以“…等 N 项”代替（不是分页）。
- `action:read` 只取文本，不还原版式与图片；页码徽标（如 `02`）也会作为文本被取出，属噪声。
- 自检（QA）是**文本/几何级**的：它抳不住“内容写得不对/不切题”这类语义问题，也抳不住
  “文字溢出自己的文本框”（没有真实排版度量）——它只保证没有占位符、没有空页、没有画出画布。
