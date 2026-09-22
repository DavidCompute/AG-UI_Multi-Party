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
| `pptx_deck` | 演示文稿生成（PPT） | 封面 / 目录 / 章节分隔 / 内容 / 两栏 / 表格 / 指标卡 / 大数字 / 进度仪表 / **示意图（金字塔 / 漏斗 / 四象限 / 循环 / 层叠）** / 网格卡 / 时间轴 / 图标行 / 引言 / 配图（含图文混排） / **自动生成的题图** / 图表 / 小结 / 结束页；**入场·强调·退出动画与页间切换**；支持读取既有稿、套模板、出稿后自检、原地编辑既有稿（含给既有稿加动画） |

## 页面类型（slides[].type）与版式变体（variant）

大多数页型可用 `variant` 换版式（设计文档给每个页型都列了多种排法）。**变体是枚举实现的**：
写错的变体名会静默回落成默认版式，所以单测逐个变体比对页面 XML，确保真的长得不一样。

| type | 用途 | variant | 关键字段 |
|---|---|---|---|
| `cover` | 封面 | `left`(默认) / `center` / `image`(背景图+蒙层) / `split`(左文右图) | `title` `subtitle` `author` `date` `path` |
| `toc` | 目录（过长自动分页） | `list`(默认) / `grid`(两列卡片) / `sidebar`(侧栏) | `title` `items[]` |
| `section` | 章节分隔 | `number`(默认) / `bar`(左侧色块) / `full`(水印序号) | `title` `subtitle` |
| `content` | 要点页（过长自动分页） | 见下方 `layout` | `title` `bullets[]` |
| `twoCol` | 两栏对比 | — | `title` `left{heading,bullets}` `right{…}` |
| `table` | 表格（过长自动分页） | — | `title` `headers[]` `rows[][]` |
| `kpi` | 指标卡（一行最多 4 张） | — | `title` `items[{value,label}]` |
| `stats` | 大数字看板（无卡片底） | — | `title` `items[{value,label}]` `cols` |
| `progress` | 进度 / 仪表 | `bar`(默认) / `ring`(环形) | `title` `items[{label,value}]` `max`(默认 100) |
| `pyramid` | 金字塔 / 分层（3~6 层，顶层最窄） | — | `title` `items[{title,text}]` |
| `funnel` | 漏斗 / 收敛（3~6 层，顶层最宽） | — | `title` `items[{title,text}]` |
| `matrix` | 四象限 | — | `title` `xTitle` `yTitle` `xLeft` `xRight` `items[左上,右上,左下,右下]` |
| `cycle` | 环形闭环（3~6 步） | — | `title` `center` `items[{title,text}]` |
| `stack` | 层叠架构（纵向分层条） | — | `title` `items[{title,text}]` |
| `hero` | 自动生成的题图（整页） | — | `title` `subtitle` |
| `grid` | 网格卡片（2/3 列，左侧色条） | — | `title` `items[{title,text}]` `cols` |
| `timeline` | 时间轴 / 流程（最多 6 步） | — | `title` `items[{title,detail}]` |
| `iconRows` | 图标行（最多 6 行） | — | `title` `items[{icon,title,text}]` |
| `quote` | 引言/金句 | — | `text` `cite` |
| `image` | 配图 / 图文混排 | `full`(默认) / `left`(图左文右) / `right`(文左图右) / `bleed`(半出血叠字) / `gallery`(2~4 张) | `title` `path` `caption` `heading` `bullets[]` `images[{path,caption}]` |
| `chart` | 图表（见下方图表节） | — | `title` `chartType` `categories[]` `series[]` `yLabel` `xLabel` |
| `summary` | 小结 / 收尾（`list` 过长自动分页） | `list`(默认) / `cta`(行动项) / `split`(左回顾右行动) | `title` `bullets[]` `items[]` `actions[]` `contact` |
| `end` | 结束页 | — | `title` `subtitle` |

`content` 页可用 `"layout":"timeline|grid|stats|iconRows|progress|pyramid|funnel|matrix|cycle|stack"` 指定子类型（少记几个 type）。
`items` 里的字符串项会被当作第一个字段（允许 `items:["要点一", …]` 这种简写）。

任何一页都可加 `notes`，写入**演讲者备注**。

> **不必为“怕溢出”而把要点拆得很碎**：每页的标题与正文都按真实字形量高自动缩字号，
> 要点页/表格装不下会**自动分页**（不丢内容），卡片类装不下会**截断并在 `warnings` 里说明**。
> 详见下方「文字溢出」一节。

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

也可以用历史主题名 `tech` / `warm` / `minimal` / `dark` / `vivid` / `business`
（**色值原样保留**，老调用方产出不变），或用 `themeColors` 逐项覆盖：
`primary`（标题/主色）、`secondary`（辅色/层级说明）、`accent`（强调色/徽标底）、
`light`（浅底/卡片）、`bg`（页面底色）、`text`（正文色）。

> **不写 `theme` 的默认是 `business-authority`**（不是历史主题 `business`）——两者的观感差异见下节。

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

### 可读性兜底与配色规则（18 套都过 WCAG）

调色板是从设计文档搬进来的，色值本身不保证“当正文色看得清”——比如某套的次深色是饱和红、
某套全是浅粉。所以派生后统一过一道兜底，并在 2026-09 做了一轮**观感优化**（下面五条就是那次定下来的）：

1. **调色全在 HSL 空间做**（只改亮度、保色相与饱和；目标饱和从“感知彩度”max−min/max 推）。
   以前是往黑/白里混（sRGB 线性混色），会把鲜艳色洗成土色：实测 `vibrant-orange-mint` 的
   FF9F1C（鲜明橙）→ CC7F16（脏芥末）、`modern-wellness` 的 E29578 → B57760（灰褐）。
2. **底线（同时成立，不是先放宽再补）**：正文 ≥ **7:1**、主色 ≥ **7:1**（标题发灰是观感差的主因），
   副色/强调 vs 底色 ≥ 3:1，徽标字 vs 强调 ≥ 4.5:1，强调与主色 ≥ 2.2:1（或色相拉开 40° 时 ≥ 1.5:1）。
3. **强调色选“最鲜艳且分得开”的**，而不是“第一个能修合格的”。实测 `vintage-academic`：
   最深的 780000 稍改一下就合格（得到与深蓝主色几乎一样暗的 990000），而不艳一点的 C1121F 明显更好看——
   所以把所有能修合格的候选都评一遍，按“艳度 + 与主色对比 + 色相拉开加成”取优。
