# pdf 文档生成技能（内置）

打印级 PDF 生成技能 `pdf_doc`。**纯 .NET 实现**（`PDFsharp 6.2.4` + 自定义字体解析器），
与平台其它技能同一条执行链路（Roslyn 编译 → 执行），**不依赖 Node / Python / wkhtmltopdf / LaTeX**。

> 设计参考 [MiniMax-AI/skills · minimax-pdf](https://github.com/MiniMax-AI/skills/blob/main/skills/minimax-pdf/SKILL.md)
> 的「文档类型 → 设计令牌 → 内容块」组织方式；实现细则沿用本仓库内置 pptx / docx 技能的既有决策。

## 产出的技能

| skillId | 名称 | 说明 |
|---|---|---|
| `pdf_doc` | PDF 文档生成 | 8 种文档类型 + 16 种内容块；矢量图表；可选带页码目录；中文字体子集嵌入 |

## 能力矩阵

| 路由 | 输入 | 说明 |
|---|---|---|
| **CREATE** | `blocks: [ … ]` | 从零按设计令牌生成文档（推荐，控制力最强） |
| **REFORMAT** | `markdown` / `text` | 把已有 Markdown / 纯文本套上版式重新排版（标题/列表/引用/表格/代码围栏都会转成块） |
| ~~FILL~~ | — | **不做**：PDFsharp 不支持填写 AcroForm 表单域（见「已知边界」） |

两者同时给出时 **blocks 优先**，并在返回的 `warnings` 里说明 `markdown` 被忽略。
文档类型由 `docType` 决定配色 / 字体 / 留白 / 封面版式；强调色可在语义色基础上显式覆盖。

## 参数 schema

```json
{
  "title": "知聚平台能力白皮书",
  "subtitle": "多数字员工协作平台 · 技术版",
  "author": "产品与研发中心",
  "date": "2026-09",
  "docType": "report",
  "accent": "#1F3864",
  "accentRole": "legal",
  "cover": true,
  "toc": true,
  "pageSize": "A4",
  "marginMm": 20,
  "colors": { "ink": "1A1A1A", "bg": "FFFFFF", "panel": "F4F6F8", "rule": "D8DEE4", "muted": "6B6B6B", "coverBg": "1F3864", "coverInk": "FFFFFF" },
  "fontPath": "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
  "outputPath": "/app/docs/whitepaper.pdf",
  "toc": { "title": "目录", "items": ["一、平台概述", "二、附录"] },
  "blocks": [
    { "type": "h1", "text": "一、平台概述" },
    { "type": "p", "text": "支持 **粗体**、*斜体* 与 `等宽` 的行内标记。" },
    { "type": "list", "ordered": false, "items": ["协作：多角色同场会商", "记忆：RAG 长期记忆"] },
    { "type": "callout", "kind": "info", "title": "重点", "text": "所有产物都落在可下载目录。" },
    { "type": "quote", "text": "让组织知道什么、记得什么，比单个模型有多强更重要。", "cite": "产品原则" },
    { "type": "table", "headers": ["维度", "私有化", "SaaS"], "rows": [["数据位置", "内网", "云端"]], "caption": "表 1 部署形态对照" },
    { "type": "image", "path": "/app/docs/a.png", "caption": "图 1", "widthMm": 120 },
    { "type": "chart", "chartType": "bar", "title": "活跃团队增长", "categories": ["Q1", "Q2"], "series": [{ "name": "活跃团队", "values": [120, 260] }], "yLabel": "个", "heightMm": 170 },
    { "type": "code", "language": "csharp", "code": "var x = 1;" },
    { "type": "divider" },
    { "type": "caption", "text": "以上数据为示例。" },
    { "type": "pagebreak" },
    { "type": "spacer", "heightMm": 12 },
    { "type": "toc", "title": "目录", "items": ["一、平台概述"] }
  ]
}
```

| 参数 | 必填 | 说明 |
|---|---|---|
| `title` | 否 | 封面标题与默认文件名；缺省取第一个 `h1`，再缺省「文档」 |
| `subtitle` / `author` / `date` | 否 | 封面与页脚信息 |
| `docType` | 否 | 见下表；默认 `report` |
| `accent` | 否 | 6 位十六进制（可带 `#`），覆盖该文档类型的强调色 |
| `accentRole` | 否 | 语义强调色：`legal` 深藏青 / `tech` 钢蓝 / `eco` 森林绿 / `academic` 深青 / `finance` 藏蓝 / `creative` 砖红 / `health` 青绿 / `luxury` 棕金 |
| `cover` | 否 | 是否绘制封面，默认 `true` |
| `toc` | 否 | `true` 自动插入目录；或 `{"title":"目录","items":[…]}`；`items` 省略时用文档里的 `h1`/`h2` 自动生成并**带页码** |
| `pageSize` | 否 | `A4`（默认）或 `Letter` |
| `marginMm` | 否 | 页边距（毫米），默认 20（各文档类型有自己的默认值） |
| `colors` | 否 | 高级覆盖：`ink` 正文色 / `bg` 底 / `panel` 浅底 / `rule` 分隔线 / `muted` 次要色 / `coverBg` / `coverInk` |
| `fontPath` | 否 | 显式字体（`.ttf`/`.ttc`）。也支持环境变量 `AGUI_PDF_FONT`；**输入参数优先于环境变量** |
| `outputPath` | 否 | `.pdf` 落盘路径。**建议不传**，让文件落到默认可下载目录（见「落盘与下载」） |
| `blocks` | 否\* | 内容块数组（\*与 `markdown` 二选一，至少给一个） |
| `markdown` / `text` | 否\* | 重排模式输入 |

## 文档类型与配色

强调色可由文档语义选出（`accentRole`），也可用 `accent` 显式覆盖；浅色变体（浅底 / 分隔线 / 次要色）自动派生。

| `docType` | 视觉识别 | 默认强调色 | 正文字体倾向 | 封面 |
|---|---|---|---|---|
| `report` | 深色满版 + 强调色横条 | `#1F3864` 深藏青 | 无衬线 | 深底白字 |
| `proposal` | 左右分割（左满高色块） | `#1B6CA8` 钢蓝 | 无衬线 | 左色块 + 右留白 |
| `resume` | 巨大首字母排版 | `#0F4C5C` 深青 | 无衬线 | 首字母 + 细线 |
| `academic` | 浅底古典衬线 + 双细线 | `#0F4C4C` 深青 | **衬线** | 居中 + 上下细线 |
| `minimal` | 白底 + 顶部单色细条 | `#222222` 近黑 | 无衬线 | 大量留白 |
| `editorial` | 幽灵字母（浅灰大字）+ 全大写 | `#B03A2E` 砖红 | **衬线** | 幽灵字叠真标题 |
| `magazine` | 暖色居中堆叠 + 圆形色块 | `#B4533C` 暖棕红 | **衬线** | 居中堆叠 |
| `terminal` | 近黑底 + 网格 + 等宽 + 荧光绿 | `#35E07A` 荧光绿 | 等宽 | 网格 + 命令行 |

> 「衬线 / 等宽」只有在环境里能找到对应中文字体时才真的有区别（见「字体」）。
> 找不到时解析器会回退到主字体——版式不变，字形降级，不会报错。

## 内容块（blocks[].type）

| type | 关键字段 | 说明 |
|---|---|---|
| `h1` / `h2` / `h3` | `text` | h1/h2 自动带强调线；h1/h2 会进目录 |
| `p` | `text` | 正文段落，支持 `**粗体**` / `*斜体*` / `` `等宽` `` |
| `list` | `items[]`, `ordered` | 无序（•）/ 有序（1.）列表，自动折行 |
| `callout` | `kind`(info\|warn\|success\|danger), `title`, `text` | 高亮框：左侧色条 + 浅底 |
| `quote` | `text`, `cite` | 引用：左侧强调条 + 斜体 + 出处右对齐 |
| `table` | `headers[]`, `rows[][]`, `caption` | 表头填强调色 + 隔行浅底 + 跨页自动重绘表头 |
| `image` | `path` 或 `imageQuery`, `caption`, `widthMm` | `path` = 服务器本地文件；`imageQuery` = 从平台「图库」语义检索自动配图。两者都只嵌 **PNG/JPEG**（PDFsharp 原生支持）；`path` 缺失会报错，`imageQuery` 取不到则**跳过该块**并在 `warnings` 说明 |
| `chart` | `chartType`(bar\|line\|pie\|doughnut), `title`, `categories[]`, `series[{name,values[]}]`, `yLabel`, `heightMm`, `caption` | **矢量绘制**，不进位图 |
| `code` | `code`, `language` | 浅底代码块，跨页续框，右上角语言标 |
| `divider` | — | 细分割线 |
| `caption` | `text` | 小号次要文字 |
| `pagebreak` | — | 强制分页 |
| `spacer` | `heightMm` | 垂直留白（默认 12pt） |
| `toc` | `title`, `items[]` | 目录：编号 + 点线引导 + 页码（页码来自第一趟测量的真实页号） |

Markdown 重排支持的语法：`#`/`##`/`###` 标题、`- `/`* ` 无序列表、`1. ` 有序列表、`> ` 引用、
``` 代码围栏（带语言）、`| a | b |` 表格（含 `---` 分隔行）、`---`/`***` 分割线、其余为段落。
项目符号与编号混排时会切成两个列表。

## 字体（本技能最关键的一节）

### 为什么必须自己探测字体

PDFsharp 6 的 Core 构建**不内置任何字体**，且默认不使用 Windows 系统字体。
必须提供 `IFontResolver`；否则 `new XFont(...)` 直接抛异常。官方 `dotnet:aspnet` 运行镜像
**不含任何字体**（见 `tools/docx-skills/README.md`），所以容器里必须装中文字体。

### 解析器只装一次（跨 ALC 的坑，务必遵守）

- `GlobalFontSettings.FontResolver` / `FallbackFontResolver` 的 setter **只在第一次生效**：
  字体工厂启用（即**进程内已经渲染过任何字体**）之后再设置会抛
  `InvalidOperationException: You must not change font resolver after is was once used.`
  （PDFsharp 的「同类型实例忽略」判断在跨程序集加载上下文时失效——技能每次执行都编译进**新的可卸载 ALC**，
  同名类型的 Type 标识不同）。
- 因此技能把**解析器实例 + 字体字节 + 回退族名**都存进进程级 `AppDomain` 数据：
  首次执行安装，之后复用同一实例、只更新字形字典。实测若每次执行都新建实例，
  **第二次执行起全部失败**。
- 各字体用**带内容哈希的族名**隔离（如 `AGUI_CJK_ab12cd34`、`AGUI_SERIF_0f9e…`），
  避免同一进程内换字体时命中旧的字形缓存。

### 占哪个槽位：优先「回退解析器」

技能优先把解析器装在 `GlobalFontSettings.FallbackFontResolver`（主槽留给宿主/其它 PDFsharp 使用者），
因为 PDFsharp 在**主解析器未设置、或对某族名返回 null** 时会查回退解析器——已实测：

| 进程内状态 | 本技能能否正常出字 |
|---|---|
| 两个槽都空（正常情况，平台只有本技能在渲染字体） | ✅ 装到回退槽即可 |
| 主槽被第三方解析器占了，但它对本技能的族名返回 null | ✅ 回退槽生效 |
| 主槽被占了、且它已经**渲染过**字体（字体工厂已启用） | ❌ 两个槽都改不了 → 返回**可读中文错误**而不是崩 |

好在本技能是平台里**唯一渲染文字**的 PDFsharp 使用者：`OfficeTextExtractor` 只用
`PdfReader`/`ContentReader` **读** PDF、不解析字形（已实测：先读取再安装解析器、再渲染，完全正常）。
但若以后有新组件在**本技能首次执行之前**就安装了自家字体解析器并渲染过文字，
PDF 生成会一直报「字体解析器无法安装」——届时应把两方字体来源合并到一个解析器里。

### glyf 与 CFF：必须优先 glyf

PDFsharp **只对 glyf（TrueType 轮廓）字体做子集化嵌入**；对 CFF/OTTO 字体不做子集化，
会把整个字体塞进 PDF。实测数据（同一段中文文本，单页）：

| 字体 | sfnt | face 尺寸 | 产出 PDF |
|---|---|---|---|
| `simhei.ttf`（Windows） | glyf | 9.7 MB | **24 KB** |
| `Deng.ttf`（Windows） | glyf | 16.3 MB | **48 KB** |
| `simsun.ttc`（Windows） | glyf | 18.3 MB | **32 KB** |
| `DroidSansFallbackFull.ttf`（Linux） | glyf | 4.0 MB | **46 KB** |
| `wqy-microhei.ttc`（Linux） | glyf | 4.6 MB | **46 KB** |
| `NotoSansCJK-Regular.ttc`（容器现有） | **CFF/OTTO** | 16.5 MB | **13.7 MB** ← 不可接受 |

所以字体发现逻辑会**读 sfnt 版本头 4 字节**判断轮廓类型（`OTTO` = CFF，要避开；
`\0\1\0\0` / `true` = glyf，可用），**优先选 glyf**；只有在显式指定或一个 glyf 都找不到时才用 CFF，
并在返回的 `warnings` 里警示「文件会很大」。

### TTC 必须先抽出单字体

TTC（TrueType Collection，头 4 字节 `ttcf`）不能直接喂给 PDFsharp，必须先把某个 face
抽成独立 sfnt：解析 ttcf 头 → 取 face 偏移 → 读该 face 的表目录 → 把所有表数据按新偏移重排。
这段逻辑内置于技能正文（`TtcFace.ExtractFace`，只依赖 `System.Buffers.Binary`），
Windows 的 `simsun.ttc` / Linux 的 `wqy-microhei.ttc` 都靠它。实测 `simsun.ttc` → 子集 SimSun，2 页 69 KB。

### 探测顺序

1. 输入参数 `fontPath`（**最高优先级**，允许 CFF 但会警示）
2. 环境变量 `AGUI_PDF_FONT`（同上）
3. Linux：`/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf`、
   `…/wqy/wqy-microhei.ttc`、`…/wqy/wqy-zenhei.ttc`、`…/noto/NotoSansCJK-Regular.ttc`（CFF，兜底并警示）
4. Windows：`C:\Windows\Fonts\simhei.ttf`、`Deng.ttf`、`simkai.ttf`、`msyh.ttc`、`simsun.ttc`
5. macOS：`/System/Library/Fonts/PingFang.ttc` 等
6. 另找一份**衬线**中文字体（`NotoSerifCJK` / `simsun.ttc` / `Songti.ttc` …），
   供 `academic` / `editorial` / `magazine` 使用；找不到就回退主字体

**一个字体都找不到时**，返回可读中文错误（提示安装 `fonts-wqy-microhei` / `fonts-droid-fallback`，
或用 `AGUI_PDF_FONT` 指定字体路径），**不崩**。

### 容器依赖

Docker 镜像里必须有中文字体（否则技能会报「未找到可用的中文字体」）：

```dockerfile
fonts-wqy-microhei   # 或 fonts-droid-fallback —— 两者都是 glyf，产物几十 KB
```

> ⚠️ 容器里现成的 `NotoSansCJK-Regular.ttc` 是 **CFF/OTTO**：能跑，但单页文档会到 13 MB。
> 装了 wqy/droid 之后会被优先选中（glyf 优先），体积自动降下来。

## 依赖说明

### 为什么技能正文里**没有** `#r "nuget: PdfSharp"`

`PDFsharp 6.2.4` 已经是平台依赖（`src/AguiGroupChat.Hub/AguiGroupChat.Hub.csproj` 的
`PackageReference`，供 `OfficeTextExtractor` 读取 PDF 用），也就是**已经在技能宿主的
可信平台程序集（TPA）里**。技能正文里 `using PdfSharp.Pdf; / PdfSharp.Drawing; / PdfSharp.Fonts;`
直接可编译。写 `#r "nuget: …"` 只会**白白触发一次联网还原**（宿主每次执行都会先解析 `#r` 声明）。
测试里有一条专门断言正文没有任何 `#r` 指令。

### 为什么不用 SkiaSharp

SkiaSharp 依赖**原生二进制**（`libSkiaSharp.so` 等），而平台的技能引用解析器**只解析托管程序集**，
无法加载原生库 —— `tools/docx-skills/README.md` 已有这条记录。本技能的图表因此改用
`XGraphics` 的矢量绘制（矩形 / 折线 / 圆 / 多边形），**更清晰、体积更小、也少一个依赖**。

### 图片嵌入

图片用 `XImage.FromStream(...)`，PDFsharp 6.2 原生支持 **PNG**（内置 `ImportedImagePng`/BigGustave）
与 **JPEG**，无需额外包。测试里实证了一张 PNG 被嵌入（PDF 内可见 `/Subtype/Image`）。
文件缺失会给可读错误。

### 图库配图（`imageQuery`）

除 `path` 外还可以写 `imageQuery`（如 `{"type":"image","imageQuery":"城市天际线 黄昏"}`）：
从平台「图库」（用户自己上传的图片，语义检索）取一张嵌入，**不依赖外网**。链路：

1. 平台在调用文档技能前，按**触发者本人可读的图库**（岗位**绑定了就只用绑定的**）登记一个「检索范围句柄」，
   把 `imageScopeId` 注入技能入参；聊天（单聊/知聚/编排）、技能库「试运行」、桌面宿主直跑三条路径都注入；
2. 技能带句柄回环调 `POST /ag-ui/images/search`（进程内自令牌 `AGUI_SELF_TOKEN`，启动时注入），
   拿回**服务器本地路径**；
3. 技能直接嵌入该文件。

技能**只带句柄、不自报图库 ID**：入参是模型生成的，若能自报 ID 就能越权读别人的图。
该端点也只对自令牌返回路径（登录用户只拿元数据）。

**两个刻意的差异（与 docx / pptx 不同）**：

- **只走图库**，没有 Wikimedia 回落（要真照片请用 PPT 技能）；
- **只嵌 PNG/JPEG**，所以候选里只挑这两类：只命中 WebP 时会**跳过该块**并在 `warnings` 里
  明说“建议换成 PNG/JPEG”（不硬塞 —— 那会生成打不开的 PDF，比少一张图糟得多）。

取不到图（无图库 / 库内无匹配 / 格式不对）一律**跳过该块 + 记 `warnings`**，不会让整份 PDF 出不来
（回归：`ImageQueryResolveTests`，用假平台端点把整条回环链路真跑一遍）。

### 选哪张图：显式门槛 + 上下文候选（与 Word / PPT 同口径）

模型**看不到图库里有什么**，只会按主题写 `imageQuery`。所以除了“拿关键词查一次”之外：

- 请求里**显式带 `minScore: 0.6`**（不传时平台按 0.25 兜底，而实测无意义关键词能到 0.44~0.55，等于不筛）；
- 关键词命中**不够确定**（< 0.78）或没命中时，再依次用**图自己的 caption/标题** → **最近的标题** → **前面最近的文字块**
  各查一次，取分数最高者（一旦 ≥ 0.78 就停，最多再查 3 次）；候选**分开查**而不是拼成一串 ——
  实测拼串会把关键名字稀释（人名直查 0.88 → 拼串 0.63）；
- 返回 JSON 多一个 `images[{query,fileName,score}]`：哪条检索词胜出、用的哪张图、多少分；
- 两次都没配上：`warnings` 里**两条原因都写出来**（关键词 + 上下文）。

实测（真实图库 + 真实模型）：正文写“· 刘佳俊”、关键词“员工 颁奖 舞台” → 回显用词为标题、文件为 `1刘佳俊.png`。
（PDF 里不再做字节比对：PDFsharp 会把 PNG 重编进内容流，所以用回显 + `/Image` 存在来判定。）

## 落盘与下载

不传 `outputPath` 时，文件名取 `title`（保留中文、去重追加 `-2`/`-3`，不覆盖已有文件），
输出目录按顺序取：

| 顺序 | 目录 |
|---|---|
| 1 | `$AGUI_PDF_OUT` |
| 2 | `$AGUI_DOCX_OUT`（Docker 下为 `/app/docs`，有命名卷 → **默认就落这里**） |
| 3 | `$AGUI_PPTX_OUT` |
| 4 | `<用户主目录>/agui-pdf` |
| 5 | `<临时目录>/agui-pdf` |

返回 JSON 含 `produce_file` 标记 → 网关把文件登记为附件（`att_xxx`）并挂到当前消息，
**前端可直接点击下载**。

返回示例（只返回摘要，不回灌正文，遵守 12,000 字符输出上限）：

```json
{
  "ok": true, "scene": "pdf", "docType": "report",
  "path": "/app/docs/知聚平台能力白皮书.pdf",
  "pages": 5, "blocks": 21,
  "font": "simhei.ttf（glyf/TTF，已子集化嵌入） + 衬线 simsun.ttc",
  "produce_file": { "path": "…", "name": "知聚平台能力白皮书.pdf", "bytes": 81282 },
  "warnings": ["自动选用中文字体：C:\\Windows\\Fonts\\simhei.ttf"],
  "message": "已生成 PDF：…（共 5 页）"
}
```

## 文件说明

```
tools/pdf-skills/
├── pdf_doc.cs        ← 技能正文（唯一需要维护的地方）
├── sync-builtin.mjs  ← 同步到平台内置副本（嵌入资源）
├── README.md
└── test-fonts/       ← 可选：探针用的字体样本（Droid / wqy / Noto 各一份）
```

内置副本：`src/AguiGroupChat.Agents/BuiltinSkills/pdf_doc.skill.txt`

### ⚠️ 改完记得同步到内置副本

```bash
node tools/pdf-skills/sync-builtin.mjs
```

内置目录下文件名必须是 `pdf_doc.skill.txt`：MSBuild 会把 `*.cs.txt` 里的 `cs` 当作文化区后缀
（Culture=cs），嵌入名被改写、运行时按名字找不到。脚本已代你处理命名，并统一 LF 换行。

改动正文后还应递增 `BuiltinPdfSkills.Version`，让已部署实例在升级时刷新旧快照。

## 实测验证

`tests/AguiGroupChat.Hub.Tests/PdfDocSkillTests.cs` 走**真实执行链路**
（`DotnetSkillHost`：`#r` 解析 → NuGet 还原 → Roslyn 编译 → ALC 装载 → 反射调用 `Run`）。

```bash
dotnet test tests/AguiGroupChat.Hub.Tests/AguiGroupChat.Hub.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PdfDoc"
dotnet test tests/AguiGroupChat.Hub.Tests/AguiGroupChat.Hub.Tests.csproj --nologo -v q --filter "FullyQualifiedName~BuiltinPdfSkills"
```

| 检查项 | 结果 |
|---|---|
| 覆盖 16 种块型的完整文档产出 + `produce_file` + 页数一致 | ✅ |
| `%PDF-` 头 + `PdfReader` 回读 + 页数 | ✅ |
| **中文渲染**：`/Type0` + `CIDFontType` + `/FontFile` + `/ToUnicode`（CID 字体已嵌入） | ✅ |
| **体积闸**：单页中文文档 &lt; 400 KB（防 CFF 未子集化的 13 MB 回归） | ✅ 实测 23.7 KB |
| 8 种 `docType` 封面版式都能真跑通 | ✅ |
| REFORMAT：Markdown → 块 → PDF | ✅ |
| 同名去重（`-2`）、强调色覆盖、目录带页码 | ✅ |
| 字体发现失败 / 非法 JSON / 空 blocks / 未知块型 / 不可写路径 / 缺图 → 可读错误 | ✅ |
| 正文无 `#r` 指令（PDFsharp 走 TPA） | ✅ |
| 同进程反复执行（跨 ALC 复用字体解析器） | ✅ 22 个用例连跑通过 |
| 平台自有 PDFsharp 用法（`OfficeTextExtractor` 读 PDF）先跑，再生成 | ✅ 实测不受影响 |

**独立校验（Python / pypdf 6.18.1）** 对测试真实产物开箱复验：

| 产物 | 页数 | 体积 | 严格模式打开 | 可提取 CJK 字符 | 字体 |
|---|---|---|---|---|---|
| 完整白皮书 | 5 | 81,282 B | ✅ | 389 个（179 个唯一） | `/Type0` + `/DescendantFonts`，子集 `VKXCEW+SimHei` |
| 单页中文文档 | 1 | 23,685 B | ✅ | 15 个 | `/Type0` + 子集 `LDCRVN+SimHei` |
| `academic` 版式 | 2 | 69,541 B | ✅ | — | 子集 `UIIOYD+SimSun`（来自 `simsun.ttc` 抽 face） |

能提取出正确的中文码点（如 `0x77E5` 知、`0x805A` 聚）说明 `/ToUnicode` 正确 ——
**复制粘贴、搜索、无障碍阅读都可用，换机器也不会丢字**。

## 平台约束（写这类技能必读）

1. **预置 using 不含 `System.IO`** —— 平台 Preamble 只给 `System`/`Linq`/`Text.Json`/`Net.Http` 等。
   用到 `Path`/`Directory`/`File` 必须自己写 `using System.IO;`（本文件已含）。
2. **入口必须是 `public static string Run(string input)`**（同步，不支持 `Task<string>`）。
3. **执行上限与 12,000 字符输出上限** —— 超时只计 `Run` 执行，不含还原与编译；
   内置文档类技能是 **60 秒**（`Agents:BuiltinSkillTimeoutMs`），自建 .NET 技能是 10 秒；
   技能只返回路径与摘要，不回灌正文。本技能带目录时要**渲染两趟**（先测页号再正式渲染），
   长文档请留意这点。
4. **`dotnet` 技能仅管理员可建，且一律强制人工审批**（平台安全策略）。
5. **不要命名嵌套类型 `Run`** —— 会与入口方法 `Run(string)` 冲突（`CS0102`，实测踩到过）。
6. **`XUnit` 与 `double` 的可选表达式**：`cond ? XUnit.FromMillimeter(x) : 12.0` 会因为双向隐式转换报
   `CS0172`，显式 `(double)` 转换即可（实测踩到过）。
7. **`XGraphics` 没有 `DrawPie`**：饼图/环形图用 `DrawPolygon(XBrush, XPoint[], XFillMode.Winding)`
   拼扇区（环形再盖一个底色圆）。
8. **颜色检查不能直接搜 PDF 原文**：内容流是 FlateDecode 压缩的，要用
   `page.Contents.Elements.GetDictionary(i).Stream.UnfilteredValue` 解压后再看（测试里就这么验强调色）。

## 配色（设计系统：18 套命名调色板）

原有的 `docType`（8 种版式）与 `accentRole`（8 种语义角色）不变；新增 `palette`，
一次给出品牌三色，其余（`AccentDark` / `AccentLight` / `Panel` / `Rule` / `Muted`）
继续由既有的派生逻辑展开——不另造一套规则。

```json
{ "title": "季度报告", "docType": "report", "palette": "education-charts", "markdown": "# …" }
```

18 个名字与 pptx / xlsx 完全一致（`modern-wellness` / `business-authority` / `tech-night` /
`education-charts` / `forest-eco` / `coastal-coral` / `platinum-white-gold` …）。

优先级：`palette`（品牌三色）→ `accentRole` → `accent` → `colors`，**后三者的显式单项始终覆盖 palette**，
所以旧调用方不受影响。

> 底色过一道 `Surface` 兜底：调色板里最亮色可能是亮黄（如 `education-charts` 的 `E9C46A`），
> 直接当文档底色会很难看——彩度超标就往白里混。这个坑在 pptx 侧已经踩过。

回归：`PalettePortTests`。

## 已知边界

- ❌ **不做 FILL（填表单域）**：PDFsharp **不支持 AcroForm 填写**，遇到「把这份 PDF 表单填好」这类需求
  只能改为「按同样字段生成一份新 PDF」，不能改原 PDF 的域值。
- ❌ **不套用用户上传的 PDF/模板**：平台技能无法携带/读取模板资产（同 docx/pptx 技能的边界）。
- ⚠️ **CFF/OTTO 字体不做子集化** → 产物可达 10 MB+；优先 glyf，容器装 wqy/droid 即可避免。
- ⚠️ **单一字形文件时**，粗体/斜体由 PDFsharp 合成（仿真），非真字重；`academic` 等衬线版式在没有
  衬线中文字体的环境里会回退主字体（版式保留、字形降级）。
- ⚠️ **图表是矢量绘制**，不可在 Reader 里改数据（要改请改 JSON 重新生成）；不支持 3D / 堆叠 / 双 Y 轴。
- ⚠️ 表格列宽**均分**（不按内容自适应），列多时字号不会自动再缩；`table` 的单元格不支持行内合并。
- ⚠️ 目录页码来自**第一趟测量**：若同一标题文字重复出现，目录会取第一次出现的页号。
- ⚠️ 若模型自作主张传了 `outputPath`（不由 `AGUI_*_OUT` 接管），文件会落在该路径：仍能下载，
  但该目录若是容器内非挂载路径，重建后即丢。提示词里最好明确「不要传 outputPath」。
- ⚠️ **同一进程内不能有两个字体解析器**：PDFsharp 在首次渲染字体后冻结解析器设置。
  若某组件在本技能首次执行前就装了自家解析器并渲染过文字，PDF 生成会报「字体解析器无法安装」；
  届时应合并字体来源（见「字体 · 占哪个槽位」）。
- ⚠️ 不生成 PDF/A、不签名加密、不嵌入可选内容（图层）。

## 维护提醒

正文约 100 KB（含 TTC 抽取、字体发现、版式与图表引擎），编译耗时上升，
但**超时只计 `Run` 执行、不含还原与编译**，实测单文档渲染远低于 10 s。
若继续叠加特性（如 PDF/A、双 Y 轴、脚注/交叉引用），请重新评估 10 s 预算或再拆技能。