4. **卡片/面板底（`light`）必须是“面”**：彩度 ≤0.30 且与底色能看出层次（≥1.06），
   一个候选都不合格就按主色合成一层淡调（5~14% 饱和）。以前直接取“调色板次亮色”，
   而它常常是个中间调饱和色（`education-charts` 的橙 F4A261、`art-food` 的琥珀 E09F3E），
   铺满卡片后整份稿子一片色块，观感很廉价。
5. **未写 `theme` 时的默认** = `business-authority`（以前是历史主题 `business`：白底+金强调，
   而金色在白底上对比度只有 2.41，强调线/徽标本来就弱）。显式写 `theme:"business"` 仍是原来那套。

18 套逐对断言的钉子见单测 `NamedPalette_RendersAndKeepsTextReadable`（含上述全部底线 + “卡片面必须是面”
+ “主色不能发灰” + “强调色得是点色”）；实际生效的色值回显在返回 JSON 的 `palette` 字段里，便于排障。

> 深色主题（如 `dark`）的 `primary` 取**亮色**——它同时用作深色底上的标题色与反色块填充。

## 版式约定

- **16:9 宽屏**：12192000 × 6858000 EMU（13.333" × 7.5"）。
- 左右安全边距 0.916"，内容宽 10515600 EMU。
- 除封面与结束页外，每页统一：页面底色 → 标题 → 内容 → **右下角页码徽标**（标题下不画强调线，见上）。
- 表格用「表头填主色 + 隔行浅色」自绘，不依赖主题部件里的表格样式（兼容性最好）。
- **图片按框裁切（cover 语义）**：图片型页与图文混排页的框不一定是原图比例，
  服务端先用 ImageSharp 裁到目标比例再嵌（`AddCoverImage`）——直接拉伸会变形。

## 插图：不需要用户提供任何素材

两类“插图”都是**自己生成的**，不依赖任何外部图片或联网：

### 1）示意图（关系图）

商务稿里最常被叫做“插图”的其实是这个：用形状把**关系**画出来。全部用 DrawingML
预设几何拼（`trapezoid` / `rect` / `ellipse` / `triangle` / `parallelogram`），不写自定义几何，
因此产出确定、在任何渲染器里都能开。

| 页型 | 画什么 | 适合 |
|---|---|---|
| `pyramid` | 梯形堆叠，顶层最窄（斜边**连续**：本层下边刚好接下一层上边） | 分层策略 / 成熟度模型 / 价值层级 |
| `funnel` | 梯形堆叠，顶层最宽 + 右侧数值列 | 转化率 / 逐步筛选 |
| `matrix` | 2×2 卡片 + 轴名与轴端标签 | 优先级 / 取舍 / 分类 |
| `cycle` | 环 + 均匀分布的节点圆 + **切向旋转的三角箭头**（中心可写一句话） | 迭代 / PDCA / 闭环 |
| `stack` | 纵向分层长条，层名在左、要点在右 | 技术架构 / 能力分层 |

### 2）程序化题图（抽象视觉）

`hero` 页型，以及 **`image` / 封面在图片缺失或未提供时**的自动降级：
按主题配色生成一张抽象图（斜带 + 大圆 + 点阵）。

- **零素材、零联网、无版权问题**，颜色跟着 `theme` 走；
- **确定性**：用标题作种子（不是 `Random`），所以同一份稿子重导出封面不会变；
  （回归：`GeneratedHeroArt_IsDeterministic`）
- 设计约束：只用实色（无渐变）、透明度只用 `a:alpha`、颜色全部取自动调色板；
- **不假称有图**：页面上会写“（图片不存在，已自动生成题图）”，返回 `warnings` 也如实说。

> 局限（实话）：这是**图形**不是**照片**。要有照片级画面必须接图库或文生图 —— 图库那一路见下一节。

### 两个实测踩到的坑

- **题图最初用“旋转矩形”做斜带，形状画到了画布外**：旋转后的包围盒会超出给定矩形，
  自检直接报 `overflow`（x=-3230879…）。现在斜带改用 `parallelogram`（自带斜边、不靠旋转），
  圆与点阵也全部限位在框内。回归：`DiagramPages_MaxItems_StayInsideCanvas`。
- **`cycle` 一度写成 `Math.Max(3, items.Count)`**：只给 1~2 项时 `n` 被抬到 3，
  但 `items` 里没那么多 → `rows[i]` 越界崩溃。被“每个页型都真的接通了”那个用例抳到（它只给 1 项）。

## 真照片：`imageQuery` → 团队图库 / Wikimedia Commons

上面两类都是“自己画的”。要**照片级**画面（“每页配一张符合内容的图”通常指这个），得用 `imageQuery`：

**取图顺序**（三道，前一道拿不到才走下一道）：

| 顺序 | 来源 | 命中时 | 拿不到时 |
|---|---|---|---|
| ① | **团队图库**（用户上传的图，平台按语义检索） | 用**本地文件**直接嵌入，**不需要署名** | 继续走 ② |
| ② | Wikimedia Commons（CC 素材） | 嵌入照片，并在稿末附「图片来源」页 | 走 ③ |
| ③ | 程序化题图 | 按主题配色生成抽象图，并在 `warnings` 说明 | — |

> `minScore` **显式传 0.6**（不传时平台按 0.25 兜底，实测无意义词能到 0.44~0.55 —— 等于不筛）。
> **图库还可以有自己的“检索严格度”**（图库管理里的 `⚙️`，三档 0.5 / 0.6 / 0.72）：库设了就以库为准 ——
> 描述是短人名 / 标签的库可以收紧避免配错，长句型描述的库可以放松让标题式查询配上。

**`imageSource` 控制走哪条路**（入参优先，其次环境变量 `AGUI_IMAGE_SOURCE`）：
`auto`（默认）/ `library`（**仅图库，彻底不出网** —— 内网部署设这个）/ `network`（仅网络）。

### 二次尝试：模型看不到图库里有什么

图库的描述常常就是**人名 / 产品名**（“刘佳俊”“黄敏谊”），而模型看不到图库内容，只会按这一页的主题
写 `imageQuery`（“员工 颁奖 舞台”）。实测（公司人员生活照库 15 张，bge-m3）：

| 检索词 | 实测结果 |
|---|---|
| `刘佳俊` | `1刘佳俊.png` **0.8808** ✅ |
| `员工 颁奖 舞台` | 命中一张**不相干的一家三口宴会合影**（那张描述里也有“舞台/宴会厅”，**0.68**）|

于是配图多了两步（都在技能里的 `ImagePathOf`）：

1. **分数择优**：图库命中低于 `LibraryConfidentScore`（**0.78**）时，再用**本页文字切出的短片段**逐个查图库，**谁分高用谁**。
   上例里 0.68 的那张输给 0.88 的本人照。门槛不贴着“能用”的底线 0.6，因为那是“能不能用”，
   而 0.6~0.78 之间实测有“蹭词命中”（描述里恰好有同一个词）。
2. **本页文字兜底**：模型关键词完全没命中，或只配到 **Wikimedia 的通用网图** 时，同样用本页文字（切短）再查一次图库
   —— **自有素材优先于通用网图**。页面上写着“高效习惯优秀进步奖 · 刘佳俊”，这个名字就是最好的检索词。

- **本页文字** = 本页的 `title` / `subtitle` / 正文文字，**截断 120 字**。
- **本页文字会先切成短片段逐个试，而不是整段丢过去**（实测对比见下）：

  | 查询 | 命中 | 分数 |
  |---|---|---|
  | `刘佳俊`（3 字） | `1刘佳俊.png` | **0.76** ✅ |
  | 含该名字的 47 字整段 | `1刘佳俊.png` | **0.5862** ❌ 低于 0.60 的可用线 |
  | `AI项目支持团队`（8 字） | `AI项目团队.png` | **0.80** ✅ |
  | 含该名字的 72 字整段 | `AI项目团队.png` | 0.6281（贴线） |

  为什么会这样：检索会被**查询词项数摊薄**（向量把一堆概念平均掉，BM25 也按词项数摊分），
  于是“图库里明明有这张照片、却被门槛挡在门外”，接着降级成网图/题图——
  用户看到的就是**“配图不正确”**（实测就是这么被发现的）。
  切法：按标题分隔符（·｜—）与常见标点切短片段（2~20 字，最多 5 个，**短片段优先**），
  整段作为**最后一个兜底候选**保留（所以不会比切之前更差）；某个片段达到 `LibraryConfidentScore`（0.78）
  就提前收手，不去抢后面的时间预算（预算是全稿共享的 35 秒）。
- 配上图就**不报**警告；两次都没配上才把两条原因（关键词 / 试过的几个片段）都报出来。

### 图库那一路是怎么接上的（安全设计）

技能是运行时编译的独立程序集，拿不到平台 DI，所以走**回环 HTTP**：

1. 平台在调用文档技能前，按**触发者本人可读的图库**登记一个「检索范围句柄」，并把 `imageScopeId` 注入技能入参；
   **岗位绑定了图库就只用绑定的那几个**（例如对外宣讲岗只准用已审核的品牌图库）；若绑定的库全都不存在了，
   则**不注入**（技能按“无图库”降级），而**不是**静默放宽成该用户全部可读的图库；
2. 技能带句柄调 `POST /ag-ui/images/search`（进程内自令牌 `AGUI_SELF_TOKEN`，启动时注入），拿回 `path`（**服务器本地路径**）；
3. 技能直接用这个路径嵌入。

注入覆盖的入口：聊天触发（单聊 / 知聚 / 编排）、技能库「试运行」、桌面宿主直跑 —— 三条都注入，
避免“聊天里能配图、界面上手动试运行却一张图都没有”这种入口差异。

两个刻意的设计：

- **技能只带句柄、不自报图库 ID**。技能入参是模型生成的，天生不可信 —— 若能自报 ID，
  任何用户都能让模型写个别人的图库 ID 把别人的图读出来。句柄由平台登记，越不了权。
- **服务器路径只给自令牌**。登录用户（如前端调试）调同一端点只拿到元数据与 `/raw` 下载地址。

### 怎么用

在**任何有图的位置**写 `imageQuery`（关键词，**英文效果最好**）：

```json
{ "type": "cover",   "variant": "split", "title": "…", "imageQuery": "team collaboration office" }
{ "type": "section", "variant": "full",  "title": "一、现状", "imageQuery": "city skyline sunrise" }
{ "type": "content", "title": "协作方式", "bullets": ["…"], "imageQuery": "team collaboration office" }
{ "type": "image",   "title": "…", "variant": "gallery", "images": [ { "imageQuery": "…", "caption": "…" } ] }
```

- 支持位置：`cover`(image/split) / `section`(full) / `image`（全版式，含 `gallery` 逐张）/ `content`。
- **`content` 带图自动变“文左图右”**，所以模型不必为了配图改用别的页型；
  这一页的**拆页也按半页宽算**（见 `ExpandOverflowSlide`），否则拆出来的页在半栏里依旧放不下。
- `path` 与 `imageQuery` **二选一，`path` 优先**（本地文件存在才用）。
- 取不到图 → 降级为题图 + `warnings`，**不会让整份稿子失败**（关键词与“本页文字”两次都试过才这么说）。

### 署名是硬要求（不是可选项）

Commons 的素材都是自由许可（CC0 / CC BY / CC BY-SA / PD），但 **CC BY / CC BY-SA 要求署名**。
所以：**只要用了检索来的照片，就在稿末自动追加一页「图片来源」**（`credits` 页型），列出
「文件名 — 作者 — 许可 — 原页面链接」；同时回显在返回 JSON 的 `images[]` 里。
这一页不由模型控制（否则会被“省略”掉），生成后**不要删**。

- 署名页**走与要点页同一套分页**：12 张 → 2 页、40 张 → 5 页，**一条不漏**（漏掉就等于没署名）。
- 作者字段带 HTML（Commons 常见 `<a href=…>`）→ 统一剥标签后再写入。
- 守一道门：许可里出现 `non-free` / `fair use` 的直接跳过，宁可不配图也不惹授权风险。

### 关键词写法直接决定成图好坏（实测，不是猜的）

Commons 是**档案库**而不是商业图库，检索质量几乎全看关键词：

| 写法 | 实测结果 |
|---|---|
| `modern office meeting room`（具体名词，3~4 个词） | 头一条就是 **Unsplash 导入的 CC0 会议室照片**（6594×4401） |
| `glass office building` | 四条全是真正的现代玻璃幕墙楼（ar 1.16~1.5） |
| `handshake business` | 头一条是 Rawpixel 的 CC0 商务握手照（6000×4015） |
| `teamwork`（抽象词） | 头两条是 `Teamwork-icon.jpg` 与 `Teamwork.com-Logo-200.png` |
| `business people working together`（抽象动词） | 头一条是 **1920 年书里的插图**（书名就叫 The hang together boys） |
| `office desk laptop notebook business`（5 个词） | **零结果**——词太多会被 AND 掉 |

所以：**用 2~4 个能看得见的具体名词**；要“现代商务感”就带 `modern` / `interior` / `exterior`；
图廊里每一张给**不同**关键词。这条已经写进了技能的 description，模型会照做。

### 筛选与降级（都是实测定下来的）

关键词拦不住所有坏命中，所以代码侧还有一道兵庖：

| 情形 | 处理 | 为什么 |
|---|---|---|
| 不是 JPEG/PNG | 跳过，换下一张 | 按 Commons 给的相关性顺序逐个试 |
| 宽 < 1200px | 跳过 | 实测 800px 宽的图铺满一页能看出虚 |
| 长宽比超出 `0.55~2.2` | 跳过 | 定框裁切（cover）下，9000×3600 的全景只剩中间一条 |
| 标题含 `logo`/`icon`/`wordmark`/`flag of`/`signature` | 跳过 | 抽象关键词下这类会排到前面 |
| 分类含 `Internet Archive Book Images` / `Book scans` / `Scanned books` | 跳过 | 书刊扫描件在 Commons 上量极大且排在前面 |
| 分类含 ≤1999 的年份（如 `1968 in Washington, D.C.`） | 跳过 | 历史档案照放进商务稿很出戏（实测：“meeting room” 抳到过 1968 年白宫会议新闻照） |
| 许可含 `non-free` / `fair use` | 跳过 | 宁可不配图也不惹授权风险 |
| 单张 > 12MB / 内容为空 | 跳过 | |
| HTTP 429 / 503 | **退避 2 秒重试一次** | Commons 会限流（探测真实 API 时实测触发过） |
| 连接失败 / 超时 | 熔断：本次生成不再尝试后续关键词 | 否则十页的稿子会堆十次超时 |
| 取图总耗时超过 **35 秒** | 停止检索，其余页降级为题图 + Warn | 技能执行有硬预算（内置技能 60 秒），超时的后果是**整份稿子都没有**——比降级差得多 |

单次请求超时也调小了（检索 8 秒、下载 12 秒），因为每多一次超时就吃掉一份渲染时间。

### 技能执行预算：为什么内置技能是 60 秒而不是 10 秒

`DotnetSkillHost` 原本对所有技能都是 **10 秒**硬预算。这个值是按“纯 CPU 技能”定的，
而联网取图天然是秒级网络操作：**实测 3 张照片就撞上限**，返回「.NET 技能执行超时」，
**整份稿子都没了**。现在：

| 技能 | 预算 | 配置 |
|---|---|---|
| 用户自建 .NET 技能 | 10 秒（不变） | `Agents:DotnetSkillTimeoutMs` |
| 内置文档类技能（`BuiltinVersion` 非空） | **60 秒** | `Agents:BuiltinSkillTimeoutMs` |

回归：`SkillTimeoutBudgetTests`（用真的会 sleep 的技能验，不依赖网络）。技能内部又自己封了
35 秒的取图上限，把剩余时间留给排版与落盘。

### 能到什么程度（实话）

- **具体、可见的主体效果好**：`modern office meeting room` → 直接拿到 Unsplash 导入的 CC0 会议室照片；
  `city skyline sunset` → 里约热内卢日落天际线；`glass office building` → 四条全是现代幕墙楼。
- **抽象、人物、情绪类概念不行**：Commons 是档案库，`teamwork` 会给你 Logo，
  “开会”可能给你一场 1968 年的内阁会议。上面的筛选能拦掉大部分，但**拦不住全部**。
- 想要可控就用 `path` 指向自己的图；想要“现代商业图库质感”本质上得接商业图库（文生图同理）。
| 同一关键词多处使用 | 只查一次（进程内缓存） | 前后拆页、预解析都不会重复联网 |

筛选链有专门的回归：桩数据把 5 张“不该选”的排在**相关性前面**（index 1~5），
断言只会选中最后那张正常照片——·道筛选漏了用例就红（已反向验证过：临时关掉
「书刊扫描件」这条，用例失败）。

照片一律**重新编码再嵌入**：JPEG 保持 JPEG（质量 85）——PNG 是无损的，一张 1600px 的照片
存成 PNG 会胀到十几 MB，而这份 pptx 是要给用户下载的；截图类（PNG 源）仍存 PNG 以保住文字边缘。
嵌入时按**魔数**判类型（`AddImage`）：JPEG 被标成 PNG 的话 PowerPoint 会报“图片不可读”。

### 部署注意：Wikimedia 在部分网络不可达

实测本环境的容器能访问 `example.com`（200）与 `api.nuget.org`（302），但
`en.wikipedia.org` 与 `upload.wikimedia.org` 都是 `000`（被拦）。这类网络下这个功能会**全部降级**。
两个补救口子：

1. 给服务配代理 —— `HttpClient` 会读 `HTTPS_PROXY` / `HTTP_PROXY` / `NO_PROXY` 环境变量；
2. 把端点指到镜像/自建代理 —— 入参 `imageSearchApi`，或环境变量 `AGUI_PHOTO_API`（入参优先）。

测试不能依赖真外网（会和上面一样时好时坏），所以回归全部跑**本地桩图库**
（`PptxDeckSkillTests.StubPhotoLibrary`，桩数据里故意混了“太小的图”和“非自由许可”两条，用来验证筛选真生效）。

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
| `textOverflow` | **文字装不进自己的文本框**（按真实字形量高；卡片 / 示意图层 / 叠字最容易出这种问题） |

**生成与编辑都会自动跑一遍**（结果在返回的 `qa` 字段），因为“模型声称写完了”与“文件里真有内容”是两件事。

三个“不误报”的细节（都是实测踩出来的）：

- **有图/图表的页不算 `titleOnly`**：图上文字本来抽不出来（散点图/雷达图页曾被误报）。
- **外部文件（`action:qa`）不判 `titleOnly` 也不判 `textOverflow`**：没有页型信息，而且外部文件的
  字体/行距/是否 autofit 都不归我们管，拿我们的排版规则去判只会误报。
- **`textOverflow` 留 2%（或 2pt）余量**：四舍五入与渲染器的细微差别不该报成问题。
- `replaceText` 把文字改长后会单独复核一次，放不进就进 `warnings`（提文字本就不重排版，但不静默）。

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
    { "op": "append",      "slides": [ { "type": "content", "title": "…", "bullets": ["…"] } ] },
    { "op": "animate",     "slides": [1], "animate":     { "preset": "flyIn", "direction": "bottom" } },
    { "op": "transition",  "slides": [2, 3], "transition": { "preset": "push" } }
  ] }
```

语义与安全：

- **绠不改原件**：先把 `path` 复制到 `outputPath` 再在副本上动刀；
  `outputPath` 缺省为 `<原名>_edited.pptx`；与源文件相同时<b>直接报错</b>。
- 页号是 **1 起算的当前顺序**，op 逐个顺序执行（上一个 op 改完的页序就是下一个 op 看到的）。
- `delete` 不能把页删光（至少留一页）；`reorder` 必须是 1..N 的一个完整排列；越界都报可读错误。
- `duplicate` **只支持不带图表的页**：含图表的页需要一并克隆 ChartPart 与它嵌入的工作簿、
  并重写图表内部的 r:id，很容易产出“需要修复”的文件——因此**宁可明确报错**，也不静默破坏。
  图片关系会正常克隆并重写 id；备注页不跟着复制（会报 `warnings`）。
- `append` 用本技能的渲染器画新页，沿用既有稿的版式。
- `animate` / `transition` 给既有稿加动画与翻页效果（见下节）；
  **可重复执行**：每次都先删掉那页旧的 `p:timing`，不会越加越多。
  写 `transition` 时要当心一个坑（已修）：只改切换时不能动已有的时间线，
  否则“先 animate 再加 transition”会把刚加好的动画抹掉。

## 动画与翻页切换

早期版本属于「已知边界」，现已支持。两类东西：

- **页间切换**（`transition`）：整页怎么切到下一页；
- **页内动画**（`animate`）：页里的形状/文字怎么出现、强调、退出。

顶层写就是**全稿默认**，页级写则覆盖它（页级写 `false` 关掉该页）：

```json
{
  "transition": { "preset": "fade", "duration": 0.4 },
  "slides": [
    { "type": "cover", "title": "年度颁奖典礼", "subtitle": "荣耀时刻",
      "animate": { "preset": "flyIn", "direction": "bottom", "duration": 0.75 } },
    { "type": "content", "title": "议程", "bullets": ["一", "二", "三"],
      "animate": { "preset": "fade", "byParagraph": true } },
    { "type": "end", "title": "谢谢",
      "transition": { "preset": "push", "direction": "left" },
      "animate": "fadeOut" }
  ]
}
```

### 预设（都是枚举好的，不做任意时间线）

| 类 | 预设 | 说明 |
|---|---|---|
| 出现 | `appear` `fade` `flyIn` `wipe` `dissolve` | `flyIn`/`wipe` 可给 `direction`（bottom/top/left/right） |
| 退出 | `disappear` `fadeOut` `flyOut` `wipeOut` `dissolveOut` | 与入场一一对应 |
| 强调 | `spin`（旋转一周）`pulse`（放大到 120% 回弹）`fillColor`（改填充色，可给 `color`） | 不改动稿子的最终观感 |

字段：`preset` `direction` `byParagraph`（要点逐条出现）`start`(with/after) `delay` `duration`(秒)
`target`(text 默认｜all 含图片装饰｜media) `only`(只动第几个形状，1 起算) `color`。
`animate` 可写字符串简写（`"fade"`）、对象、或**数组**（一页多个效果，各占一次点击）。

切换的 `preset`：`fade cut dissolve newsflash wedge random push wipe cover pull zoom split
blinds checker circle comb diamond plus randomBar strips wheel`；
另有 `direction` / `orientation` / `spokes` / `speed`(fast|med|slow) / `duration`(秒，映射到三档)
/ `advanceAfter`(秒，到点自动翻页) / `advanceOn`(click|after)。

### 结构不是凭记忆写的

动画 XML（AnimationML）没有“大致对”这回事：写错就被 PowerPoint 判「需要修复」。
所以结构逐项对齐**真实 PowerPoint 产物**——样本取自 LibreOffice 回归库 `sd/qa/unit/data/pptx/*.pptx`：

| 样本 | 定下来的东西 |
|---|---|
| `tdf124457` | Fly In = `p:anim` + `tavLst` 位移（`1+#ppt_h/2` → `#ppt_y`），**不是** `animEffect` |
| `connector-shape-animations` | Wipe = `animEffect filter="wipe(up)"`，`presetID=22` / `sub=1` |
| `tdf107608` | 退出 = `animEffect transition="out"` + 紧随的 `set hidden`（`delay=dur-1`） |
| `tdf112280` / `tdf112333` | 强调 = `animRot by=21600000` / `animClr` + 两个 `set` |
| `tdf168755` | 表格·图表·SmartArt 这类图形框用 `bldGraphic` + `bldAsOne` |

骨架：`tmRoot(1)` → `mainSeq(2)` → 每个请求一个「点击组」→ 组内每个目标/段一个效果节点
（`clickEffect` / `withEffect` / `afterEffect`）。元素次序遵循 `CT_Slide`：
`cSld → clrMapOvr → transition → timing → extLst`。

### 三层校验（都是自动跑的）

1. `OpenXmlValidator` 过 schema（单测）；
2. **悬挂引用检查**：每个 `spTgt/@spid` 必须在当页真实存在（`qa` 里报 `animTarget`）——这是最容易让 PowerPoint 要求修复的点；
3. LibreOffice 实际打开：本仓库实测过“生成 → `soffice --convert-to pdf` 正常出图”与
   “`--convert-to pptx` 回写后动画**被理解并保留**”（含逐段的 5 个节点）。

### 做不到的部分（据实说）

- **没有 morph（变形）与 3D**，也没有 p14/p15 的掠夺型效果：它们要 `mc:AlternateContent` + 扩展命名空间，
  风险与收益不成比例。
- 切换的「任意毫秒时长」不存在于经典 schema：只接受 `speed` 三档，给秒数就映射到最近的一档。
- `p:push/@dir` 等方向用的是 **OOXML 原义**（推移方向），与 PowerPoint 界面文案“从左侧”可能相差一个方向，
  觉得反向就换一个值；`wipe` 的 filter 方向语义已按真实文件定下（从下方进入 = `wipe(up)`）。
- `byParagraph` 用“**每段一个效果节点 + `bldP build="p"`**”编码（与 PowerPoint 自身输出的形状一致，
  默认**不开**）。自动化能证明“文件是好的、能打开”，但**逐段播放的观感需在 PowerPoint 里看一眼**
  （LibreOffice 导 PDF 会丢掉动画层）。
- 页码徽标标了 `name="PageBadge"` 并在排动画目标时跳过：它是装饰，动起来只是噪声。

### 兼容承诺：不写就不加

没请求动画时，本技能的输出与从前**逐字节一致**（注入只发生在 `WithAnimation` 里），
所以既有稿子、既有单测（逐变体比 XML）都不受影响。返回 JSON 里新增的 `animations` 字段
（`pages` / `effects` / `transitions` / `presets`）会如实报出实际用量——0 就是没加。
未知的预设名/切换名一律**报错**，不静默回落成“没有动画”。

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

### 图片嵌入：先瘦身再嵌，别把稿子顶到附件上限

图库里的图是**原图**：一张 12MP 手机照 3~12MB，四张就能把 .pptx 顶到 21~31MB，而平台对附件有大小上限——
超了的产物**挂不到对话里**，用户看到的是“回复说文件生成了、却没有下载入口”（实测踩到 21.3MB / 31.2MB 两份）。
所以嵌入前统一瘦身：

- 字节 ≤ **1.2MB** 的图原样嵌（不重编，避免丢像素与无谓耗时）；
- 否则最长边压到 **1920px**（已超过 16:9 满屏所需），照片重编为 **JPEG q85**；
- **只有真的存在透明像素**（逐像素查 A&lt;255）才保 PNG —— 图库里很多是“截图/照片型 PNG”，
  按容器格式一律保 PNG 的话 1920px 仍有 3MB，等于没瘦（实测踩到 3.7MB 的 PNG 未被压小）；
- 重编反而更大就用原图；解码失败不阻断出稿。同一张图会嵌到多页，结果按指纹缓存。

回归：`PptxImageSlimTests`（4000×3000 噪点大图 → 媒体件像素 ≤1920 且比原图小得多；小图原样嵌）。

### 图库检索的门槛（minScore 0.6）

平台默认 `minScore` 0.25 太松。实测（公司生活照库 18 张，bge-m3）：

| 查询 | 最高分 | 性质 |
|---|---|---|
| `qzxv-不存在-9987` | 0.4412 | 无匹配 |
| `一只在雪地里的猫` | 0.5475 | 无匹配（库里没猫） |
| `财务报表趋势图` | 0.4776 | 无匹配 |
| `年度颁奖典礼 舞台 合影` | 0.6168 | 真实命中 |
| `城市天际线 黄昏 楼群剪影` | 0.7773 | 真实命中 |
| `城市 日落` | 0.8411 | 真实命中 |
| `团队协作 会议` | 0.8225 | 真实命中 |

所以本技能调图库检索时显式传 `minScore: 0.6`（`LibraryMinScore`）：不然 0.25 会把不相干的人物照
当作“命中”嵌进幻灯片——比降级成题图差得多。阈值以下**不静默**：走降级路径并报 warnings（关键词写出来，用户能自己改词重试）。

> 词面命中（BM25）那条路**不受这个阈值管**：它本来就是“向量相似度低于阈值但词面命中”的兜底，
> 而且这是**刻意的产品决定**（专属名词/人名/型号不因为向量分低就配不上图，保留不动）：
>
> | 查询 | 向量路 | 词面路 | 结果 |
> |---|---|---|---|
> | `刘佳俊`（库里描述就叫这个） | 低 | 命中 | 召回本人照 |
> | `SKU-2026`（描述里含这个型号） | 低 | 命中 | 召回包装盒照 |
> | “只共用一个常用词”（如“公司”） | 低 | **不算命中** | 不召回 |
>
> 它与“无意义关键词也召回”只差一道闸：BM25 是 sigmoid 归一化，零重叠也给 0.5，
> 所以中间加了一道 **相似度量纲换算 + 固定词面底线**（`Bm25Ranker.KeywordSimilarityFloor`）——
> 见下方“零重叠陷阱”。
>
> 副作用（已治）：图片描述很长（尤其**自动生成的长描述**）时，BM25 因多个普通词累加而虚高，
> 会把不相关的图也拉过底线——实测一张“AI 协作插画”被“颁奖 团队 合影”以 0.38 分命中，
> 嵌进幻灯片就是用户看到的“配图不正确”。
> 治的办法不是抬高门槛（那会连带把专有名词也挡在外面），而是把“词面命中”定义收紧：
> 新增一道**词组证据**（`Bm25Ranker.HasPhraseEvidence`）——查询里必须有**一段连续词项**同时出现在文本里；
> 只共享一个孤立的常用词不算。于是 `刘佳俊` / `SKU-2026` / `颁奖典礼合影` 照样命中，
> 长描述里蹭一个“团队”不再算命中。

### 零重叠陷阱（BM25 的 0.5 基准分）

`Bm25Ranker.Score` 是 sigmoid 归一化：**零词面重叠也返回 0.5**（sigmoid(0)）。这对**融合排序**是有用的中性起点，
但“关键词召回兜底”如果写成 `if (bm25 <= 0) continue;` 就等于不筛 —— 任何查询都会把全库以 0.5 分召回。
实测影响：图库里无意义关键词也能“命中”任意照片（且 `minScore` 形同虚设，0.5 &gt; 0.25）；
知识库里任何提问都能把全库最近 120 条切片当成“关键词命中”塞进 RAG 上下文。
现在两处都用 `Bm25Ranker.ZeroOverlapScore`（=0.5）判定，回归见 `ImageLibraryTests` / `KnowledgeBaseTests` 的 `*_IgnoresZeroOverlap`。

**1.0.150 又往前修了一步：光判 0.5 基准不够，还得换量纲。**
BM25 的 sigmoid 分与余弦相似度**不可直接比**：零重叠 = 0.5，于是“只共用一个常用词”也看着像 0.54，
比向量路给无关内容的分（~0.41）还高，能盖过一切门槛（实测：提问“公司食堂今天中午吃什么”，
而描述里恰好有“公司”二字 → 0.537，连“严格”档都拦不住）。
现在词面命中先经 `Bm25Ranker.ToSimilarity`（零重叠 → 0）再过一条固定底线 `KeywordSimilarityFloor = 0.35`：
罕罕见词 / 专有号（实测 0.42）仍能笛住，只共用一个常用词（0.07）不算命中（回归：`Bm25SimilarityScaleTests`）。

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

配套工具（`tools/`）：`preview-pptx.py`（渲成图 + 自动检查）、`verify-pptx-textfit.py`（文字是否真的
装进框，见下）、`run-skill.py` / `check-skill.py`（本地编译/运行技能）。

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

- **文字溢出文本框**：我们按真实字形量高，但字体不完全一致、渲染器也可能有自己的行高算法，
  所以要用「另一个渲染器」复核一遍（见下方「文字溢出」一节里的 `verify-pptx-textfit.py`）；
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

## 文字溢出：根因、标定与自动处理（重要）

“文字溢出自己的框”是这份技能栽得最深的坑，值得单独记一笔。

### 根因：三个单位/系数错了，而渲染器一直在替我们兜底

最初的“量高”是用「字数 × 字号」估的，三处与真实排版不符：

1. **行高系数当成 1.0**：而 CJK 字体的自然行高是 1.27（微软雅黑）~1.45 em（`Noto Sans CJK SC`）；
2. **段前距单位小了 100 倍**：plan 里存的是「磅」，估算处却按「百分之一磅」乘；
3. **粗体、左缩进（`marL`）与 CJK/拉丁混排的真实字宽都没算**。

三者叠加，估出来的高度常常不到真实值的一半 —— 所以「缩字号」几乎从不触发。
以前样张看着还行，是因为每个文本框都带着 `<a:normAutofit/>`：**LibreOffice 会替我们缩**；
而 **PowerPoint 打开时并不重算 autofit**（PptxGenJS 的 `shrinkText` 被抱怨“编辑一下才生效”是同一个坑），
用户看到的才是真身 —— 文字溢出自己的框。

### 标定：用真实渲染反推行高与宽度

拿容器里的 LibreOffice 渲一遍并用 `pdftotext -bbox` 量行位（实测记录）：

| 输入 | 量到 | 结论 |
|---|---|---|
| 17pt / 行距 125% 的两行间距 | 30.92pt | 自然行高 = 30.92/(17×1.25) = **1.455 em** |
| 26pt / 行距 130% 的引言行高 | 行框 37.7pt | 37.7/26 = **1.448 em**（与上一条一致） |
| 47 个汉字 @17pt | 799pt | 汉字 advance 就是 **1.0 em**，可用宽 801pt 正好放 47 字 |
| 段前距 10pt 的两段间距 | 40.93pt = 30.92 + 10 | `spcBef` 是**在行高之上叠加**的（单位是磅） |

据此：`行高 = 字号 × max(1.45, 实测) × 行距倍率`，量宽则**区分 CJK 与拉丁**——
汉字不需要余量（两个字体都是 1 em，加了反而把“刚好放下”判成多一行），
拉丁字母保留 5%（不同字体间差得多）。

还有两个只能靠实测发现的量宽细节：

- **CJK 上下文里的空格要按 0.5 em 算**。SixLabors 量出来只有约 0.25 em，而真实排版里中文空格接近全角，
  实测差出 1.5 em ——「A / B」这类带斜杠空格的标题因此会**多折一行**。
- **这个修正必须同时打在折行的快、慢两条路径上**。`WrappedLines` 对“原样文本”有一条不做逐字量宽的快路径，
  只改慢路径的话修正根本不生效（第一版就是这么漏的）；反过来把快路径整个删掉也不对——
  宽容量会随之变化，表格单元格会被判成多一行、每页少装一行。

### 处理链条：缩字号 → 分页 → 截断并报警

1. **缩字号**：`FitScale` 用**二分**求“放得下的最大缩放”。为什么不是一步除 `availH/need`——
   高度对缩放不是线性的（字号一缩，折行数也会变少）：实测一个 3 行 28pt 的标题，一步除给出 0.65，
   而缩到 0.85 就只剩 2 行、完全放得下，白缩掉了三分之一。
   下限 12pt、行距不低于 100%（`spcPct < 100%` 时行框比字体矮，汉字墨迹会**越出行框**、顶到框外）。
2. **分页**（缩到下限仍放不下）：`content` / `summary`（`list` 与 `split`）/ `toc` 按能放下的条数拆页，
   标题带「（n/m）」，**不丢内容**；表格过长仍按行切页。与表格分页同一口径。
3. **截断 + Warn**（结构固定的框不能分页：卡片 / 示意图层 / 封面 / 图注）：按“还能放几行”截断，
   并在 `warnings` 里**说清楚截掉了多少字**——不静默丢内容。
4. **自检**：`qa` 里新增 `textOverflow`（见上），从**写出来的 XML** 反推框与字号再复核一遍，
   能拦住“排版算对了但没写进 XML”这类错位。

### 独立复核：`tools/verify-pptx-textfit.py`

自家人验自家人不算验。这个工具把产物先**摘掉 `<a:normAutofit/>`**再交给 LibreOffice 渲染，
于是“渲染器替我们兜底”这条路被断掉——渲染出来的版式完全由我们写进去的字号决定；
再把 `pdftotext -bbox` 的每个词按中心点归到对应的文本框，检查它有没有越界。

```bash
# 生成一份“长标题 / 长引言 / 长卡片 / 超多要点”的压力样张并检查
python tools/verify-pptx-textfit.py --stress
# 检查任意产物（--keep-autofit 可做对照）
python tools/verify-pptx-textfit.py x.pptx
```

两个刻意的判定约定：

- **上/左边框放宽到 6pt**：汉字的**字形墨迹**本来就会略微超出首行行框（全角标点尤其明显），
  几磅的“越出”在渲染图上看不出来；**下/右边界才严格（1.5pt）**——那才是“压到别人身上”。
- **表格/图表里的文字不计入**（它们在 `GraphicFrame`/图片里，不在文本框内），只报个数。

工具本身也踩过两个坑，改它之前先看这里：

- **归位要三种匹配**：① 完整包含该词的框 → 通过；② 否则按**中心点**找框；③ 中心点落在所有框之外时，
  认**比该词还窄/矮且与之相交**的框。“文字居中溢出一个小框”这种最该被抓住的情形，
  只看中心点会被判成“未匹配”而静默放过。
- **反向验证手法**：把某类节点的框在样张里改窄到 20pt，旧版工具会报“通过”——说明那种情形没被覆盖；
  修好匹配逻辑后必须能报出来。同理，压测样张里要**故意**包含长标题 / 长引言 / 超长卡片 / 20 条要点，
  否则“全绿”没有意义。

当前状态：`--stress`（本机字体）与 `--live`（容器字体 `Noto Sans CJK SC`）各跑一遍，均 **14 页、0 越界 / 0 页外文字**。
两个环境都要跑：本机是微软雅黑、线上是 Noto Sans CJK SC，行高相差约 14%，本机通过不代表线上通过。

### 踩坑：这些做法试过，别再来一遍

改 `FitScale` / `WrappedLines` / `TableBody` 之前请读这几条，它们都是**当时看着“更保险”、实测反而更差**的选择：

| 做法 | 为什么不能这么做 |
|---|---|
| 把折行容量**整体放宽**（加大 `LineFitSlack`）来盖住渲染器差异 | 本来放得下的内容被多算一行：引言行数变多、表格行高翻倍 |
| “**最后一行几乎填满就多算一行**”（早期试过的 `LastLineFullRatio`） | 粒度太粗：24 字 @10.5pt 的表格单元格被判成两行、行高翻倍，30 行表从 4 页变 5 页**且仍丢行** |
| 表格 `TableBody` **无条件**给“另有 N 行未显示”预留一行高度 | 拆页切好的每一页，最后一行都会在渲染阶段被换成提示行。必须改成“**全部行放得下就不留**”，且 `TableRowsPerPage` 与 `TableBody` 两边口径必须一致 |

`LineFitSlack = 0.98` 这个值是**取舍后的结果**：留 2% 余量是为了盖住约 1.6% 的量宽偏差，
再放宽就会开始产生上面第一行的副作用。它和「CJK 空格按 0.5 em」是**两件事**，不要用其中一个去顶替另一个。

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
11. **示意图真画出了自己的图形**（`DiagramPages_DrawTheirOwnShapes`）：断言到 `prstGeom`——
   金字塔/漏斗要出现 `trapezoid`、循环要出现 `triangle`、题图要出现 `parallelogram`；
   “标题在不在”拦不住静默回落。另加 `DiagramPages_MaxItems_StayInsideCanvas`（6 项上限不越界）
  与 `GeneratedHeroArt_IsDeterministic`（同一标题两次生成完全一致）。
12. **缺图自动出题图**（`MissingImage_FallsBackToGeneratedArt`）：页上无 `<p:pic>`、但有题图形状，
   且返回 `warnings` 如实说明。
13. **同页型的各变体真的长得不一样**（`VariantsOfSameType_ProduceDifferentPages`）：
   把变体的页面 XML 两两比较（先把页码徽标抹平，否则比的是页号）。
14. **字体配对不会把汉字丢给拉丁字体**（`FontPair_SetsLatinFacesAndKeepsEastAsianFont` /
   `CjkFont_FollowsExplicitChineseFontFace`）。
15. **出稿自检**（`Qa_FlagsPlaceholdersAndEmptyBody` / `Qa_PassesOnAHealthyDeck`）。
16. **原地编辑**（`Edit_DeleteReorderDuplicateReplaceAndAppend` 等）：删/重排/复制/替文字/追加，
    并用 `action:read` 把结果读回来逐页核对；另验证“不得覆盖原件”“不得删光”“图表页复制要报错”。
17. **文字过多不溢出**（`TooManyBullets_PaginateInsteadOfOverflowing` / `LongTitle_ShrinksAndStaysOutOfTheBody` /
    `OverlongCardText_IsTrimmedWithAVisibleWarning` / `ReplaceText_ThatNoLongerFits_WarnsInsteadOfSilence`）：
    20 条长要点必须**拆成多页且一条不丢**（标题带「（n/m）」）、超长标题必须缩字号且不顶进正文、
    卡片文字放不下必须**截断 + 报警**、`replaceText` 改长后必须进 `warnings`；四者都要求 `qa.issueCount == 0`。

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
# 文字是否真的装进自己的框（摘掉 autofit 后交给 LibreOffice 渲，再按词对框）
python tools/verify-pptx-textfit.py --stress
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
- **页面正文与标题都会缩**：所有文本框都走同一套「真实字形量高 → 二分求缩放」
  （`FitTexts` / `FitBox`），放不下再分页或截断。**新加页型时请用 `FitBox` 而不是裸的
  `TextBox`**，否则该页的文字就不会自适应（这是最容易被遗漏的一处）。
- 平台预置 `using` 不含 `System.IO`，需自行 `using`（本文件已含）。
- 换行统一 `\n`；正文由同步脚本统一处理。
- **页型的 `case` 字面量必须全小写**：`RenderSlide` 先做了 `type.ToLowerInvariant()`，
  写成 `case "twoCol"` 就永远匹配不上，会**静默回落**成默认要点页（实测踩到：两栏页型从来没生效过）。
  回归由单测 `EveryDocumentedSlideType_IsActuallyWired` 钉住（把每种页型与“不存在的页型”产出对比）。
- **量高的单位约定（改公式时必看）**：`ParaPlan.SpaceBefore` 是**磅**（与 `Para` 的
  `spaceBefore` 同一单位，写 XML 时×100），`Spacing` 是**行距百分数**（125 = 125%，与 `Para` 的
  `lineSpacing` 同一单位）；**行高 = 字号 × `LineHeightEm()` × 行距**。
  实测踩过：估算时把段前距当成「百分之一磅」（小了 100 倍）、把行高系数当成 1.0（小了 30%~45%），
  两项叠加让「缩字号」几乎从不触发。**改估算公式请同步核对所有调用方传的单位。**
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
- 不支持 **morph（变形）/ 3D / SmartArt 内容生成**与母版多版式；
  **动画与页间切换是支持的**（经典效果；见「动画与翻页切换」一节）。
- 图标只有内置的 45 个几何图形，没有图标字体/外部图标库；名字不在列表里就当 1~2 字的短标记。
- `image` 的 `bleed` 是“右侧半出血 + 左侧叠字”，不支持任意方向的出血/挖空。
- `image` 页的图片走 ImageSharp 读取以计算尺寸与裁剪；Sprite 不支持的格式（如 svg/emf）会报可读错误。
- 表格列宽均分（不按内容自适应），列多时字号不会自动再缩。
- 自适应用的是**真实字形度量**（`SixLabors.Fonts` 量宽 + 从字体度量取行高），已是当前环境能做到的
  最好近似；仍**不是**目标渲染器的真实排版度量（字体不同则行位有零点几到一磅的差别），
  所以刻意取“偏保守”的一侧（行高至少按 1.45 em 算、拉丁字母留 5% 余量）。
- 行高按**容器字体**（`Noto Sans CJK SC`，1.45 em）标定：在 PowerPoint（微软雅黑，约 1.27 em）
  里会更宽松 —— 宁可略松，也不要在别的环境下溢出。这个系数写在 `LineHeightEm()` 里，
  真要在某个环境上精调，改那一个常量即可。
- 自检（QA）是**文本/几何级**的：它抳不住“内容写得不对/不切题”这类语义问题。
  文字是否装进框**现在能抳住**（`textOverflow`，按真实字形量高）；
  但它仍是**我们的度量**，不是“把产物渲成图再看一遍”——后者请用 `verify-pptx-textfit.py`。
- **表格过长会自动分页**：在渲染前先按“字号下限下一页能放几行”切块，每块出一页 table 页，
  标题带「（n/m）」；**不丢行**。万一某页仍装不下（极端单元格），末行会换成
  「… 另有 N 行未显示」——总之不静默丢数据。
- **要点页过长也会自动分页**（`content` / `summary` 的 `list` 与 `split` / `toc`）：标题带「（n/m）」，不丢条。
- **结构固定的框**（卡片 / 示意图层 / 封面 / 图注 / 两栏）缩到 12pt 仍放不下时会**截断**，
  并在 `warnings` 里报出截掉的字数。想要完整内容请改用要点页（会自动分页）或精简文案。
- 图表图例最多 3 行，超出以“…等 N 项”代替（不是分页）。
- `action:read` 只取文本，不还原版式与图片；页码徽标（如 `02`）也会作为文本被取出，属噪声。
- 自检（QA）是**文本/几何级**的：它抳不住“内容写得不对/不切题”这类语义问题。
  文字是否装进框**现在能抳住**（`textOverflow`，按真实字形量高）；
  但它仍是**我们的度量**，不是“把产物渲成图再看一遍”——后者请用 `verify-pptx-textfit.py`。
