# AG-UI 群聊桌面版 1.0.143 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.143 Release Notes (current Windows desktop release)

**版本说明**：1.0.143 为当前 Windows 桌面版本。这一版把 **PPT 的配色重做了一遍**：18 套命名调色板逐套重算角色（底色 / 标题 / 强调 / 卡片面），修掉了“中间调铺满卡片”“鲜艳色被洗成土色”“强调色与主色糊在一起”这三类观感问题；未指定 theme 时的默认也换成更好看的一套。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.143 is the current Windows desktop release. It reworks **deck colour**. All 18 named palettes have their roles (background / title / accent / card surface) derived again, fixing three things that made decks look cheap — saturated mid-tones tiling whole slides, vivid hues washed into mud, and accents indistinguishable from the title colour. The default when no `theme` is given also moves to a better-looking palette. Web and desktop share the same Hub / gateway / frontend.

## PPT 配色重做（1.0.143）
# Deck colour rework (1.0.143)

中文：
- **为什么改**：原来的角色推导用“往黑/白里混”（sRGB 线性混色）来满足可读性，代价是颜色失真：
  `vibrant-orange-mint` 的鲜明橙 FF9F1C 被洗成脏芥末 CC7F16、`modern-wellness` 的 E29578 被洗成灰褐 B57760。
  更明显的是**卡片面**：它直接取“调色板次亮色”，而那个色常常是个中间调饱和色
  （`education-charts` 的橙 F4A261、`art-food` 的琥珀 E09F3E、`nature-outdoors` 的茶色 DDA15E）——
  铺满卡片后整份稿子就是一片色块，观感很廉价。
- **现在（五条规则）**：
  1. **调色全在 HSL 空间做**：只改亮度、保色相与饱和，目标饱和从“感知彩度”推——鲜艳的仍鲜艳、柔和的仍柔和；
  2. **底线同时成立**：正文/主色 ≥ **7:1**（以前 4.5，投影下会发灰）、副色与强调 vs 底色 ≥ 3:1、
     徽标字 vs 强调 ≥ 4.5:1、强调与主色 ≥ 2.2:1（色相拉开 40° 时 ≥ 1.5:1）；
  3. **卡片面必须是“面”**：彩度 ≤ 0.30 且与底色能看出层次，不合格就按主色合成一层淡调（5~14% 饱和）；
  4. **强调色选“最鲜艳且分得开”的**，而不是“第一个能修合格的”——实测 `vintage-academic` 会挑到一个
     与深蓝主色一样暗的 990000，而不艳一点的 C1121F 明显更好看；
  5. **未写 `theme` 时默认 `business-authority`**（以前默认历史主题 `business`：白底+金强调，
     而金色在白底上只有 2.41:1，强调线/徽标本来就弱）。**显式写 `theme:"business"` 仍是原来那套**，老调用方不受影响。
- **效果举例（改前 → 改后）**：`modern-wellness` 强调色 B57760（灰褐）→ CC7756（陶土）；
  `coastal-coral` D8665D（暗砖）→ DF7067（珊瑚）；`nature-outdoors` E9BA90（浅杏，对底色只有 1.68）→ D86A09（烤橙）；
  `vintage-academic` 暗红 990000 → C1121F；`tech-night` 灰蓝 → 005AB8（深底上更亮、与黄主色互补）；
  `education-charts` 卡片从亮橙 F4A261 → 淡灰面；`art-food` 从琥珀 E09F3E → 淡米面。
- **钉住它的测试**：`NamedPalette_RendersAndKeepsTextReadable` 从“能读就行”扩到整套设计底线
  （7:1、卡片面彩度、主色不发灰、强调色得是点色、强调与主色分得开），18 套逐对跑。
- 测试：全量 **1343 通过**。

English:
- **Why**: role derivation satisfied readability by **mixing towards black or white in sRGB**, which distorts colour — `vibrant-orange-mint`'s vivid orange FF9F1C became a dingy mustard CC7F16, and `modern-wellness`'s E29578 turned into grey-brown B57760. Worse, **card surfaces** reused the palette's second-lightest colour, which is usually a saturated mid-tone (`education-charts` orange F4A261, `art-food` amber E09F3E, `nature-outdoors` tan DDA15E): across a deck that reads as slabs of colour and looks cheap.
- **Now (five rules)**: (1) all colour maths happens in **HSL** — only lightness moves, hue and saturation are kept, and target saturation comes from perceptual chroma, so vivid stays vivid and muted stays muted; (2) **all floors hold at once** — body and title text ≥ **7:1** (was 4.5, which greys out on a projector), secondary and accent vs background ≥ 3:1, badge text on the accent ≥ 4.5:1, accent vs primary ≥ 2.2:1 (or ≥ 1.5:1 when hues are 40°+ apart); (3) **card surfaces must look like surfaces** — chroma ≤ 0.30 and visibly stepped from the background, otherwise a pale tint of the primary is synthesised; (4) **the accent is the most vivid candidate that separates well from the primary**, not the first one that can be fixed (`vintage-academic` used to pick a maroon 990000 as dark as its navy title, where the slightly softer C1121F looks far better); (5) **with no `theme`, the default is now `business-authority`** (it used to be the legacy `business` theme whose gold on white only reached 2.41:1). **An explicit `theme:"business"` still yields the old look**, so existing callers are unaffected.
- **Concrete before → after**: `modern-wellness` accent B57760 (grey-brown) → CC7756 (terracotta); `coastal-coral` D8665D (dull brick) → DF7067 (coral); `nature-outdoors` E9BA90 (pale apricot at 1.68:1 on its background) → D86A09 (burnt orange); `vintage-academic` 990000 → C1121F; `tech-night` grey-blue → 005AB8 (brighter on the dark backdrop, complementary to the yellow title); `education-charts` cards F4A261 (orange) → a pale grey surface; `art-food` E09F3E (amber) → a pale sand surface.
- **Pinned by tests**: `NamedPalette_RendersAndKeepsTextReadable` grew from “readable is enough” to the full design floor (7:1, surface chroma, title not grey, accent must be a point colour, accent separates from primary), run across all 18 palettes.
- Tests: **1343 passing** in total.

---

# AG-UI 群聊桌面版 1.0.142 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.142 Release Notes (current Windows desktop release)

**版本说明**：1.0.142 为当前 Windows 桌面版本。这一版是两处界面打磨：**新建知识库 / 新建图库改为独立弹窗**（两处体验完全一致），以及**弹窗标题图标不再重复**（修前会看到「📚 📚 知识库管理」）。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.142 is the current Windows desktop release. Two UI refinements: **creating a knowledge base or an image library now uses a proper dialog** (identical in both places), and **dialog titles no longer repeat their icon** (they used to read “📚 📚 Knowledge Base Management”). Web and desktop share the same Hub / gateway / frontend.

## 新建库走弹窗（1.0.142）
# Library creation moves into a dialog (1.0.142)

中文：
- **为何改**：原本“＋ 创建知识库 / ＋ 创建图库”是在管理弹窗里**内联展开**一小块输入区，夹在搜索框与列表之间：
  不醒目，列表一长还会被顶出视野；而且与其它“新增类操作走弹窗”的口径不一致。
- **现在**：两处共用一个「新建」弹窗（`#libCreateModal`），由各自弹窗底部的 `＋ 创建…` 按钮打开。
  打开即聚焦名称输入框；`Enter` 提交；`Esc` / 取消 / 点遮罩关闭；名称为空时提示且**不关窗**；
  创建失败**保留已填内容**（改名重试不用重打）。
- **层级**：弹窗带 `ui-dialog-overlay`（`z-index:80`），高于管理弹窗的 `60` —— 从管理弹窗里唤起也在最上层、可正常输入（同类问题之前在一次“新建分组”上报过）。
  `Esc` 在**捕获阶段**处理，所以只关新建弹窗，不会把下层的管理弹窗一起关掉。
- **两处完全一致**：知识库与图库的标题、占位符、按钮文案随类型切换；说明文案分则告知创建后怎么用（上传文档 / 上传图片）。
  弹窗开着时切语言，标题/按钮/占位符**即时跟随**，且不动已填内容。

English:
- **Why**: “Create knowledge base / Create image library” used to expand an **inline input strip** inside the manager dialog, squeezed between the search box and the list — easy to miss, pushed out of view by a long list, and inconsistent with the “new item = dialog” convention used elsewhere.
- **Now**: both share one dialog (`#libCreateModal`) opened by the `＋ Create …` button at the bottom of each manager. Focus lands on the name field, `Enter` submits, `Esc` / Cancel / backdrop click closes; an empty name warns without closing, and a failed request **keeps what you typed** so a rename-and-retry is one keystroke.
- **Stacking**: the dialog carries `ui-dialog-overlay` (`z-index:80`), above the manager dialog's `60`, so it is operable on top when opened from inside it (the same class of bug was reported once for “new user group”). `Esc` is handled in the **capture phase**, so it closes only the create dialog and leaves the manager open.
- **Identical in both places**: title, placeholders and buttons switch with the kind, and the hint explains what to do after creating (upload documents / upload images). Switching language while the dialog is open updates title, buttons and placeholders **immediately** without touching your input.

## 弹窗标题图标不再重复（1.0.142）
# Dialog titles no longer repeat their icon (1.0.142)

中文：
- **现象**：图库管理标题显示为「🖼️ 🖼️ 图库管理」；知识库那处其实一直是「📚 📚 知识库管理」，只是一直没被注意到。
- **根因**：标题的图标写在了 HTML（译文字面）里，而 i18n 值里**又带了一个**。i18n 运行时是**整体替换**元素文本
  （fallback 会被替掉、不会叠加），所以规则应是「**图标放 HTML、i18n 只放文字**」—— 这也正是其它弹窗标题一贯的口径。
- **修法**：去掉 `kb.title` / `imgLib.title` 里的开头图标，图标留在标记里；并写脚本把“值以 emoji 开头且在元素前紧邻同一个字面 emoji”的用法全扫一遍，确认无其它重复点。
- 顺带删掉因内联面板下线而失效的 `.kb-create` 样式。

English:
- **Symptom**: the image-library title read “🖼️ 🖼️ Image Library”; the knowledge-base one had actually read “📚 📚 Knowledge Base Management” all along, just unnoticed.
- **Root cause**: the title's icon lives in the markup, and the translation value carried a second copy. The i18n runtime **replaces** an element's text (the fallback is overwritten, not appended), so the rule is **icon in the markup, plain text in i18n** — which is what every other dialog title already does.
- **Fix**: strip the leading icon from `kb.title` / `imgLib.title`, keep it in the markup, and audit every value that starts with an emoji while a literal copy of that emoji sits right before the element — no other duplicates remain.
- The now-dead `.kb-create` styles were removed along with the inline panels.

---

# AG-UI 群聊桌面版 1.0.141 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.141 Release Notes (current Windows desktop release)

**版本说明**：1.0.141 为当前 Windows 桌面版本。这一版新增**图库**：把公司自己的图片传上去，数字员工出稿（PPT / Word / PDF）时按语义**自动配图**，**不依赖外网**。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.141 is the current Windows desktop release. It adds an **image library**: upload your own pictures once, and digital employees then **illustrate PPT / Word / PDF deliverables by semantic search** — **with no internet dependency**. Web and desktop share the same Hub / gateway / frontend.

## 图库：自有图片，语义配图，不出网（1.0.141）
# Image library: your own pictures, semantic matching, fully offline (1.0.141)

中文：
- **要解决什么**：此前 PPT 的 `imageQuery` 只能去 Wikimedia 找照片。内网 / 合规场景里外网往往不可达，
  而且“公司自己有一批品牌图 / 产品图，数字员工配图时自己挑”才是真实需求。
- **怎么用**：`AI 角色管理 → 🖼️ 管理图库` 建库并上传图片（可多选）；上传后**视觉模型自动写中文描述**
  （描述里附检索要素，失败则退回文件名 + 手填描述），描述 + 标签 + 文件名一起向量化。
  出稿时在需要图的位置写 `imageQuery`（关键词）即可，库内命中就用本地图片直接嵌入 —— **不需要署名**，
  因为那是你自己上传的图。
- **三个内置文档技能都接通**，但口径按能力分开：
  - **PPT**（`pptx_deck`）：先查图库，未命中再回落 Wikimedia，都没有则降级为题图；
  - **Word**（`docx_*`）：只走图库（`{"image":{"imageQuery":"…"}}`），取不到就**跳过这张图**并在 `warnings` 说明；
  - **PDF**（`pdf_doc`）：只走图库，且只嵌 **PNG/JPEG**（PDFsharp 的限制）；只命中 WebP 时**宁可跳过并告知**
    “建议换成 PNG/JPEG”，也不硬塞 —— 硬塞会生成打不开的 PDF。
- **岗位可以绑定图库**（表单「图库（可选）」）：绑了 = 该岗位出稿**只用这几个库**（典型：对外宣讲岗只准用已审核的品牌图库）；
  不绑 = 用触发者本人可读的全部图库。**绑定的库全被删时不注入**（按“无图库”降级），而**不是**静默放宽成该用户全部图库。
- **安全设计（两条硬线）**：① 平台只往技能入参里注入一个**检索范围句柄**，技能**不自报图库 ID** ——
  入参是模型生成的，能自报就能让模型写个别人的 ID 把别人的图读出来；② `/ag-ui/images/search` 的
  **服务器本地路径只回给进程内自令牌**，登录用户只拿元数据与 `/raw` 地址。图库向量也**不参与群记忆检索**。
- **三个入口都注入**（聊天单聊/知聚/编排、技能库「试运行」、桌面宿主直跑）：
  顺手补掉一个口径缺口 —— 原先只有聊天路径注入，于是「聊天里能配图、界面上手动试运行却一张图都没有」。
- **顺手修掉一个真 bug**：图库允许上传 WebP，而 Word 只认 png/jpg/gif/bmp/tiff。
  现在按**文件头**判定并把认不出的重编成 PNG（旧实现按扩展名，遇到改过名的图会报“不支持的图片格式”并让整篇文档失败）。
- **界面**：管理弹窗与知识库同构（搜索 + 创建 + 展开缩略图网格 + 逐图改描述 + 多图上传 + 处理中状态轮询 + 删图/删库），
  数字员工表单新增「图库（可选）」绑定区（只罗列已选、新增走弹窗勾选），列表带 🖼️ 计数徽标；中英文案齐全。
- **实测（容器内真实链路）**：上传一张日落城市天际线图 → 视觉模型写出“11 栋建筑 / 日落 / 渐变天空 / 平涂矢量”等要素
  → 检索命中 **0.777** → 经平台跑 `docx_report`（带 `imageQuery`）→ 产物含嵌入图片、`warnings` 为空，
  日志可见 `已为文档技能注入图库检索范围：skill=docx_report libs=1`。
- 测试：新增 14 个（回环链路用假平台端点真跑一遍：句柄 + 自令牌 + 路径 + 嵌入 + 各类降级；绑定语义：收窄/不绑取全部/绑定失效不放宽/非文档技能不动/非 JSON 不动），
  全量 **1343 通过**。

English:
- **What it solves**: `imageQuery` on decks previously only searched Wikimedia. On intranets (and for compliance) the internet is often unreachable, and “we have our own brand/product shots — let the employee pick” is the real requirement.
- **How to use it**: create a library under `AI role management → 🖼️ Image library` and upload pictures (multi-select). A **vision model writes a Chinese description** (with search facets; on failure the filename plus your caption is used), and description + tags + filename are vectorised. Then just write `imageQuery` where a picture is wanted — a library hit embeds the local file and needs **no attribution**, because it is your own image.
- **All three built-in document skills are wired up**, with deliberately different policies: **PPT** tries the library first, then Wikimedia, then generated art; **Word** uses the library only (a miss **skips that image** and explains itself in `warnings`); **PDF** uses the library only and embeds **PNG/JPEG only**, skipping rather than force-fitting a WebP (which would produce an unopenable PDF) and telling you to convert it.
- **Positions can bind libraries** (the new “Image library (optional)” form section): bound = that role may illustrate from **those libraries only** (e.g. a public-facing role restricted to approved brand assets); unbound = every library the triggering user can read. If every bound library is deleted, **nothing is injected** rather than silently widening to all of the user's libraries.
- **Security (two hard rules)**: the platform injects only a **search-scope handle**, never library IDs — the skill input is model-generated, so self-declared IDs would let a model read someone else's pictures; and the **server-side file path is returned only to the in-process self token**, while logged-in users get metadata and `/raw` URLs. Library vectors are **excluded from group-memory retrieval**.
- **All three entry points inject the scope** (chat, the skill-library “Run” button and the desktop host), closing a gap where illustrations worked in chat but not when test-running the skill from the UI.
- **A real bug fixed along the way**: the library accepts WebP while Word only embeds png/jpg/gif/bmp/tiff. Detection is now by **file signature** with re-encoding to PNG for anything else (the old code trusted the extension, so a renamed file failed the whole document).
- **UI**: the management dialog mirrors the knowledge-base one (search, create, expandable thumbnail grids, per-image caption editing, multi-file upload, processing-state polling, delete image/library); the agent form gains an “Image library (optional)” binding section (lists only what is selected, adds via a picker dialog) and the list shows a 🖼️ count badge. Chinese and English strings are complete.
- **Verified live in the container**: upload a sunset-skyline picture → the vision model describes it (11 buildings, sunset, gradient sky, flat vector) → semantic search scores **0.777** → run `docx_report` through the platform with `imageQuery` → the output contains the embedded image with empty `warnings`, and the log shows `已为文档技能注入图库检索范围：skill=docx_report libs=1`.
- Tests: 14 new cases (the loopback chain exercised against a fake platform endpoint — handle, self token, path, embedding and every degradation path; plus binding semantics: narrowed to bound, all readable when unbound, no widening when bindings are gone, non-document skills untouched, non-JSON input untouched); **1343 passing** in total.

---

# AG-UI 群聊桌面版 1.0.140 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.140 Release Notes (current Windows desktop release)

**版本说明**：1.0.140 为当前 Windows 桌面版本。这一版修掉两个「内容凭空消失」的问题：PPT 里**文字压出自己的框**（PowerPoint 打开时才看得出来），以及某种路由下数字员工先回一句「（X 代为处理）」、**之后整条消息再无正文**。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.140 is the current Windows desktop release. It fixes two ways content silently went missing: **text spilling out of its own box** in decks (only visible once PowerPoint opens the file), and a routing path where an agent replied with a single “(handled by X)” line and **nothing else**. Web and desktop share the same Hub / gateway / frontend.

## PPT：文字不再压出框（1.0.140）
# PPT: text no longer spills out of its box (1.0.140)

中文：
- **根因不是「某处字号写大了」**，而是我们的「量高」从一开始就算不出真实高度：行高系数当成 1.0（真实是 1.27~1.45 em）、
  段前距的单位小了 100 倍、粗体 / 左缩进 / 中英混排的真实字宽都没算。三项叠加，估出来的高度常常不到真实值的一半——
  于是**「缩字号」几乎从不触发**。
- **以前为什么看不出来**：每个文本框都带着 `<a:normAutofit/>`，LibreOffice 打开时会**替我们缩**；
  但 **PowerPoint 打开时并不重算 autofit**，用户看到的就是溢出后的样子。
- **标定**（容器里渲染 + `pdftotext -bbox` 实测）：自然行高 = **1.455 em**；`spcBef` 是在行高之上叠加的磅值；
  汉字 advance 正好 **1.0 em**（所以汉字不留余量、拉丁保留 5%）。修正后 `FitScale` 用**二分**求「放得下的最大缩放」，
  下限 12pt、行距不低于 100%（行距低于 100% 时汉字墨迹会冒出行框）。
- **装不下就分页，不丢内容**：要点页 / 小结 / 目录按能放下的条数**自动拆页**（标题带「（n/m）」），表格按行切页；
  结构固定的框（卡片 / 示意图层 / 封面 / 图注）会截断，并在 `warnings` 里**说清楚截掉了多少字**。
- **出稿自检新增 `textOverflow`**：从写出的 XML 反推框与字号再复核一遍；`replaceText` 改长后单独复核并进 `warnings`。
- **独立复核工具 `tools/verify-pptx-textfit.py`（新）**：先把产物里的 `<a:normAutofit/>` **摘掉**再交给 LibreOffice 渲染
  （「渲染器替我们兜底」这条路被断掉），再用 `pdftotext -bbox` 逐词查越界。本机字体与容器字体（`Noto Sans CJK SC`）各跑一遍：
  **14 页、0 越界 / 0 页外文字**。
- 顺带修掉两个真 bug：四象限纵轴名（36pt 宽的框里塞 4 个字会折行溢出）、目录卡片序号行没计入高度。
- 测试：全量 **1303 通过**（新增分页 / 截断 / 测量 / 自检用例）。

English:
- **The root cause was not “a font size written too large”** — our height measurement could never produce the real height: the line-height factor was treated as 1.0 (it is really 1.27–1.45 em), the space-before value was off by a factor of 100, and bold text, left indent and mixed CJK/Latin advance widths were not accounted for. Together these made the estimate less than half the true height, so **shrink-to-fit almost never fired**.
- **Why it used to look fine**: every text box carried `<a:normAutofit/>`, so LibreOffice **shrank the text for us**; **PowerPoint does not recompute autofit when opening**, which is the version users saw — overflowing.
- **Calibration** (render in the container, measure with `pdftotext -bbox`): natural line height is **1.455 em**; `spcBef` adds on top of the line height; a CJK glyph advances exactly **1.0 em** (so CJK gets no slack, Latin keeps 5%). `FitScale` now **binary-searches** for the largest scale that fits, floored at 12pt with line spacing never below 100% (below that, glyph ink escapes the line box).
- **When it still does not fit, paginate instead of dropping content**: bullet / summary / TOC pages **split across slides** by how many items fit (titles carry “(n/m)”) and tables split by row; fixed boxes (cards, diagram layers, cover, captions) trim and **say in `warnings` how much was cut**.
- **QA now reports `textOverflow`**: the written XML is re-read to re-check boxes against font sizes; `replaceText` re-checks after lengthening a run.
- **Independent checker `tools/verify-pptx-textfit.py` (new)**: it strips `<a:normAutofit/>` from the product **before** rendering with LibreOffice — closing the “renderer covers for us” escape hatch — then flags every word whose `pdftotext -bbox` box escapes its own text box. Run against local fonts and the container font (`Noto Sans CJK SC`): **14 slides, 0 overflows, 0 words off-slide**.
- Two real bugs fixed along the way: the quadrant page’s vertical axis label (four characters in a 36pt-wide box wrapped and overflowed) and the TOC card’s index line not being counted towards its height.
- Tests: **1303 passing** overall (new pagination / trimming / measurement / QA cases).

## 「代为处理」之后没有正文（1.0.140）
# Nothing after “handled by X” (1.0.140)

中文：
- **现象**：与某位数字员工单聊，只收到一句「（X 代为处理）」，此后再无内容——无报错、无审批卡，技能也从未执行。
- **根因有两层**：① 路由判断见到「确定性计划」就直接走计划路径，理由写的是「计划能干活」；
  但**文档生成类技能在计划里是被刻意跳过的**（结构化 JSON 入参必须由模型当工具构造），
  只挂这类技能的叶子岗位因此落到**轻量自答路径**——那条路径**没有工具、也不处理审批**，
  模型返回的是一个审批请求而不是正文，于是正文为空。
  ② 兜底只兜 `null`（`??=`），**兜不住空串 / 空白串**，剥壳后变空也一样漏掉，整条消息就只剩前缀。
- **修法**：新增 `PlanPathCanDoTheWork(def)` —— 有可派下级、或本岗有「非文档生成 / 非落库」技能才算计划能干活；
  **只挂文档生成技能的叶子**回落到完整流式路径。轻量自答为空而本岗有可执行技能时，**在同一条消息、同一个 run 内补跑一次完整流式**（复用既有审批机制）。
  三处收尾统一按 `IsNullOrWhiteSpace` 判空并补上可展示的兜底文案。
- **验证**：新增 5 个用例（含两个反向保护：有计划技能 / 有下级时**不应**回落到流式），
  并用 `MockChatClient` 的触发词稳定复现「模型什么都不回」；改回旧逻辑确认用例失败后再恢复。
  实盘复测两条单聊：8 页图示版 PPT、16 条长要点 + 四象限 + 12 行表格的详版 PPT，均正常出稿（前者**以前就是空回复**）。

English:
- **Symptom**: in a direct chat with a digital employee the only content received was a single “(handled by X)” line — no error, no approval card, and the skill never ran.
- **Two root causes**: ① the routing check short-circuited to the plan path whenever deterministic planning was on, on the assumption that “the plan can do the work”; but **document-generation skills are deliberately skipped inside the plan** (their structured JSON arguments must be built by the model as a tool call), so a leaf agent holding only such skills landed on the **lightweight self-answer path** — which **has no tools and does not handle approvals**, so the model returned an approval request instead of prose and the body came back empty. ② The fallback only covered `null` (`??=`), **not empty or whitespace strings**, and unwrapping a coordinated answer could leave it empty too, so the message was reduced to its prefix.
- **Fix**: a new `PlanPathCanDoTheWork(def)` only lets the plan path run when the agent can delegate downward or holds a skill that is neither document generation nor record-keeping; **a leaf holding only document skills falls back to the full streaming path**. When the lightweight self-answer comes back empty but the agent has an executable skill, the full streaming path is **re-run inside the same message and the same run** (reusing the existing approval machinery). All three closing paths now test `IsNullOrWhiteSpace` and add a displayable fallback.
- **Verification**: 5 new cases (including two guards asserting the streaming fallback does **not** kick in when plan-eligible skills or subordinates exist), plus a `MockChatClient` trigger that deterministically reproduces “the model returns nothing”; reverting the fix was confirmed to fail the cases before restoring it. Two end-to-end chats re-ran cleanly: an 8-slide illustrated deck and a text-heavy deck (16 long bullets + quadrant + 12-row table) — the former **used to be an empty reply**.

# AG-UI 群聊桌面版 1.0.139 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.139 Release Notes (current Windows desktop release)

**版本说明**：1.0.139 为当前 Windows 桌面版本。PPT 技能新增**自动插图**：一类是**示意图**（金字塔 / 漏斗 / 四象限 / 循环闭环 / 层叠架构），用形状把关系画出来；另一类是**程序化题图**，图片缺失时自动按主题配色生成。**全程不需要用户提供任何图片素材、不联网、无版权问题**（产物里零图片文件）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.139 is the current Windows desktop release. The PPT skill can now **illustrate itself**: diagram pages (pyramid / funnel / quadrant / cycle / stack) draw relationships with shapes, and a procedural hero image is generated from the theme palette whenever an image is missing. **No user-supplied assets, no network, no licensing concerns** (the output contains zero image files). Web and desktop share the same Hub / gateway / frontend.

## PPT：自动插图（1.0.139）
# PPT: automatic illustrations (1.0.139)

中文：
- **示意图页型（新）**：`pyramid`（3~6 层，顶层最窄，斜边**连续**）/ `funnel`（顶层最宽 + 右侧数值列）/
  `matrix`（四象限 + 轴名与轴端标签）/ `cycle`（3~6 步环形闭环 + 切向箭头 + 中心标题）/ `stack`（纵向分层条）。
  全部用 DrawingML 预设几何（`trapezoid`/`rect`/`ellipse`/`triangle`/`parallelogram`）拼出来，
  **不写自定义几何、不依赖外部素材、不联网**。
- **程序化题图**：新增 `hero` 页型；且 `image` 页与封面在图片缺失/未提供时**自动降级为题图**
  ——以前只能画一块纯色，看着就是一页色块。题图用标题作种子（**确定性**，重导出不会变），
  只用实色（无渐变）、透明度用 `a:alpha`、颜色全部取自动调色板。
- **不假称有图**：降级时页面上写明“（图片不存在，已自动生成题图）”，返回 `warnings` 也如实报。
- **让模型主动用**：技能描述里写清了“当用户说要有插图/别只有文字时，就用这些页型”，
  并强调轮换版式。实测（真实单聊）：一句“用示意图表达分层推进/转化漏斗/优先级取舍/迭代闭环，
  开头一页纯视觉题图” → 产出 6 页（hero + 金字塔 + 漏斗 + 四象限 + 闭环 + 小结），
  **产物里图片文件数为 0**，且自检报“无占位符 / 空页 / 越界”。
- **两个实测踩到的坑（都已钉回归）**：
  ① 题图最初用“旋转矩形”做斜带，旋转后的包围盒超出给定矩形 → 形状画到画布外（自检报 overflow），
  现在改用 `parallelogram`（自带斜边、不靠旋转），圆/点阵全部限位；
  ② `cycle` 一度写成 `Math.Max(3, items.Count)`，只给 1~2 项时 `n` 被抬到 3 而 `items` 没那么多 →
  数组越界崩溃（被“每个页型都真的接通了”那个用例抳到，它只给 1 项）。
- 测试：新增 18 个用例（示意图形状特征 / 6 项上限不越界 / 题图确定性 / 缺图降级 / layout 别名），
  全量 **1294 通过**；实盘 35 页 × 3 套调色板/风格组合**形状全部在版面内** + OPC 结构完整。
- **局限（实话）**：这是**图形**而不是**照片**。要有照片级插图，必须接一个文生图服务
  （平台目前没有这个能力，默认 provider 也不提供）。

English:
- **Diagram pages (new)**: `pyramid` (3-6 layers, narrowest on top, **continuous slanted sides**), `funnel` (widest on top plus a value column), `matrix` (quadrants with axis labels), `cycle` (3-6 step ring with tangential arrows and a centre caption) and `stack` (vertical layers). All are built from DrawingML preset geometry (`trapezoid`/`rect`/`ellipse`/`triangle`/`parallelogram`) — **no custom geometry, no external assets, no network**.
- **Procedural hero image**: a new `hero` page type, and `image` pages / covers now **fall back to generated art** when the picture is missing or absent — previously they drew a flat colour block, which reads as exactly that. The art is seeded by the title (**deterministic**, so re-exporting a deck does not change the cover), uses solid colours only (no gradients), expresses transparency via `a:alpha`, and takes every colour from the active palette.
- **It does not pretend there is an image**: the slide says the picture was missing and generated art was used, and the response reports it in `warnings`.
- **The model reaches for them by default**: the tool description now says to use these page types when a user asks for illustrations or “not just text”, and to rotate layouts. Verified end to end in a real single chat: one request for diagrams covering layering, a funnel, prioritisation and a loop produced 6 pages (hero + pyramid + funnel + quadrant + cycle + summary) with **zero image files** in the output, and QA reported no placeholders, empty slides or overflow.
- **Two bugs the new tests caught**: ① the hero art originally used rotated rectangles for diagonal bands, whose rotated bounding box exceeded the given rect and drew shapes off-canvas (QA reported overflow); it now uses `parallelogram`, which is slanted without rotation, and circles/dot grids are clamped. ② `cycle` computed `Math.Max(3, items.Count)`, so 1-2 items lifted the count to 3 while the item list stayed short, crashing on an out-of-range index (caught by the “every page type is actually wired” case, which supplies a single item).
- Tests: 18 new cases (shape signatures per diagram, six-item upper bound staying inside the canvas, deterministic hero art, missing-image fallback, layout aliases); **1294 passing** in total. Live checks render 35 pages across 3 palette/style combos with every shape inside the canvas and a complete OPC structure.
- **Honest limitation**: these are **graphics, not photographs**. Photo-grade illustrations require a text-to-image service, which the platform does not have today.

---

# AG-UI 群聊桌面版 1.0.138 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.138 Release Notes (current Windows desktop release)

**版本说明**：1.0.138 为当前 Windows 桌面版本。PPT 技能全面对齐参考实现（MiniMax pptx-generator）的能力面：**版式变体、图文混排、散点/雷达图、进度与环形仪表页、字体配对、出稿后自检、原地编辑既有稿**。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.138 is the current Windows desktop release. The PPT skill is now feature-aligned with the reference implementation (MiniMax pptx-generator): **layout variants, mixed text+image pages, scatter/radar charts, progress and ring gauges, font pairings, post-generation QA, and in-place editing of existing decks**. Web and desktop share the same Hub / gateway / frontend.

## PPT：版式、图文、图表、QA 与编辑（1.0.138）
# PPT: layouts, media, charts, QA and editing (1.0.138)

中文：
- **版式变体**（同一个页型多种排法，`variant`）：封面 `left/center/image/split`、目录 `list/grid/sidebar`、
  章节页 `number/bar/full`、小结 `list/cta/split`。变体是**枚举**实现的，因此单测逐个变体对比页面 XML——
  未实现的变体会**静默回落**成默认版式（以前 `twoCol` 就这么死过），光看“标题在不在”拦不住。
- **图文混排**：`image` 页新增 `left`(图左文右) / `right`(文左图右) / `bleed`(半出血+叠字) /
  `gallery`(2~4 张图廊)。图片按框**裁切（cover）**而不是拉伸，否则非等比框会变形；
  图片缺失不再静默空白，而是报 `warnings` 并在页上画占位块。
- **图表**：新增 `scatter`（散点，按 x/y 数值轴）与 `radar`（雷达，多维对比）；
  `doughnut/scatter/radar` 请求原生图表时**如实说明降级**（不静默降级）。
- **进度 / 仪表页（新页型 `progress`）**：`bar` 横向进度条 / `ring` 环形仪表。
  进度、完成度、占比这类表达用数字卡片（kpi）说不清“已走到哪”。
- **图标 12 → 45 个**：新增 `money` `target` `rocket` `shield` `layers` `globe` `network` `cloud`
  `database` `mail` `phone` `calendar` `flag` `search` `edit` `file` `pie` `link` `eye` `heart` `key`
  `crown` `map` `cpu` `package` `award` `briefcase` `users` `code` `gauge` `filter` `refresh` `download`。
- **字体**：`fontPair` 命名字体配对（georgia-calibri / cambria-calibri / trebuchet-calibri / …）+ `fontCjk`；
  **拉丁字面与中文字面分开写**（`a:latin` / `a:ea`）——拿 Georgia 去排汉字会整段落到 fallback。
- **去掉“标题下强调线”**：设计规范把它列为 AI 生成稿的典型特征，现在**默认不画**，
  需要旧观感的传 `titleRule: true`。
- **出稿后自检（QA）**：生成与编辑都会自动跑一遍（返回 `qa` 字段；也可 `action:"qa"` 单独跑）——
  查占位符 / 空页 / “只有标题” / 自动填充的空状态文案 / 形状越界。两个防误报细节：
  有图/图表的页不算“只有标题”；外部文件不知道页型时不做这项判定。
- **原地编辑既有稿（`action:"edit"`）**：删页 / 重排 / 复制页 / 替换文字 / 追加新页。
  **绝不改原件**（先复制到 `outputPath`），输出与原件相同时直接报错，不能删光、页号越界报可读错误；
  含图表的页**不支持复制**（需克隆 ChartPart 与内嵌工作簿，容易产出“需要修复”的文件——宁可报错也不破坏）。
- **两个实测踩到的坑**（都已钉回归）：
  ① 环形仪表被画成**实心饼**（用底色填内圆挖空 → 中心变成了不透明底色），改用开放折线描边画弧；
  ② `fontTitle:"宋体"` 被默认的正文字体反手盖掉，导致汉字不走宋体。
- 测试：新增 39 个用例，全量 **1276 通过**；实盘 27 页 × 3 套调色板/风格组合**形状全部在版面内** + OPC 结构完整；
  真实单聊 e2e：一次请求产出 8 页（居中封面 / 卡片目录 / 进度条 / 雷达图 / CTA 收尾），模型回报“自检通过：0 空页、0 占位符、0 越界”。
- 新增本地工具 `tools/run-skill.py`：在本地“编译 + 运行”一份技能（不依赖容器），改渲染代码时迭代快得多。

English:
- **Layout variants** (several排法 per page type, via `variant`): cover `left/center/image/split`, TOC `list/grid/sidebar`, section `number/bar/full`, summary `list/cta/split`. Variants are **enumerated in code**, so tests compare the generated page XML pairwise — an unimplemented variant **silently falls back** to the default layout (as `twoCol` once did), which a "is the title there?" assertion cannot catch.
- **Mixed text + image**: the `image` page gains `left`, `right`, `bleed` (half-bleed image with overlaid text) and `gallery` (2-4 images). Pictures are **cover-cropped** to the frame instead of stretched (a non-proportional frame would distort); a missing file no longer yields a silent blank — it reports a `warning` and draws a placeholder.
- **Charts**: adds `scatter` (numeric x/y axes) and `radar` (multi-dimension comparison); requesting native charts for `doughnut/scatter/radar` **states the fallback** rather than degrading silently.
- **Progress / gauge page (new `progress` type)**: `bar` or `ring`. Progress and completion ratios cannot be expressed by a KPI number card.
- **Icons: 12 → 45** (adds `money`, `rocket`, `shield`, `gauge`, `users`, `code`, …).
- **Fonts**: `fontPair` presets plus `fontCjk`; **Latin and East-Asian typefaces are now written separately** (`a:latin` / `a:ea`) — using Georgia for CJK dropped whole runs to fallback.
- **No more accent line under titles**: the design guide calls it a hallmark of AI-generated slides, so it is **off by default** (`titleRule: true` restores it).
- **Post-generation QA**: generation and editing both run it automatically (`qa` in the response; also available as `action:"qa"`) — placeholders, empty slides, title-only slides, auto-filled empty-state text and shapes outside the canvas. Two false-positive guards: slides containing images/charts don't count as title-only, and external files skip that check (page types unknown).
- **In-place editing (`action:"edit"`)**: delete / reorder / duplicate / replace text / append. It **never touches the original** (copies to `outputPath` first), refuses to write over the source, refuses to delete every slide, and reports out-of-range page numbers readably. Duplicating a slide that contains a chart is **not supported** (it would require cloning the ChartPart and its embedded workbook — an error beats a corrupt file).
- **Two bugs found by testing** (both pinned by regressions): ① the ring gauge rendered as a **solid pie** (carving the centre with the background colour made it opaque), now drawn as a stroked open arc; ② `fontTitle:"宋体"` was overwritten by the default body font, so CJK never used it.
- Tests: 39 new cases, **1276 passing** in total; live checks render 27 pages across 3 palette/style combos with **every shape inside the canvas** and a complete OPC structure; a real single-chat run produced an 8-page deck (centred cover / card TOC / progress bars / radar chart / CTA close) and the model reported "self-check passed: 0 empty, 0 placeholders, 0 overflow".
- New local tool `tools/run-skill.py`: compile and run a skill body locally (no container) for much faster iteration on rendering code.

---

# AG-UI 群聊桌面版 1.0.137 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.137 Release Notes (current Windows desktop release)

**版本说明**：1.0.137 为当前 Windows 桌面版本。修复**编排计划“空答复”缺陷**：计划里每一步都被跳过时（典型场景：单聊里接着说“希望有一些插图”），用户以前只会收到一句写死的“已按计划收集了各岗位的结果，但未汇总出可展示的最终文本”，既与事实不符、也拿不到文件。现在交付判断会参考计划点名的文件技能、并把计划内的产出当素材交给交付岗，直接出成品；万一交付确实没接手，兜底文案也会如实说明哪些步骤被跳过、为什么。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.137 is the current Windows desktop release. It fixes the **"empty coordinated-plan reply" defect**. When every plan step was skipped (typically a single-chat follow-up such as "希望有一些插图"), users used to get only the hard-coded line "…collected each role's results but no final text could be assembled" — untrue, and no file. Delivery detection now consults the document skill the plan named, and the plan's own output is handed to the delivery agent as source material, so a real file is produced. If delivery genuinely does not take over, the fallback text honestly states which steps were skipped and why. Web and desktop share the same Hub / gateway / frontend.

## 编排计划空答复修复（1.0.137）
# Empty coordinated-plan reply fix (1.0.137)

中文：
- **累计缓冲不再被覆盖**：每一步的产出改为**追加**到“前序已产出”，先前是每步 `Clear()` 重写 ——
  最后一步没产出时，前面各岗位的成果会被整体丢掉，而兜底文案却声称“已收集各岗位结果”。
  拼进下游提示词时按 6000 字符截断，避免累计后撑大上下文。
- **交付物类型不再只看用户那一句**：实测单聊里“我希望ppt是绿色的”能出文件（句子里恰好有 `ppt`），
  紧接着的“希望有一些插图”同样意图却什么都没拿到 —— 因为交付判断只读当前这句话。
  现在用户没提格式词时，回退用**计划里点名的文件技能**（`pptx_` / `xlsx_` / `pdf_` / `docx_`）确定交付物。
- **交付岗拿到正文素材**：计划的各步产出随 `upstreamDraft` 一起交给交付兑底（上限 12000 字符，取尾部），
  不再只盯着用户那句原始请求从零重写（否则前面的产出等于白做）。
- **兜底文案改成说实话**：区分“一步都没跑”与“跑了但没有任何产出”，并列出被跳过的步骤与原因、
  给出可行的下一步；不再在什么都没跑时说“已收集各岗位结果”。
- **不再叠两条自相矛盾的说明**：交给交付收尾时，计划侧那段说明暂不发；
  只有交付**确实静默放弃**（没认出交付物 / 找不到能做的岗位 / 空异常）时才补发，避免用户看到空消息。
- **补上诊断日志**（原先该分支完全没有日志，线上无法定位）：
  `计划无任何可展示产出，进入如实兜底：steps=… ran=… needsDelivery=… skips=[…] input=…`。
- 单测：编排/交付相关新增 21 个用例，全量 **1237 通过**。
- 实盘复验（容器 `agui-group-chat-web`）：原先只回一句兜底文案的“希望有一些插图”，
  现在产出 26 页带插图的 PPT；继续“再加一页团队介绍”“改成蓝色科技风”均正常出稿（30 页 / 32 页）。

English:
- **The cumulative buffer no longer overwrites itself**: each step's output is **appended** to "prior output" instead of `Clear()`-ing the buffer every step. Previously, when the last step produced nothing, every earlier role's work was dropped — while the fallback text still claimed results had been collected. The buffer is capped at 6000 chars when fed into downstream prompts.
- **Deliverable type no longer depends only on the user's sentence**: in a single chat, "我希望ppt是绿色的" produced a file (it happens to contain `ppt`), while the follow-up "希望有一些插图" — same intent — produced nothing, because delivery detection only read the current message. When the user names no format, the **document skill the plan named** (`pptx_` / `xlsx_` / `pdf_` / `docx_`) now decides the deliverable.
- **The delivery agent receives source material**: the plan's per-step output is passed along as `upstreamDraft` (capped at 12000 chars, tail-kept) instead of the agent rewriting everything from the user's one-line request.
- **The fallback text tells the truth**: it distinguishes "no step ran at all" from "steps ran but produced nothing", lists the skipped steps with reasons, and offers a concrete next step — no more claiming results were collected when nothing ran.
- **No more two contradictory explanations in one message**: when handing off to delivery, the plan-side note is withheld and only emitted if delivery **genuinely gave up silently** (no deliverable recognised / no capable owner / empty exception), so users never see a blank message.
- **Diagnostics added** (this branch previously logged nothing, making it undiagnosable in production): `计划无任何可展示产出，进入如实兜底：steps=… ran=… needsDelivery=… skips=[…] input=…`.
- Tests: 21 new orchestration/delivery cases; **1237 passing** in total.
- Live re-verification (container `agui-group-chat-web`): the exact message that used to return only the fallback line ("希望有一些插图") now yields a 26-page illustrated PPT; follow-ups ("再加一页团队介绍", "改成蓝色科技风") produce 30- and 32-page decks.

---

# AG-UI 群聊桌面版 1.0.136 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.136 Release Notes (current Windows desktop release)

**版本说明**：1.0.136 为当前 Windows 桌面版本。PPT 新增** 12 个内置图标**：`iconRows` 的 `icon` 可以直接填图标名，画成真正的图标（不再只能是 1~2 个字）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.136 is the current Windows desktop release. It adds **12 built-in icons** to PPT: `iconRows` can now take an icon name and renders an actual icon instead of being limited to one or two characters. Web and desktop share the same Hub / gateway / frontend.

## 内置图标（1.0.136）
# Built-in icons (1.0.136)

中文：
- 可用：`check` `cross` `arrow` `star` `dot` `warn` `lock` `user` `chart` `clock` `gear` `bulb`。
- 画法：用 ImageSharp 直接画成 PNG（透明底），颜色取主题的 OnAccent，所以**自动跟主题配色走**；
  图标放在彩色圆内并留出内边距，不贴边。
- **为什么自己画而不是用图标字体**：技能是**单个编译单元**，既不能携带字体/素材文件，
  也不能假设宿主装了某个图标字体；而 ImageSharp 已经为图表引入了，画几个几何图形是顺手的事。
- **不认识的名字仍然回退成文字**（原来那个 1~2 字的行为保留），不会变成空白。
- 单测：`IconRows_RendersBuiltInIconsAsImages`（两个图标名→两张合法 PNG、第三个名字以文字回退、图标不出圆）。
  PPT 相关 65 个用例全绿。
- 踩到的小坑：本版本 ImageSharp.Drawing **没有** `DrawLines` / `DrawArc`，折线要拆成多次 `DrawLine`，
  锁梁改用圆环代替（视觉上仍是挂锁）。

English:
- Available: `check`, `cross`, `arrow`, `star`, `dot`, `warn`, `lock`, `user`, `chart`, `clock`, `gear`, `bulb`.
- Rendering: drawn directly as transparent PNGs via ImageSharp, coloured with the theme's OnAccent so they **follow the palette automatically**; each sits inside the coloured circle with padding.
- **Why draw them instead of using an icon font**: a skill is a **single compilation unit** — it cannot ship font/asset files, nor assume the host has a given icon font. ImageSharp is already pulled in for charts, so a few geometric shapes come for free.
- **Unknown names still fall back to text** (the previous one-or-two-character behaviour), never to a blank.
- Covered by `IconRows_RendersBuiltInIconsAsImages` (two icon names yield two valid PNGs, a third name falls back to text, icons stay within the circle); 65 PPT-related cases green.
- Minor snag: this version of ImageSharp.Drawing has **no** `DrawLines` or `DrawArc`, so polylines are split into repeated `DrawLine` calls and the padlock shackle became a ring (still reads as a padlock).

---

# AG-UI 群聊桌面版 1.0.135 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.135 Release Notes (current Windows desktop release)

**版本说明**：1.0.135 为当前 Windows 桌面版本。**设计系统下沉到 Excel 与 PDF**：两个技能原先没有任何主题概念（xlsx 只有一个写死的表头蓝，pdf 只有 8 个语义 role），现在与 PPT 共享同一套 **18 个命名调色板**。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.135 is the current Windows desktop release. The **design system now reaches Excel and PDF**: neither skill had any theme concept (xlsx had a single hard-coded header blue, pdf had eight semantic roles), and both now share the same **18 named palettes** as PPT. Web and desktop share the same Hub / gateway / frontend.

## 18 套命名调色板：xlsx / pdf（1.0.135）
# 18 named palettes for xlsx and pdf (1.0.135)

中文：
- **xlsx**：新增 `theme`，可取 18 个名字之一，也可直接给十六进制主色（`"theme":"#2F6B4F"`）。
  品牌色只控**表头底 / 表头上的字 / 合计行底色与合计线**；**输入蓝与跨表引用绿刻意不变**——
  那是 Excel 多年的约定（表头用「最深色 + 对比度更高的黑白字」保证反白字始终看得清）。
- **pdf**：新增 `palette`，与既有的 `docType`（8 种版式）、`accentRole`（8 种语义角色）共存；
  `palette` 只给品牌三色（主色/底色/强调色），`AccentDark`/`AccentLight`/`Panel`/`Rule`/`Muted`
  继续由既有派生逻辑展开，不另造一套规则。
- **优先级**：`palette` → `accentRole` → `accent` → `colors`，显式单项始终覆盖，
  **不传则产出与改造前完全一致**（有单测钉住 xlsx 的 `FF1F3864` / `FFF2F2F2`）。
- **踩到的真缺陷**：xlsx 默认字色原是 `FF000000`（8 位 ARGB），我把主题色存成 6 位后直接写进去，
  变成 `rgb="000000"` —— SpreadsheetML 的 `rgb` 要求 ARGB，于是**schema 校验失败**（被既有测试当场抓住）。
  现统一补 `FF` 前缀，默认值仍逐字节等同改造前。
- 另一处沿用 pptx 的教训：pdf 的底色过一道 `Surface` 兜底，否则 `education-charts` 的最亮色是亮黄 `E9C46A`，
  会得到一张黄底文档。
- 单测：`PalettePortTests`（8 个用例）+ 全量 **1215 全绿**；回退取证：把 `theme` 参数短路掉，3 个用例立刻失败。

English:
- **xlsx** gained `theme`, accepting any of the 18 names or a bare hex brand colour (`"theme": "#2F6B4F"`). It drives only the **header fill / header text / total-row fill and rule**; the **input-blue and cross-sheet-green stay put**, being long-standing Excel conventions. The header pairs the palette's darkest colour with whichever of black/white contrasts more, so reversed text is always legible.
- **pdf** gained `palette`, coexisting with the existing `docType` (8 layouts) and `accentRole` (8 roles). It supplies only the brand three (primary / background / accent); `AccentDark`, `AccentLight`, `Panel`, `Rule` and `Muted` keep flowing from the existing derivation rather than a second set of rules.
- **Precedence**: `palette` → `accentRole` → `accent` → `colors`, so explicit single values always win, and **omitting it reproduces the previous output byte for byte** (a test pins xlsx's `FF1F3864` / `FFF2F2F2`).
- **A real defect surfaced**: xlsx's default font colour was `FF000000` (8-digit ARGB); storing theme colours as 6 digits and writing them straight through produced `rgb="000000"`, which SpreadsheetML rejects as ARGB — **schema validation failed** and the existing test caught it immediately. Colours are now normalised to ARGB, with defaults still byte-identical to before.
- The same lesson as pptx was applied to pdf: backgrounds pass through a `Surface` guard, otherwise `education-charts`' brightest colour (a bright yellow `E9C46A`) would produce a yellow-paged document.
- Tests: `PalettePortTests` (8 cases) plus a full **1215 green**; revert evidence: short-circuiting the `theme` parameter makes three cases fail at once.

---

# AG-UI 群聊桌面版 1.0.134 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.134 Release Notes (current Windows desktop release)

**版本说明**：1.0.134 为当前 Windows 桌面版本。PPT 长表格改为**自动分页**（不再“只显示前几行”）；Word 修复了**图片宽度不受正文区限制**（显式传 20/24cm 会画到页边距外）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.134 is the current Windows desktop release. Long PPT tables are now **split across pages** instead of showing only the first few rows, and Word no longer lets an **image exceed the text area** (an explicit 20/24 cm width used to run past the margins). Web and desktop share the same Hub / gateway / frontend.

## PPT：长表格自动分页
# PPT: long tables are split across pages

中文：
- **之前**：一页幻灯片只能放几行，超出的行不画，末行提示「… 另有 N 行未显示」。诚实，但用户拿不到全部数据。
- **现在**：在**渲染之前**先按“字号下限下一页能放几行”切块，每块出一页 table 页，标题带「（n/m）」；
  **每一行都在**，不再有截断提示（若某页仍装不下，截断提示仍作为兼底保留）。
- 为什么在渲染前拆：`RenderSlide` 一次只出一页，页型自己开不了新页；所以 `Build` 先把超长表格展开成多页再逐页渲染。
- **实测**（实盘容器）：30 行 × 5 列 × 60 字的表 → 自动分成 5 页；回归用例额外校验
  **30 行一行不少**、无截断提示、形状仍全在版面内。

English:
- **Before**: only a few rows fitted on one slide, the rest were omitted with an “…and N more rows” line. Honest, but the user never got the full data.
- **Now**: rows are chunked **before rendering** by how many fit at the font floor, and each chunk becomes its own table slide with a “(n/m)” title. **Every row is present** and the truncation notice is gone (it remains only as a safety net for pathological cells).
- Why split before rendering: `RenderSlide` produces exactly one slide, so a page type cannot open another page itself; `Build` therefore expands oversized tables into multiple pages first.
- **Measured** (live container): a 30-row × 5-column × 60-character table becomes 5 pages; the regression additionally checks that **all 30 rows survive**, that no truncation notice appears, and that every shape stays inside the canvas.

## Word：图片不再越出页边距
# Word: images no longer exceed the margins

中文：
- **根因**：图片宽度只被卡在 `≤24cm`，而 A4 正文宽约 **15.9cm** —— 显式传 `widthCm:20/24`
  （或传一张很宽的图配合 `widthPercent`）就会画到页边距外。实测：24cm = 8640000 EMU，正文宽仅 5731510 EMU。
- **修复**：图片等比缩到**正文区内**，上限由**当前页面的实际尺寸与页边距**算出（不写死 15.9cm），
  同时限制高度不超正文高。缩到正文宽即止，不会顺手缩得更小。
- **附带确认**：Word 侧**不需要**“正文缩字号”——Word 是流式排版（段落自然分页、表格行自动长高），
  不存在“撑出页面”。新增回归 `HugeBodyText_IsNotClipped` 用 300 条要点确认一字不少，
  把这个判断钉成可验证的事实（而不是拍脑袋不加）。
- 回退取证：去掉夹取后，该回归报「图片宽 8640000 EMU 超过了正文宽 5731510 EMU」。

English:
- **Root cause**: image width was only capped at `≤ 24 cm` while A4 text width is about **15.9 cm**, so an explicit `widthCm: 20/24` (or a wide image with `widthPercent`) drew past the margins. Measured: 24 cm = 8,640,000 EMU against a text width of 5,731,510 EMU.
- **Fix**: images scale down proportionally to fit the **text area**, with the limit derived from the **actual page size and margins** (no hard-coded 15.9 cm), and height capped to the text height too. Scaling stops as soon as the width fits, so images are never shrunk more than necessary.
- **Also confirmed**: Word does **not** need body-text shrinking — it reflows (paragraphs paginate, table rows grow), so nothing can “spill out of the page”. The new `HugeBodyText_IsNotClipped` regression asserts that all 300 bullet points survive, turning that judgement into a verifiable fact rather than an assumption.
- Revert evidence: removing the clamp makes that regression report “image width 8640000 EMU exceeds text width 5731510 EMU”.

---

# AG-UI 群聊桌面版 1.0.133 发布说明
# AG-UI Group Chat Desktop 1.0.133 Release Notes

**版本说明**：1.0.133 为当前 Windows 桌面版本。修复了 PPT 技能里三个**一直存在、且从不报错**的缺陷：① 「缩字号」实际上是死代码（内容一多就直接溢出正文区）；② **`twoCol` 页型从未生效**（每页都静默变成要点页）；③ 表格行高写死，长文本会撑出页面。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.133 is the current Windows desktop release. It fixes three long-standing defects in the PPT skill that **never reported an error**: (1) the shrink-to-fit logic was effectively dead code (content overflowed the body area instead); (2) the **`twoCol` page type never took effect** (every such slide silently became a bullet page); (3) table row heights were fixed, so long cell text pushed the table off the page. Web and desktop share the same Hub / gateway / frontend.

## 修复一：「缩字号」是死代码（1.0.133）
# Fix 1: shrink-to-fit was dead code (1.0.133)

中文：
- **现象**：PPT 内容一多就溢出正文区/页面——这就是你最早报的「内容太多会越界」。
- **根因**：高度估算函数把 `lineSpacing` 当成**百分数**（`/100.0`），而所有调用方传的都是**倍率**（`1.25`）。
  于是估出来的高度只有真实值的约 1%，`need <= boxH` 永远成立、缩放系数永远是 `1.0`——
  **“缩字号”这一整套逻辑从来没执行过**。它不报错、不影响打开，只是默默什么都没做。
- **修复**：统一为倍率（并兼容 `>5` 视为百分数，防止后来人写 `125`）。
  同时**补全页型覆盖**：`toc` / `twoCol` / `table` / `kpi` 之前根本没有调用过缩字号，现已全部接上。
- **表格额外处理**：行高改为按“最长那一列折几行”计算；缩到字号下限（约 9pt）仍装不下的行
  不画出去，末行换成「… 另有 N 行未显示」——一张幻灯片本来装不下「30 行 × 每格折 4 行」，
  硬画出去只会得到看不见的表；宁可少显示并告诉你少了多少。
- **实测**（实盘、真容器）：30 行 × 5 列 × 60 字的表 → 显示 6 行 + 「… 另有 24 行未显示」；
  20 项目录 / 每栅15 条两栏 / 4 张超长标签指标卡 → **全部形状仍在版面内**。
- 回退取证：把估算公式改回 `/100.0`，新增回归确实失败。

English:
- **Symptom**: PPT content overflowed the body area and the page as soon as there was enough of it.
- **Root cause**: the height estimator treated `lineSpacing` as a **percentage** (`/100.0`) while every caller passes a **multiplier** (`1.25`). The estimated height came out at roughly 1% of reality, so `need <= boxH` always held and the scale was always `1.0` — **the entire shrink-to-fit path had never run**. No error, no corruption, it simply did nothing.
- **Fix**: settle on the multiplier convention (still tolerating `> 5` as a percentage, in case someone writes `125`). Slide-type coverage was also completed: `toc` / `twoCol` / `table` / `kpi` never even called the shrink logic and now do.
- **Table handling**: row height is now derived from how many lines the widest cell wraps to; rows that still do not fit at the font floor (~9pt) are omitted and the last row becomes “…and N more rows”. A slide cannot hold 30 rows of four-line cells; drawing them anyway just produces an unreadable table.
- **Measured** (live container): a 30-row × 5-column × 60-character table shows 6 rows plus “…and 24 more rows”; a 20-item TOC, two 15-bullet columns and four oversized KPI labels all keep **every shape inside the canvas**.
- Revert evidence: restoring the `/100.0` formula does make the new regression fail.

## 修复二：`twoCol` 页型从未生效（1.0.133）
# Fix 2: the `twoCol` page type never took effect (1.0.133)

中文：
- **现象**：文档里写着支持两栏，实际每一页都变成普通的要点页——不报错、不崩，就是静默给你错的东西。
- **根因**：分发前对 `type` 做了 `ToLowerInvariant()`，而 `case` 写成了驼峰 `"twoCol"`，
  于是**永远匹配不上**，落入 `default` 分支（默认要点页）。
- **修复**：改为全小写 `"twocol"`；并加了回归 `EveryDocumentedSlideType_IsActuallyWired`——
  把**每种对外声明的页型**与“不存在的页型”各出一份，逐页比 XML，一样就说明没接上（会直接点名是哪个页型）。
  回退取证：把 `case` 改回驼峰，该回归报「未接上：twoCol」。

English:
- **Symptom**: two-column slides were documented but every one of them came out as an ordinary bullet page — no error, no crash, just silently wrong output.
- **Root cause**: `type` is lower-cased before dispatch, while the `case` label was written as `"twoCol"`, so it never matched and fell through to `default` (the bullet page).
- **Fix**: use the lower-case `"twocol"`, and add the regression `EveryDocumentedSlideType_IsActuallyWired` — it renders **every documented page type** and the same content with a non-existent type, compares the slides pairwise, and names any type whose output is identical (i.e. never wired).
- Revert evidence: restoring the camel-case label makes that regression report “not wired: twoCol”.

---

# AG-UI 群聊桌面版 1.0.132 发布说明
# AG-UI Group Chat Desktop 1.0.132 Release Notes

**版本说明**：1.0.132 为当前 Windows 桌面版本。修复了套模板返回的页数不对（`keepTemplateSlides` 时只报新生成页数，3 页报成 1，看着像丢了页），并校正了 PPT 技能文档里已过时的能力描述。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.132 is the current Windows desktop release. It fixes a wrong slide count returned when authoring from a template (`keepTemplateSlides` reported only the newly generated slides, so a 3-page result read as 1 and looked like the template pages had been dropped), and corrects capability statements in the PPT skill docs that had gone stale. Web and desktop share the same Hub / gateway / frontend.

## 修复：套模板返回的页数（1.0.132）
# Fix: slide count returned by template runs (1.0.132)

中文：
- **现象**：用 `keepTemplateSlides: true` 套模板时，返回的 `slides` 只是**本次新生成的页数**。
  一份 3 页的稿子（2 页模板 + 1 页新增）会报成 `1`，调用方/用户会以为模板原有页丢了。
- **修复**：改为返回**整份稿子的总页数**（`SlideIdLst` 里的实际条目数），并补了 `keepTemplateSlides` 这条路径的回归测试（之前它没有被任何用例覆盖）。
- 实测：`模板 2 页 + 追加 1 页` → 返回 `slides=3`，产物里 3 个 `slideN.xml`，模板页与新页都在。

English:
- **Symptom**: with `keepTemplateSlides: true` the returned `slides` was only the number of **newly generated** slides. A 3-page deck (2 template + 1 new) reported `1`, making it look like the template pages had been dropped.
- **Fix**: report the **total** number of slides in the deck (the actual `SlideIdLst` entries), and add regression coverage for the `keepTemplateSlides` path (it had none).
- **Measured**: a 2-page template plus 1 appended page now returns `slides=3`, with three `slideN.xml` parts in the package and both the template and new slides present.

## 文档校正：PPT 技能的能力描述
# Docs: corrected capability statements for the PPT skill

中文：把 `tools/pptx-skills/README.md` 的「已知边界」改成与实现一致：
- 原文写「**不支持从模板/既有 pptx 编辑**」——已不准确（现在支持 `action:read` 读取文本与 `template` 套模板重出）；
  改为明确“**不支持在 XML 层任意编辑既有页**”。
- 原文写「图表是图片，**不可**在 PowerPoint 内改数据」——补上原生图表选项。
- 顺带补齐之前没写清的边界：无图标素材、无图文混排版式、原生图表只支持 bar/line/pie、
  缩字号实际覆盖哪些页型、`action:read` 会把页码徽标当文本取出。

English: `tools/pptx-skills/README.md`'s "known limits" now match the implementation: "cannot edit from a template or an existing pptx" was no longer accurate (`action: read` and `template` exist) and now says arbitrary XML-level editing of existing slides is not supported; "charts are images, not editable in PowerPoint" now mentions the native chart option; and previously unstated limits were added (no icon assets, no mixed text/image layouts, native charts limited to bar/line/pie, which slide types actually shrink text, and that `action: read` surfaces the page-number badge as text).

---

# AG-UI 群聊桌面版 1.0.131 发布说明
# AG-UI Group Chat Desktop 1.0.131 Release Notes

**版本说明**：1.0.131 为当前 Windows 桌面版本。修复了 1.0.130 新引入的**套模板会丢掉模板底色**（深色模板产出变成白底）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.131 is the current Windows desktop release. It fixes a defect introduced in 1.0.130 where **authoring from a template lost the template's background colour** (a dark template came out light). Web and desktop share the same Hub / gateway / frontend.

## 修复：套模板丢掉模板底色（1.0.131）
# Fix: template authoring lost the template's background (1.0.131)

中文：
- **现象**：拿一份深色底模板出稿，产物却变成白底——配色看起来“只借了一半”。
- **两层原因，都是“读错了地方”**：
  1. 底色原本只读主题色板的 `lt1`，而 `lt1` 几乎总是 `sysClr(window)`，即**永远是白**。
     真正生效的底色写在 `<p:bg>` 里，得从那里读。
  2. 修正后仍不对：**母版也有一份 `<p:bg>`（白底）**，而代码写成
     `母版 ?? 幻灯片 ?? lt1`，于是在母版的白色上就短路了，根本轮不到幻灯片那一层。
     按 OOXML 的优先级应该是 **幻灯片 → 母版 → `lt1`**（页背景覆盖母版背景）。
- **修复**：按上述优先级取色，并在母版 / 首张幻灯片两处都读 `<p:bg>`。
- **实测**：`tech-night`（底 `000814`）作模板 → 产物 `bg=000814`、`primary=FFD60A`，与模板一致；
  之前是 `bg=FFFFFF`、`primary=806B05`（被守卫调暗过的橄榄绿）。
- 全量单测 **1201 全绿**；新增回归 `TemplateMode_KeepsDarkTemplateBackground`（外加母版白底时也会失败）。

English:
- **Symptom**: authoring from a dark template produced a light deck — the styling looked only half-adopted.
- **Two layers of the same mistake, both “reading the wrong place”**:
  1. The background was taken from the theme's `lt1`, which is almost always `sysClr(window)` — i.e. **always white**. The background that actually applies lives in `<p:bg>`.
  2. After that fix it was still wrong: the **master also carries a `<p:bg>` (white)**, and the code read `master ?? slide ?? lt1`, so it short-circuited on the master's white and never consulted the slide. OOXML precedence is **slide → master → `lt1`** (a slide's background overrides the master's).
- **Fix**: honour that precedence, reading `<p:bg>` from both the master and the first slide.
- **Measured**: a `tech-night` template (background `000814`) now yields `bg=000814`, `primary=FFD60A`, matching the template; previously `bg=FFFFFF`, `primary=806B05` (an olive that the readability guard had darkened).
- Full suite **1201 green**; new regression `TemplateMode_KeepsDarkTemplateBackground` (it also fails if the master's white background is preferred again).

---

# AG-UI 群聊桌面版 1.0.130 发布说明
# AG-UI Group Chat Desktop 1.0.130 Release Notes

**版本说明**：1.0.130 为当前 Windows 桌面版本。PPT 产出能力大改造：**18 套设计级调色板 + 4 种版式风格 + 4 种新页型**（设计系统移植），新增**原生可编辑图表**（可在 PowerPoint 里改数据）、**读取既有 pptx** 与**套用户模板出稿**。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.130 is the current Windows desktop release. A major upgrade of the deck authoring skill: **18 design-grade palettes, 4 layout styles and 4 new slide types** (ported design system), plus **native editable charts** (editable in PowerPoint), **reading existing decks** and **authoring from a user-supplied template**. Web and desktop share the same Hub / gateway / frontend.

## 设计系统：18 套调色板 + 4 种版式风格 + 4 种新页型
# Design system: 18 palettes, 4 layout styles, 4 new slide types

中文：
- **18 套命名调色板**（医疗/年报/学术/母婴/科技发布/教育统计/ESG/时尚/美食/奢侈品/云AI/旅行/快消/金融科技…）。每套只给 5 个色值，**角色（主色/底色/强调/浅底）由亮度与彩度推导**，18 套共用一条规则——而不是逐套手调。
- **两个推导规则是实盘翻车倒逼出来的**：① 底色必须先保证是“面”——`education-charts` 的最亮色是亮黄 `E9C46A`，直接当底色就是一张黄底幻灯片，现在会把彩度压下来（`F6E7C3` 奶油底）；② 强调色必须与主色**色相拉开**——`forest-eco` 原本选中 `3A5A40`，与主色 `344E41` 色相差仅 4°，强调线等于白画。
- **WCAG 可读性兜底**：正文 ≥4.5:1、主/副/强调 ≥3.0:1，并保证强调色上至少黑或白有一个能读清（它是页码徽标/序号圆的底色）。
- **`style`（与主题正交）**：`sharp` / `soft` / `rounded` / `pill`，只动页边距、间距、圆角，可与任意主题自由组合（如 `tech-night` + `pill`）。
- **4 种新页型**：`stats`（大数字看板）、`grid`（2/3 列网格卡）、`timeline`（序号圆 + 连接线）、`iconRows`（彩色圆图标行）；`content` 页还可用 `"layout":"grid"` 这类别名。
- **验证**：把上面两条推导规则分别回退后，18 套里 **6 套失败**（包括我原本没发现的 `luxury-mysterious`/`coastal-coral`/`art-food`/`vibrant-tech`）。

English:
- **18 named palettes** (healthcare, annual reports, academic, mother-and-baby, tech launches, education/statistics, ESG, fashion, food, luxury, cloud/AI, travel, FMCG, fintech, ...). Each supplies only **five colours**; the roles (primary / background / accent / light surface) are **derived from luminance and chroma**, so a single rule has to hold for all 18 instead of being hand-tuned per palette.
- **Two of those rules exist because the live run failed**: (1) the background must read as a *surface* — `education-charts`' brightest colour is a bright yellow `E9C46A`, which as a slide background is simply a yellow page; chroma is now pushed down (`F6E7C3`). (2) The accent must be **hue-separated** from the primary — `forest-eco` had picked `3A5A40`, only 4° away from the primary `344E41`, making every accent rule invisible.
- **WCAG guards**: body text ≥ 4.5:1, primary/secondary/accent ≥ 3.0:1, and the accent is adjusted so that at least one of black/white is readable on it (it backs the page-number badge and timeline nodes).
- **`style`, orthogonal to the theme**: `sharp` / `soft` / `rounded` / `pill`, moving only margins, spacing and corner radii; freely combinable with any theme (e.g. `tech-night` + `pill`).
- **Four new slide types**: `stats` (large callouts), `grid` (2–3 column cards), `timeline` (numbered nodes on a connector) and `iconRows`; `content` also accepts a `"layout"` alias.
- **Verification**: reverting either derivation rule makes **6 of the 18 palettes fail** — including four I had not spotted while writing them.

## 原生可编辑图表（可选）
# Native editable charts (opt-in)

中文：
- 默认仍渲成 PNG（兼容性最好），但需要“拿回去继续改数据”时，把 `chartType` 写成 `bar-native` / `line-native` / `pie-native`（或 `chartData:"native"`）即可生成**真正的 DrawingML 图表**，并**嵌入一份数据工作簿**（否则“编辑数据”拿不到表格）。
- `doughnut` 暂不支持原生，会**降级为图片**，并在返回 JSON 的 `nativeChartFallback` 里如实说明（不静默降级）；`nativeCharts` 报出实际生成了几张。
- **实测踩到一个真缺陷**：图表调色板常量带 `#`（ImageSharp 接受），但 `srgbClr/@val` 是 `xsd:hexBinary`，带 `#` 就不合法——OpenXmlValidator 直接报错。现已在输出层统一去 `#`。
- 单测覆盖：部件存在、关系存在、嵌入工作簿可打开、ChartSpace 缓存值与输入一致、整包过 schema 校验。**仍建议发布前用 PowerPoint 真开一次**——这是本项目唯一无法靠自动化完全覆盖的风险点。

English:
- Charts stay rasterised by default (best compatibility). When the user needs to keep editing the data, `chartType: "bar-native" / "line-native" / "pie-native"` (or `chartData: "native"`) produces a **real DrawingML chart** with an **embedded data workbook** (without it, “Edit Data” has no table).
- `doughnut` is not supported natively and **falls back to an image**, reported honestly in `nativeChartFallback` (never silent); `nativeCharts` reports how many were produced.
- **A real defect surfaced here**: the chart palette constants carry a leading `#` (ImageSharp accepts it), but `srgbClr/@val` is `xsd:hexBinary` and rejects it — OpenXmlValidator flagged it immediately. Normalised at the output layer.
- Covered by tests: parts exist, relationships exist, the embedded workbook opens, the ChartSpace cache matches the input, and the whole package passes schema validation. **Still open a file in PowerPoint once before shipping** — it is the one risk this project cannot fully automate.

## 读取既有 pptx 与套用户模板出稿
# Reading existing decks and authoring from a template

中文：
- **读取**：`{ "action": "read", "path": "att_xxx" }` → 按放映顺序返回每页文本（含备注）。只取文本，不还原版式与图片。
- **套模板**：`template` 传入既有 .pptx → **先复制再改副本，绝不写原件**（`template` 与 `outputPath` 相同时直接报错）；保留模板的母版/版式，并从其主题读出配色与字体；默认清空模板原有页（`keepTemplateSlides:true` 则追加）。
- **清空要连部件一起删**：只删 `SlideId` 的话 `ppt/slides/slideN.xml` 还会留在包里（占体积、文本仍能被搜到），实测表现为“看着像清空失败”。
- **用户上传的文件怎么传给技能**：模型只看得到附件 ID（`att_xxx`）、看不到服务器路径，而技能吃的是路径。平台现在在调用 .NET 技能前把入参里的 `att_xxx` 换成真实路径（`SkillRunner.ResolveAttachments`）；解析不到的 ID 原样保留（技能会报「找不到文件」），解析器抛异常也不会把调用搞挂。
- **实盘已验**：真的 `POST /ag-ui/upload` 拿到 `att_63007e4ffb1348f5`，再用它做 `read`（读回 2 页）与 `template`（出 3 页、母版/主题各 1 套、模板配色 `2B2D42` 生效、模板旧页已清）。

English:
- **Read**: `{ "action": "read", "path": "att_xxx" }` returns each slide's text (plus notes) in show order. Text only — layouts and images are not reconstructed.
- **Template**: pass an existing `.pptx` as `template` → the file is **copied first and only the copy is modified**; using the same path for `template` and `outputPath` is rejected outright. The template's master/layouts are kept and its theme colours and fonts are read; template slides are cleared by default (`keepTemplateSlides: true` appends instead).
- **Clearing must delete the parts too**: removing only the `SlideId` leaves `ppt/slides/slideN.xml` in the package (dead weight, and the text is still findable), which in practice looks like “the clear didn't work”.
- **How an uploaded file reaches a skill**: the model only sees the attachment id (`att_xxx`), never a server path, while skills consume paths. The platform now rewrites `att_xxx` in the arguments before invoking a .NET skill (`SkillRunner.ResolveAttachments`). Unresolvable ids pass through unchanged (the skill reports “file not found”), and a throwing resolver cannot break the call.
- **Verified live**: a real `POST /ag-ui/upload` returned `att_63007e4ffb1348f5`, which was then used for `read` (2 slides back) and `template` (3 slides out, exactly one master and one theme, the template's `2B2D42` palette in effect, and the template's old slide cleared).

---

# AG-UI 群聊桌面版 1.0.129 发布说明
# AG-UI Group Chat Desktop 1.0.129 Release Notes

**版本说明**：1.0.129 为当前 Windows 桌面版本。修复了**内置产出技能（Word / PPT）的图表**两个缺陷：① **内容一多就画出画布**（长标题、多系列、多分类、大数值）；② **饼图其实一直没画出来**（扇区被填成了几乎没面积的“弓形”）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.129 is the current Windows desktop release. It fixes two defects in the **chart renderers** of the built-in authoring skills (Word / PPT): (1) **content spilling outside the canvas** with long titles, many series, many categories or large values; and (2) **pie charts that were effectively never drawn** (slices were filled as near-zero-area circular *segments*). Web and desktop share the same Hub / gateway / frontend.

## 修复一：图表内容过多会越界（1.0.129）
# Fix 1: charts spilling outside the canvas (1.0.129)

中文：
- **现象**：图表标题很长、系列/分类很多、数值位数很大时，文字与图例会被画出画布边界（ImageSharp 会静默裁到边框，于是贴着边就是最后的痕迹）。
- **根因**：渲染器全程使用**固定坐标**——标题直接画在 `x=8`、Y 轴刻度写死左边距、图例一行横向累加 x 坐标、分类标签按字符数而不按实际槽宽判断是否挤压。内容一多，锚点就跑到画布外。
- **修复（两侧一致）**：
  - 标题按可用宽度**先缩字号再裁剪**；
  - Y 轴刻度文案**先量宽**，据此推左边距（钳在 72–200px）并**右对齐**到轴线左侧；
  - 图例按宽度**换行**（最多 3 行），每项按宽裁剪，放不下的补“…等 N 项”，且**把图例占用的行数提前计入顶部留白**；
  - 分类标签按**槽宽**裁剪，过密时**隔位显示**（`stride = ceil(52 / slot)`），绘制起点用 `ClampX` 夹在绘图区内；
  - 饼图图例同样按宽裁剪、按高限行数并补“…等 N 项”。
  - 另外 PPT 的**页面正文**（`content` 页型）新增“内容太多整体缩字号”：先整理要点 → 估算高度定缩放（下限 0.6）→ 再生 XML。
- **实测（逐像素求墨迹包围盒，画布 1400×800 / 900×480）**：

  | 图 | 修复前 | 修复后（PPT） | 修复后（Word） |
  |---|---|---|---|
  | 柱状 | 右侧贴边 `R0` | `L20 T25 R35 B51` | `L18 T23 R27 B42` |
  | 折线 | 右侧贴边 `R0` | `L20 T25 R35 B51` | `L18 T23 R24 B42` |
  | 饼图 | 墨迹铺到 `(0,0)-(1399,799)` | `L40 T23 R40 B26` | `L24 T19 R23 B29` |

English:
- **Symptom**: with a very long title, many series/categories, or many-digit values, text and legends were drawn past the canvas edge (ImageSharp silently clips at the border, so “hugging the edge” is the tell-tale sign).
- **Root cause**: the renderer used **fixed coordinates throughout** — the title was drawn at a hard-coded `x=8`, the Y-axis left padding was constant, the legend accumulated x across a single row, and category labels decided “too crowded?” by character count rather than actual slot width.
- **Fix (identical on both sides)**: titles shrink-then-clip to the available width; Y-axis tick labels are measured first to derive the left padding (clamped to 72–200px) and are **right-aligned** against the axis; legends **wrap** by width (max 3 rows), clip per item, and fall back to “…and N more”, with their row count **folded into the top padding**; category labels are clipped to the **slot** width, **thinned** when crowded (`stride = ceil(52 / slot)`) and clamped into the plot area; pie legends clip by width and cap rows the same way. PowerPoint `content` slides also gained *shrink-to-fit body text* (estimate height → pick a scale, floor 0.6 → emit XML).
- **Measured** (per-pixel ink bounding box, canvases 1400×800 / 900×480): as in the table above; before the fix the bar and line charts hugged the right edge (`R0`) and the pie's ink covered `(0,0)-(1399,799)`.

## 修复二：饼图一直没被真正画出来（1.0.129）
# Fix 2: pie charts were never actually drawn (1.0.129)

中文：
- **现象**：饼图区域**整片留白**，只在圆盘外缘留一圈极细的弧线（远看就是“这张图没画”）。
- **根因**：`PathBuilder.AddArc` **只是往当前图形里追加一段弧**——它既不会先移到圆心，也不会自动补上两条半径。代码只做 `AddArc` + `Fill`，于是路径被当成「弧 + 弦」围成的**弓形**来填充，面积远小于扇形：40 项饼图只剩 **0.8%** 的彩色像素。
  - PPT 侧还有一种更糟的形态：误用了 `AddArc` 的 7 参重载（它的前两个参数同样是**圆心**，不是包围盒左上角），算出**半径翻倍的巨大圆**，墨迹直接铺满整张画布（连 `(0,0)` 都被填上）。
- **修复**：不再依赖 `AddArc` 的重载语义，改为显式围出扇形——`MoveTo(圆心)` → 沿弧 `LineTo` 走一圈 → `CloseFigure()`，用每 2° 一段的多边形逼近（弦高远小于 1px，肉眼不可见）。Word / PPT 两侧统一。
- **实测（彩色像素占比，真圆盘应占画布约 27%）**：PPT 饼图从 **0.8% → 28.8%**；Word 饼图从 **0.8% → 26.8%**。
- **回归测试**：新增 `ChartOverflowTests`，把 200 字标题 + 30 个 20 字分类 + 4 个长名系列 + 40 项饼图图例跑过**真实** `DotnetSkillHost`，从产物里抠出图表 PNG，用自带的纯 C# PNG 解码器**逐像素求墨迹包围盒**断言四周留空边；并另加两条**饼图实心度**断言（彩色像素占比 ≥ 20%）。
  仅断言“没越界”是不够的——扇区画坏时整张图仍全在画布内，越界断言一律通过。
  已实测：把两侧的修复分别回退后，对应测试**确实失败**（PPT 饼图墨迹回到 `(0,0)-(1399,799)`、柱/折线回到 `R0`；饼图实心度回到 0.8% / 17.3% / 13.7%）。
- **全量单测**：**1157 通过 / 1 失败**。唯一失败的是既有的 `TwinTriggerTests`（用 `Task.Delay(200)` 等异步触发派发，全量并行跑时会偶发超时；单独跑通过），**与本次改动无关**，未在本次修复。

English:
- **Symptom**: the pie area was **blank**, with only a hairline arc along the rim — effectively “the chart wasn't drawn”.
- **Root cause**: `PathBuilder.AddArc` only *appends an arc to the current figure* — it neither moves to the centre nor adds the two radii. The code did `AddArc` + `Fill`, so the path was filled as the circular **segment** cut off by the arc and its chord, whose area is far smaller than the wedge: a 40-item pie kept only **0.8%** coloured pixels. On the PowerPoint side there was an even worse variant: the 7-argument `AddArc` overload was used (its first two arguments are also the **centre**, not the bounding-box corner), producing a circle of **double radius** that flooded the entire canvas (even `(0,0)` was painted).
- **Fix**: stop relying on `AddArc` overload semantics and build the wedge explicitly — `MoveTo(centre)` → walk the arc with `LineTo` → `CloseFigure()`, using a polygon approximation at 2° per step (sagitta far below 1px). Applied identically to the Word and PPT renderers.
- **Measured** (share of coloured pixels; a real disk covers ≈27% of the canvas): the PPT pie went **0.8% → 28.8%** and the Word pie **0.8% → 26.8%**.
- **Regression tests**: the new `ChartOverflowTests` runs a stress payload (200-character title, 30 categories of 20 characters, 4 long-named series, a 40-item pie legend) through the **real** `DotnetSkillHost`, extracts the chart PNGs from the produced package and asserts, with a hand-written pure-C# PNG decoder, that the **per-pixel ink bounding box** still leaves margin on all four sides. Two further assertions cover **pie solidity** (coloured share ≥ 20%): checking “nothing is out of bounds” alone is not enough, because a broken slice still sits entirely inside the canvas. Verified by reverting each side's fix in turn — the corresponding tests **do** fail (PPT pie ink back to `(0,0)-(1399,799)`, bar/line back to `R0`; solidity back to 0.8% / 17.3% / 13.7%).
- **Full suite**: **1157 passed / 1 failed**. The single failure is the pre-existing `TwinTriggerTests` (it waits on async trigger dispatch via `Task.Delay(200)` and occasionally times out under full parallel load; it passes in isolation). It is **unrelated to this change** and was left untouched.

---

# AG-UI 群聊桌面版 1.0.128 发布说明
# AG-UI Group Chat Desktop 1.0.128 Release Notes

**版本说明**：1.0.128 为当前 Windows 桌面版本。修复了**图表里的标点符号方向错误**（破折号 `—` 变成竖线、`（）「」【】《》` 被旋转 90°）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.128 is the current Windows desktop release. It fixes **punctuation rendered with the wrong orientation in charts** (an em dash `—` came out as a vertical bar, `（）「」【】《》` were rotated 90°). Web and desktop share the same Hub / gateway / frontend.

## 修复：图表标点被竖排渲染（1.0.128 核心）
# Fix: chart punctuation rendered vertically (1.0.128 headline)

中文：
- **现象**：图表里的横线类与括号类标点方向错误——破折号 `—` `–` 被画成**竖线**，`（）「」『』【】《》`
  被**旋转 90°**；而汉字与 `、。：；！？` 一切正常，所以看起来像“部分符号方向错了”。
- **根因不在字体，也不在我们的排版代码**：图表代码里**没有任何旋转**（所有文字都水平绘制）。
  把容器里的字体文件取出来用 **FreeType/PIL** 渲染，同一份字体是**横排正确**的；
  而容器里**7 种中文字体全部**在 ImageSharp 下出错（Noto Sans/Serif CJK、WQY Micro Hei/Zen Hei、
  AR PL UMing/UKai、Droid Sans Fallback）—— 所以是库的问题：
  **`SixLabors.Fonts` 1.0.0 的 shaping 错误地对 CJK 字体套用了竖排（`vert`）字形替换。**
  而它与平台选的字体无关、与备注/页型无关：只要图表文字里出现这批标点就会出现。
- **为何一直命中**：技能只声明了 `SixLabors.ImageSharp 2.1.5`，它对 `SixLabors.Fonts` 的依赖是
  `>= 1.0.0`，而平台的 NuGet 解析器取**最低满足版** —— 于是每次都落到有缺陷的 1.0.0。
- **修复**：在技能里**显式钉住 `#r "nuget: SixLabors.Fonts, 1.0.1"`**（Word 侧写在生成器的共享 banner，
  会带进三份场景技能），并加了单测 `ChartSkill_PinsSixLaborsFontsAtLeast101` 拦住被当作“多余引用”而清理掉。
  1.0.1 仍为 **Apache-2.0**，不受 Six Labors 从 2.0 起改用 Split License 的影响（这也是没升到 ImageSharp 3.x 的原因）。
- **验证（逐层取数）**：
  - **独立渲染器对照**：同一份 `wqy-microhei.ttc`，PIL/FreeType 得到 `—` 46×4（横）、`（` 18×43（高瘦）
    → 证明字体本身正确；
  - **库版本受控对比**：同一份字体、同一段诊断代码，`SixLabors.Fonts` **1.0.0 得到 `—` 4×49（竖）**，
    **1.0.1 得到 44×4（横）**；`（` 从 42×12（扁宽）变为 17×43（高瘦）；`「` `【` `《` 同样恢复正常；
  - **端到端（真实 pptx 技能产物）**：把图表分类标签设为单个 `—`，量其墨迹包围盒为 **11×1（宽高比 11.0，横向 ✓）**；
    标签设为 `（）` 得到 **9×14（高>宽，方向正确 ✓）**；另设 `XX` 作对照；
  - 全量单测 **1154 全绿**；把修复行删掉后新单测确实失败（已实测）。

English:
- **Symptom**: dashes and brackets in charts came out with the wrong orientation — an em dash `—` `–` was drawn as a **vertical bar**, and `（）「」『』【】《》` were **rotated 90°** — while Chinese characters and `、。：；！？` looked fine, so it read as “some symbols have the wrong direction”.
- **The font is not at fault, and neither is our layout code**: there is **no rotation anywhere** in the chart renderer (every string is drawn horizontally). Rendering the container's own font files through **FreeType/PIL** gives the correct horizontal shapes, while **all seven** CJK fonts present (Noto Sans/Serif CJK, WQY Micro Hei/Zen Hei, AR PL UMing/UKai, Droid Sans Fallback) misbehave under ImageSharp — so the problem is in the library: **`SixLabors.Fonts` 1.0.0 wrongly applies the vertical (`vert`) glyph substitutions to CJK fonts.** It has nothing to do with which font is picked, nor with notes or slide type: any chart text containing this punctuation is affected.
- **Why it always bit us**: the skill declared only `SixLabors.ImageSharp 2.1.5`, whose dependency on `SixLabors.Fonts` is `>= 1.0.0`, and the platform's NuGet resolver takes the **lowest satisfying version** — so it landed on the defective 1.0.0 every time.
- **Fix**: explicitly pin `#r "nuget: SixLabors.Fonts, 1.0.1"` in the skills (on the Word side it lives in the generator's shared banner, which flows into all three scene skills), with a unit test, `ChartSkill_PinsSixLaborsFontsAtLeast101`, to stop it being cleaned up as a “redundant reference”. 1.0.1 is still **Apache-2.0**, unaffected by Six Labors moving to the Split License at 2.0 — which is also why this does not upgrade to ImageSharp 3.x.
- **Verification (measured layer by layer)**:
  - **independent renderer as control**: the same `wqy-microhei.ttc` through PIL/FreeType yields `—` 46×4 (horizontal) and `（` 18×43 (tall) — the font itself is correct;
  - **controlled library-version comparison**: same font, same diagnostic code — `SixLabors.Fonts` **1.0.0 gives `—` 4×49 (vertical)** while **1.0.1 gives 44×4 (horizontal)**; `（` goes from 42×12 (flat) to 17×43 (tall); `「` `【` `《` likewise return to normal;
  - **end to end, through the real pptx skill**: with a single `—` as the only chart category label, the label ink measures **11×1 (aspect 11.0, horizontal ✓)**; with `（）` it measures **9×14 (taller than wide, correct ✓)**; `XX` was measured as a control;
  - the full suite is **1154 green**, and deleting the fix line does make the new test fail (measured).

---

# AG-UI 群聊桌面版 1.0.127 发布说明
# AG-UI Group Chat Desktop 1.0.127 Release Notes

**版本说明**：1.0.127 为 Windows 桌面版本。修复了 **Word / PPT 图表里的中文显示为乱码**（实际是空心方框）的问题。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.127 is a Windows desktop release. It fixes **Chinese text in Word / PPT charts rendering as garbled characters** (in fact hollow notdef boxes). Web and desktop share the same Hub / gateway / frontend.

## 修复：图表中文变乱码（1.0.127 核心）
# Fix: Chinese in charts rendered as garbage (1.0.127 headline)

中文：
- **根因：图表字体只按“族名命中”就选定，结果选到了纯拉丁字体**。图表是 ImageSharp 渲成的 PNG。
  字体候选名单原为 `Microsoft YaHei / SimHei / SimSun / Arial / DejaVu Sans`——在 Linux 容器里前四个
  都不存在，第一个命中的是 **`DejaVu Sans`（纯拉丁字体）**。字形缺失时 ImageSharp 会把每个汉字画成
  **空心方框（notdef）**，于是图表的中文标题 / 分类标签 / 系列名全变方框，而英文坐标数字正常——
  看上去很像“字体渲染错乱”，实际是选错了字体。
- **修复：命中候选后必须验字形覆盖**。现在会用一个常用汉字取样串逐个调 `Font.TryGetGlyphs`，
  只采用**真的含中文字形**的字体；候选名单也补上了 Linux / macOS 的常见中文字体族名
  （`Noto Sans CJK SC`、`Source Han Sans SC`、`WenQuanYi Micro Hei`、`Droid Sans Fallback`、`PingFang SC` 等）；
  第二步还会遍历系统全部字体族找中文字形（应对未知族名）。容器里现在正确选到 `Noto Sans CJK SC`。
  真的一个中文字体都没有时，不再静默出方框，而是把提示写进返回的 `message`。
- **可排障**：PPT / Word 技能的返回 JSON 新增 `chartFont` 与 `chartFontCjk`，一眼能看出图表用的是哪款字体、是否含中文字形。
- **影响范围**：内置 PPT（`pptx_deck`）与三个 Word 技能（`docx_gongwen` / `docx_notice` / `docx_report`）共用同一套图表渲染，三者的图表中文都受影响，已一并修复（Word 侧改在生成器共享内核 `tools/docx-skills/generate.mjs`，再重新生成三份技能）。
- **验证（改前 / 改后逐一取数）**：
  - 容器内实际选中字体：修复前 `DejaVu Sans` → 修复后 **`Noto Sans CJK SC`**（`chartFontCjk: true`）；
  - 把修复前实际产出的 pptx 取回，抽出图表 PNG 逐像素分析：标签带**每行恒为 24 个深色像素**——
    这是 **16 个空心方框**（4 个标签 × 4 个字，只有边框有墨）的特征；修复后同一区域逐行着墨为
    `111/85/116/52/70/48/56/67/38/68/66`，是真实汉字的笔画分布；
  - 把标签带降采样成文本图目视对比：修复前是 4 个空心方框轮廓，修复后是 4 簇互不相同的汉字笔画；
  - Word 产物同样确认：图表标签区为 3 簇真实汉字笔画（非方框）。
- **防回归**：新增单测 `ChartFont_MustHaveChineseGlyphs`——用 `SixLabors.Fonts` **独立**校验“所选字体真的含中文字形”（不只信技能自报）；环境里确实没有中文字体时跳过而非误报失败。

English:
- **Root cause: the chart font was chosen on family-name match alone, so a Latin-only font won.** Charts are PNGs rendered with ImageSharp. The candidate list was `Microsoft YaHei / SimHei / SimSun / Arial / DejaVu Sans` — none of the first four exists in the Linux container, so the first hit was **`DejaVu Sans`, a Latin-only font**. With no glyph for a character ImageSharp draws a **hollow notdef box**, so every Chinese chart title, category label and series name became a box while the Latin axis numbers looked fine — which reads like broken rendering but is really the wrong font being picked.
- **Fix: verify glyph coverage after a name match.** The picker now walks a sample string of common Chinese characters through `Font.TryGetGlyphs` and only accepts a face that **actually has them**; the candidate list gained the usual Linux / macOS CJK family names (`Noto Sans CJK SC`, `Source Han Sans SC`, `WenQuanYi Micro Hei`, `Droid Sans Fallback`, `PingFang SC`, …); a second pass scans every installed family for CJK coverage, covering unknown names. The container now picks `Noto Sans CJK SC`. When no CJK font exists at all it no longer silently emits boxes: the hint goes into the returned `message`.
- **Diagnosable**: the PPT and Word skills now return `chartFont` and `chartFontCjk`, so it is immediately visible which face the charts used and whether it carries Chinese glyphs.
- **Scope**: the built-in PPT skill (`pptx_deck`) and the three Word skills (`docx_gongwen` / `docx_notice` / `docx_report`) share one chart renderer, so all four were affected and all four are fixed (on the Word side in the generator's shared kernel, `tools/docx-skills/generate.mjs`, then regenerated).
- **Verification (measured before and after)**:
  - face actually selected in the container: `DejaVu Sans` before → **`Noto Sans CJK SC`** after (`chartFontCjk: true`);
  - the deck produced by the *old* build was fetched and its chart PNG analysed pixel by pixel: the label band held **a constant 24 dark pixels per row**, the signature of **16 hollow boxes** (4 labels × 4 characters, only the outlines inked); after the fix the same band reads `111/85/116/52/70/48/56/67/38/68/66` — the stroke distribution of real glyphs;
  - downsampling the label band into a text picture shows four hollow box outlines before and four distinct clusters of Chinese strokes after;
  - the Word output was confirmed the same way: three clusters of real strokes, not boxes.
- **Regression guard**: a new unit test, `ChartFont_MustHaveChineseGlyphs`, uses `SixLabors.Fonts` to **independently** check that the selected face really carries Chinese glyphs (rather than trusting the skill's own report); it skips instead of failing on an environment that genuinely has no CJK font.

---

# AG-UI 群聊桌面版 1.0.126 发布说明
# AG-UI Group Chat Desktop 1.0.126 Release Notes

**版本说明**：1.0.126 为 Windows 桌面版本。修复了一个严重缺陷：**内置 PPT 技能生成的 .pptx 会被 PowerPoint 判为“需要修复”才能打开**。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.126 is a Windows desktop release. It fixes a serious defect: **the built-in PPT skill produced .pptx files that PowerPoint demanded to repair before opening**. Web and desktop share the same Hub / gateway / frontend.

## 修复：PPT 产物需要“修复”才能打开（1.0.126 核心）
# Fix: PPT output needed repairing before it opened (1.0.126 headline)

中文：
- **根因：OOXML 包缺了必备部件与关系**。生成的 .pptx 里：
  1. **幻灯片母版没有关联主题部件**（`p:sldMaster` 必须拥有主题—— 颜色/字体由它提供）；
  2. 只要有一页带演讲者备注，包里就会出现 `notesSlide`，**却没有对应的 `notesMaster`**，`presentation.xml` 也缺 `notesMasterIdLst`；
  3. 版式（`slideLayout`）没有回指其母版的关系。
  这三处都会让 PowerPoint 弹“需要修复”。修复方式：生成时补上主题部件（含完整的 `clrScheme` / `fontScheme` / `fmtScheme`，且 fmtScheme 四个子列表各至少 3 项）、按需创建备注母版并由 presentation 与每个备注页分别关联、版式回指母版；另补齐备注页回指幻灯片的反关系。
- **为什么以前没被发现（教训）**：`OpenXmlValidator` 只校验**单个部件内部的 schema**，**完全不校验跨部件的必备关系**。上述三处缺漏它全部放行（错误 0），所以单测一直是“绿的”。
  同时该缺陷**与备注无关的那部分一直都在**——也就是说**之前生成的所有 .pptx 都需要修复**，与是否使用备注、图表无关。
- **新增两层防回归**：
  - 单测新增 `Package_HasAllRequiredCrossPartRelationships`，直接用 OpenXML SDK 断言包的跨部件关系（母版有主题、版式回指母版、有备注页就有备注母版、`presentation.xml` 含 `notesMasterIdLst`、备注页回指幻灯片）；
  - 新增独立工具 `tools/verify_office_package.py`，不依赖 .NET，按 OOXML 规则逐个检查 pptx / docx / xlsx 的部件与关系完整性（并报了未声明 content type、悬空关系）。
- **验证**：上述两处修复**先临时关掉**确认新测试与结构检查都会失败（已实测），恢复后再跑——全量 **1153 个用例全绿**；产物另经**独立复验**：OpenXML schema 校验 0 错误、`tools/verify_office_package.py` 结构完整、`python-pptx`（另一套实现）能打开并正确读出 8 页与全部中文备注、各部件内 `cNvPr id` 无重复、无悬空关系。

English:
- **Root cause: the OOXML package was missing required parts and relationships.** In the generated .pptx:
  1. the **slide master had no theme part** (a `p:sldMaster` must own a theme — colours and fonts come from it);
  2. as soon as any slide carried speaker notes the package contained `notesSlide` parts **with no `notesMaster`**, and `presentation.xml` lacked `notesMasterIdLst`;
  3. the slide layout **did not point back at its master**.
  All three make PowerPoint offer to repair the file. The fix adds the theme part (complete `clrScheme` / `fontScheme` / `fmtScheme`, with at least three entries in each of the four format lists), creates the notes master on demand and relates it from both the presentation and every notes slide, points the layout back at its master, and adds the inverse notes-slide-to-slide relationship.
- **Why this went unnoticed (the lesson)**: `OpenXmlValidator` only validates **the schema inside a single part** — it does **not** check the required **cross-part** relationships. It passed all three omissions with zero errors, so the unit tests stayed green. Note also that the **non-notes part of the defect was always present**: every .pptx generated before this fix needed repairing, whether or not notes or charts were used.
- **Two new layers against regression**:
  - a unit test, `Package_HasAllRequiredCrossPartRelationships`, asserting the cross-part relations through the OpenXML SDK (master has a theme, layout points back at the master, notes slides imply a notes master, `presentation.xml` carries `notesMasterIdLst`, notes slides point back at their slide);
  - a standalone tool, `tools/verify_office_package.py`, which needs no .NET and checks the parts and relationships of pptx / docx / xlsx against the OOXML rules (it also reports undeclared content types and dangling relationship targets).
- **Verification**: both fixes were **temporarily disabled first** to confirm the new test and the structure check do fail (measured), then restored — the full suite is **1153 green**; the output was additionally **independently verified**: zero OpenXML schema errors, structurally complete per `tools/verify_office_package.py`, openable by `python-pptx` (a different implementation) with all 8 slides and every Chinese note read back correctly, no duplicate `cNvPr` ids within any part, and no dangling relationships.

---

# AG-UI 群聊桌面版 1.0.125 发布说明
# AG-UI Group Chat Desktop 1.0.125 Release Notes

**版本说明**：1.0.125 为 Windows 桌面版本。在 1.0.124 基础上新增**内置 Excel 生成技能**（`xlsx_book`）与**内置 PDF 生成技能**（`pdf_doc`），并把两者接入编排交付闭环；同时修正了 PDFsharp 字体解析器（进程级全局）被抢先占用导致内置 PDF 技能失效、以及测试间泄露该全局状态的问题。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.125 is a Windows desktop release. On top of 1.0.124 it adds a **built-in Excel generation skill** (`xlsx_book`) and a **built-in PDF generation skill** (`pdf_doc`), wires both into the orchestration delivery loop, and fixes both the process-wide PDFsharp font-resolver conflict that could disable the built-in PDF skill and the test that leaked that global state. Web and desktop share the same Hub / gateway / frontend.

## 内置 Excel + PDF 生成，交付技能矩阵齐备（1.0.125 核心）
# Built-in Excel + PDF generation, completing the producer matrix (1.0.125 headline)

中文：
- **内置 `xlsx_book` Excel 技能（开箱即用）**：照 `pptx_deck` 的同一套工程约定做成嵌入资源，`DocumentFormat.OpenXml` 的 SpreadsheetML。支持多工作表、列定义（文字 / 数值 / 货币 / 百分比 / 日期）与数字格式、**公式优先**（算出来的列写真正的工作表公式，合计行自动生成 SUM 等，跨表引用用 `表名!`）、冻结窗格与自动筛选、合计行上框线（会计式），并遵循财务配色惯例（硬编码输入＝蓝、同表公式＝黑、跨表公式＝绿）；另提供 `analyze` 模式读取已有 .xlsx 并回报各表结构摘要。产物带 `produce_file` 标记 → 直接可下载，实测对话产出 `.xlsx`（2 个工作表 / 14 个公式 / 4.3 KB）。
- **内置 `pdf_doc` PDF 技能（开箱即用）**：借鉴 MiniMax-AI/skills 的 minimax-pdf 思路，由**设计令牌**驱动——多种文档类型（report / proposal / resume / academic / minimal / editorial / magazine / terminal）各带封面版式与语义强调色，内容块体系（标题 / 正文 / 富文本标记 / 列表 / callout / 引用 / 表格 / 图片 / 矢量图表 / 代码 / 分隔线 / 图注 / 目录 / 分页 / 留白 / 指标卡），既可直接给结构化 `blocks`，也可把整篇 **Markdown 重排**成 PDF（REFORMAT 路由）。A4 / Letter、页码页脚、目录页码回溯填充。
- **中文字体：自动探测 + 子集嵌入（关键工程点）**：PDF 必须把字体嵌进文件，而 PDFsharp 只对 **glyf（TrueType 轮廓）** 字体做子集化，对 **CFF/OTTO** 字体会把整份字体塞进去。实测：同一页中文文档，glyf 字体产出 **24–48 KB**，而容器已有的 `NotoSansCJK`（CFF）产出 **13.7 MB**。因此镜像新增 `fonts-droid-fallback`（Apache-2.0，glyf）供 PDF 技能使用，技能侧也会读字体头自行判别 glyf/CFF、并对 CFF 给出体积警示；可用 `AGUI_PDF_FONT` 或参数 `fontPath` 指定自己的字体（支持 .ttf/.ttc，TTC 会自动抽 face）。实测对话产出 4 页中文 PDF：**95 KB**，字体为子集 `/BIIYBT+Droid Sans Fallback`，中文可提取可搜索。
- **编排交付闭环覆盖四类格式**：要 Excel / 表格 / 报表 → 点名 `xlsx_book`；要 PDF → 点名 `pdf_doc`（与原有 PPT/Word 一致），并加入 `org_design` 技能正文的复用规则与“空洞交付技能”告警文案。
- **交付“厚度”按类型分别度量（修正）**：原先把各技能的字段混着取最大值，而各技能报的字段语义不同（pptx 报 `slides`、xlsx 报 `rows`、docx/pdf 报 `blocks`）——xlsx 的结果会因为没有 `blocks` 而被判成“0 内容”触发无谓重试。现按交付类型选度量：pptx 看页数（≥3）、xlsx 看数据行数（≥3）、docx/pdf 看内容块数（≥5）。
- **Excel / PDF 入参形状校验**：与 docx 的 `sections`、pptx 的 `slides` 同理，新增 xlsx 的 `sheets`（必须同时有列定义与数据行）与 pdf 的 `blocks`（每块要有 `type`，或直接给 `markdown`）校验，拦住“只有标题的空表 / 只有封面的 PDF”，并回送正确形状让模型自纠。
- **PDFsharp 字体解析器冲突（进程级全局，已修）**：PDFsharp 只允许安装一次字体解析器，且一旦有人渲染过 PDF 文字，该插槽就<b>永久冻结</b>。技能都跑在 Web 同一进程里，因此一个先渲染 PDF 文字的自建 dotnet 技能会连带内置 PDF 技能一起失效。现技能会先尽力安装自带解析器，失败时尝试复用已安装的那个（可解析中文字体时仍可出稿），否则给出<b>可执行的中文错误</b>（而不是一句莫名的异常）。
- **测试间泄露该全局状态（已修）**：附件存储用例原先用 PDFsharp 现场渲染一个 PDF 夹具，从而在测试进程里冻结了解析器插槽，导致全量跑时 PDF 技能用例连锁失败（实测 16 个）。现改为手写一份最小 PDF 夹具（base-14 Helvetica，不需字体解析器）——它只关心“能从 PDF 提取文本”，本不该占用这个全局；全量 1129 个用例恢复全绿。

English:
- **Built-in `xlsx_book` Excel skill (works out of the box)**: shipped as an embedded resource using the same engineering conventions as `pptx_deck`, implemented on `DocumentFormat.OpenXml` SpreadsheetML. It supports multiple worksheets, column definitions (text / number / currency / percent / date) and number formats, **formulas first** (computed columns carry real worksheet formulas, totals rows generate SUM and friends, cross-sheet references use `Sheet!`), frozen panes and autofilter, an accounting rule above totals, and the usual financial color convention (hard-coded input = blue, same-sheet formula = black, cross-sheet formula = green); an `analyze` mode reads an existing .xlsx and reports a structural summary per sheet. The output carries the `produce_file` marker → downloadable, and was verified live (2 worksheets / 14 formulas / 4.3 KB).
- **Built-in `pdf_doc` PDF skill (works out of the box)**: following the design-system idea of MiniMax-AI/skills' minimax-pdf, it is driven by **design tokens** — several document types (report / proposal / resume / academic / minimal / editorial / magazine / terminal) each with their own cover layout and semantic accent color, plus a content-block system (headings / paragraphs with inline markup / lists / callout / quote / table / image / vector charts / code / divider / caption / toc / page break / spacer / KPI). Feed it structured `blocks`, or hand it a whole **Markdown** document to re-typeset into PDF (the REFORMAT route). A4 / Letter, page numbers and footers, and a table of contents whose page numbers are resolved on a second pass.
- **Chinese fonts: auto-detection plus subset embedding (the key engineering point)**: a PDF must embed its fonts, and PDFsharp only subsets **glyf (TrueType outline)** fonts — for **CFF/OTTO** fonts it embeds the entire file. Measured on the same one-page Chinese document: a glyf font yields **24–48 KB** while the container's pre-existing `NotoSansCJK` (CFF) yields **13.7 MB**. The image therefore adds `fonts-droid-fallback` (Apache-2.0, glyf) for the PDF skill; the skill also inspects the font header itself to tell glyf from CFF and warns about the size cost of CFF. A custom font can be supplied via `AGUI_PDF_FONT` or the `fontPath` parameter (.ttf/.ttc supported, TTC faces are extracted automatically). Verified live: a 4-page Chinese PDF at **95 KB**, with a subset font `/BIIYBT+Droid Sans Fallback` and extractable, searchable Chinese text.
- **The orchestration delivery loop now covers all four formats**: Excel / spreadsheet / report → names `xlsx_book`; PDF → names `pdf_doc` (alongside the existing PPT / Word cases), with matching updates to the built-in `org_design` skill's reuse rules and to the hollow-delivery-skill warning.
- **Delivery “thickness” is measured per format (fix)**: previously the fields reported by different skills were pooled into a single maximum, but their meanings differ (pptx reports `slides`, xlsx reports `rows`, docx/pdf report `blocks`) — so an xlsx result, having no `blocks`, looked like “0 content” and triggered a pointless retry. The metric is now chosen by deliverable type: slides for pptx (≥3), data rows for xlsx (≥3), content blocks for docx / pdf (≥5).
- **Input-shape validation for Excel and PDF**: mirroring the docx `sections` and pptx `slides` checks, the gateway now validates xlsx `sheets` (each must carry both a column definition and data rows) and pdf `blocks` (each block needs a `type`, or supply `markdown` directly), which blocks an “empty table with only a header” or a “cover-only PDF” and feeds the correct shape back so the model can self-correct.
- **PDFsharp font-resolver conflict (process-wide global, fixed)**: PDFsharp allows its font resolver to be set only once, and once anything has rendered PDF text the slot is **permanently frozen**. Skills all run in the same web process, so a single self-authored dotnet skill that renders PDF text first would disable the built-in PDF skill too. The skill now tries to install its own resolver, and on failure attempts to reuse the installed one (which still works if that one can resolve a Chinese font); otherwise it returns an **actionable Chinese error** instead of an opaque exception.
- **A test leaked that global state (fixed)**: the attachment-storage test used to render a PDF fixture with PDFsharp, freezing the resolver slot inside the test process and making the PDF skill tests fail in bulk when the whole suite ran (16 of them, measured). It now writes a minimal PDF fixture by hand (base-14 Helvetica, no font resolver needed) — that test only cares about extracting text from a PDF and never needed this global; all 1129 tests are green again.

---

# AG-UI 群聊桌面版 1.0.124 发布说明
# AG-UI Group Chat Desktop 1.0.124 Release Notes

**版本说明**：1.0.124 为 Windows 桌面版本。在 1.0.123 基础上新增**内置 PowerPoint 生成技能**，并让**一键编排 / 组织架构构建师自动适配**它；同时修正了交付物判定的优先级错误。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.124 is a Windows desktop release. On top of 1.0.123 it adds a **built-in PowerPoint generation skill** and makes **one-click orchestration / the org architect adapt to it automatically**, plus a fix to the deliverable-detection precedence. Web and desktop share the same Hub / gateway / frontend.

## 内置 PPT 生成 + 编排适配（1.0.124 核心）
# Built-in PPT generation + orchestration adaptation (1.0.124 headline)

中文：
- **内置 `pptx_deck` 演示文稿技能（开箱即用）**：与内置 Word 技能同一条 Roslyn 链路，**纯 .NET（DocumentFormat.OpenXml）**，不依赖 Node / Python / 外部解释器。按页型组织——封面 / 目录 / 章节分隔 / 内容 / 两栏 / 表格 / 指标卡 / 引言 / 图片 / 图表 / 小结 / 结束页，任意页可带演讲者备注；六套主题预设或自定义配色与字体、16:9 宽屏、除封面外每页页码徽标。图表（柱 / 折线 / 饼 / 环形）**渲成 PNG 再按图片嵌入**而非发 ChartPart（沿用内置 Word 技能的决策，避开 DrawingML 图表兼容性风险）。产物带 `produce_file` 标记 → 网关自动登记为附件，**前端可直接下载**。可由 `Agents:BuiltinPptxSkills` 关闭播种。
- **编排自动适配内置产出技能**：编排提示词与「可复用技能」小节现在都点名——要 PPT / 演示文稿就**直接引用 `pptx_deck`**，要 Word 就引 `docx_report`/`docx_gongwen`/`docx_notice`，并明令**不要**为这些已有能力另造只能写字的 prompt 空壳；内置 `org_design` 技能的复用规则同步更新。预览侧 `DetectDeliveryGap` 在点名该格式时也会**直接给出内置技能名**，模型与用户都有可引用的对象。
- **交付物判定改为「明确格式词优先、中文泛称靠后」（重要修正）**：此前先判中文泛称「表格」，于是「做份 PPT，含一张对比表格」被判成 **Excel 交付** —— 找不到 xlsx 技能后兜底**静默放弃**，用户拿不到文件。现明确格式词（pptx / xlsx / docx / pdf）优先，中文泛称（「文档」→ docx、「表格」→ xlsx）作兜底，且兜底内部仍保持「文档」优先，含糊表述（如「写份文档，里面含一张表格」）的判定与历史一致。
- **治理旧代码**：新造交付技能与内置技能同 id 时视为重复（应改为直接引用），该规则原本只对 docx 表述，现同时覆盖 pptx。

English:
- **Built-in `pptx_deck` presentation skill (works out of the box)**: same Roslyn path and **pure .NET (DocumentFormat.OpenXml)** as the built-in Word skills — no Node, Python or external interpreter. Organised by slide type: cover, toc, section, content, two-column, table, KPI, quote, image, chart, summary and end, each optionally carrying speaker notes; six theme presets or explicit colors and fonts, 16:9 widescreen, a page badge on every page but the cover. Charts (bar / line / pie / doughnut) are **rendered to PNG and embedded as pictures** rather than emitted as ChartParts — the same call the Word skills make to avoid DrawingML chart compatibility risk. The output carries the `produce_file` marker, so the gateway registers it as an attachment that **downloads straight from the frontend**. Can be turned off with `Agents:BuiltinPptxSkills`.
- **Orchestration adapts to the built-in producers**: both the orchestration prompt and its reusable-skills section now name them — for a deck, **reference `pptx_deck` directly**; for Word, `docx_report` / `docx_gongwen` / `docx_notice` — and explicitly forbid inventing text-only prompt shells for capabilities that already exist. The built-in `org_design` skill's reuse rules were updated to match, and the preview's `DetectDeliveryGap` **names the built-in skill** so both the model and the user have something concrete to reference.
- **Deliverable detection now prefers explicit format words over generic Chinese nouns (important fix)**: it tested the loose noun “表格” first, so “make a PPT with a comparison table” was classified as an **Excel** deliverable — the fallback then found no spreadsheet skill and **gave up silently**, leaving the user with no file. Explicit format words (pptx / xlsx / docx / pdf) now win, with generic nouns (“文档” → docx, “表格” → xlsx) as the fallback; inside that fallback “文档” still comes first so ambiguous phrasing such as “write a document containing a table” resolves exactly as before.
- **Old rule brought in line**: a newly invented delivery skill sharing an id with a built-in one is treated as a duplicate that should have been a direct reference; that rule previously only said docx, and now covers pptx too.

---

# AG-UI 群聊桌面版 1.0.123 发布说明
# AG-UI Group Chat Desktop 1.0.123 Release Notes

**版本说明**：1.0.123 为 Windows 桌面版本。在 1.0.122 基础上：**用户分组（按组细粒度授权）正式进入桌面安装包**，并修复了一轮严格代码审核发现的问题（弹窗层级导致操作卡死、用户分组创建时间持久化丢失、技能目标准入判定顺序、单聊复用路径绕过准入、白名单 id 无校验）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.123 is a Windows desktop release. On top of 1.0.122 it brings **user groups (group-based fine-grained authorization) into the desktop installer** and fixes a round of issues found in a strict code review (a stacking bug that hung dialogs, loss of a group's creation timestamp across restarts, the skill-target access check ordering, the direct-chat reuse path bypassing admission, and unvalidated allowlist ids). Web and desktop share the same Hub / gateway / frontend.

## 代码审核修复（1.0.123 核心）
# Code-review fixes (1.0.123 headline)

中文：
- **弹窗不再被遮住而卡死（回归修复）**：通用确认框 `uiConfirm` / 输入框 `uiPrompt` 没有自己的层级，从「知识库管理」里删除文档时确认框会被画到该弹窗**底下** —— 用户看不到、`await` 也永不返回，界面直接卡住。现为确认框单独指定层级（高于二级弹窗、低于 toast）。
- **用户分组的创建时间不再丢失**：该字段被标了 `[JsonIgnore]`，永远不会落盘，重启后一律归零；三种持久化模式（内存 / PostgreSQL / Redis）均受影响。已修正，并把原先“看似在测、实则测不到”的持久化用例改为走真实生产路径且断言该字段。
- **技能目标准入判定顺序**：访问策略把管理员放行排在“技能目标永不直接可见”之前，对技能子代理会返回“可访问”。虽然现有调用点都另外挡了一层、尚无可实际触发的越权，但这是安全原语里的 fail-open 隐患，已调整顺序并加回归测试。
- **单聊复用路径补上准入**：此前只在“创建”会话时校验用户组白名单，复用已存在会话时直接返回 —— 用户被移出白名单组后仍能继续单聊。现将准入校验前置，创建与复用两条路径同权。**行为变化**：被移出白名单组的用户，其已有单聊会立即失效。
- **白名单 id 增加校验**：数字员工 / 技能的“允许访问的用户组”此前接受任意字符串，写错一个 id（或漏 `ug_` 前缀）会让该资源**静默对所有人不可见**且保存不报错；现在保存即拦下。
- **目录可见性与单聊准入统一为同一份策略**：数字员工目录曾自行重写一遍可见性规则（与准入策略是两份实现，已经开始漂移）；现已统一调用同一策略，并加组合测试把两者钉死。
- **删除分组前给出影响提示**：新增影响预检（该分组被哪些数字员工 / 技能引用），删除确认会列出具体资源与成员数，避免运维误删后有人突然失去访问。

English:
- **Dialogs no longer render behind the caller and hang**: the generic confirm/prompt dialog had no z-index of its own, so confirming a document deletion inside the knowledge-base manager drew it underneath that dialog — invisible, and its promise never resolved, freezing the UI. It now has an explicit layer above second-level dialogs and below the toast.
- **A user group's creation timestamp survives restarts**: the field was marked `[JsonIgnore]`, so it was never persisted and always restored as zero, in memory, PostgreSQL and Redis alike. Fixed, and the persistence test that only appeared to cover it now drives the real production path and asserts the field.
- **Skill-target access check ordering**: the policy tested the admin bypass before the “skill targets are never directly visible” rule, answering true for a skill sub-agent. Every current caller also guards separately, so nothing was exploitable, but it was a fail-open ordering hazard in a security primitive; reordered with a regression test.
- **Direct chat validates on the reuse path too**: admission was only checked when creating the conversation, so a user dropped from an allowlist group kept it by reopening the existing one. The check now precedes both paths. **Behaviour change**: an existing direct chat stops working as soon as its user is removed from the group.
- **Allowlist ids are validated**: the “allowed user groups” list on employees and skills accepted any string, so a single typo could silently hide a resource from everyone while saving without error. Invalid ids are now rejected on save.
- **Catalog visibility and direct-chat admission share one policy**: the employee catalog re-implemented the visibility rule instead of using the shared policy (two implementations that had already drifted). It now delegates to the same policy, pinned by a combinatorial test.
- **Group deletion reports its impact**: a new impact check lists which employees and skills reference a group, so the delete confirmation shows exactly what and how many users would lose access before it happens.

---

# AG-UI 群聊桌面版 1.0.122 发布说明
# AG-UI Group Chat Desktop 1.0.122 Release Notes

**版本说明**：1.0.122 为当前 Windows 桌面点版本（已构建 Windows 1.0.122 MSI），在 1.0.121 基础之上修正了编排/长稿稳定性的三个问题，全部 Web 已推送（Web 与桌面共用同一套 Hub/网关/前端）。
**Version note**: 1.0.122 is the current Windows desktop point release (a Windows 1.0.122 MSI was built). On top of 1.0.121 it fixes three issues affecting orchestration & long-form stability, all already on the Web build (Web and desktop share the same Hub / gateway / frontend).

## 编排长稿稳定性修复（1.0.122 核心）
# Orchestration & long-form stability fixes (1.0.122 headline)

中文：
- **模型网络超时放大（不再“跑到一半被掐”）**：底层模型客户端默认仅在 100 秒内响应，思考模型（deepseek-reasoner）长思考/长稿生成时请求未完成就被中断，表现为“编排/指派路由进行中取消、出不来稿”。已把单次请求网络超时提到 15 分钟（真正的一次运行时限仍由执行配置的流式超时 `streamTimeoutMinutes` 控制）。
- **多附件问答可用（不再“只认第一个”）**：一条消息可带多个附件，模型上下文现在会给「附件清单」（每个带 attachmentId + 注入状态）；可提取文本附件按“每文件 12K + 总 60K”逐文件注入（此前共享 24K 且先到先得，首个大文件会挤掉后续）；`read_attachment` 支持分段续读（startIndex/maxChars），长文档可被完整引用；历史追问回放预算同步放大到 24K。
- **编排回复绝不空白（不再“只有计划卡、没有字”）**：综合答复拿不到可用文本时，回退到已收集的中间结果整理成一段可见回答，或给明确提示；递归补查每步都保证返回非空；取消/异常中断的收尾也会在上一条“正文为空”的消息上补一句说明。任何情况下都不再留下纯空白回复。

English:
- **Model network timeout enlarged**: the underlying model client only waited 100s, so a reasoning model (deepseek-reasoner) still thinking when long-form output is requested got cut off before the request finished — showing up as “orchestration / assignment routing gets cancelled, no draft”. The per-request network timeout is now 15 minutes (the true per-run limit is still governed by the execution stream timeout `streamTimeoutMinutes`).
- **Multi-attachment Q&A works**: a message may carry multiple files and the model context now gets an “attachment inventory” (each with its own attachmentId + inlined status). Extractable text files are inlined per file (12K each within a 60K total, replacing a shared 24K served first-come-first-served that let the first big file crowd out the rest); `read_attachment` supports paginated reads (`startIndex`/`maxChars`) so long documents can be fully referenced; the historical follow-up inline budget is raised to 24K.
- **Orchestration replies never arrive blank**: when the synthesized answer yields no usable text it now falls back to a readable summary of the collected intermediate results or a clear notice; the recursive gathering returns a non-empty value at every boundary; and cancelled / error teardowns stamp a brief note rather than leaving an empty-content bubble.

---

## 用户分组与按组授权（细粒度访问控制）（已包含于 1.0.123 桌面安装包）
# User groups & group-based authorization (fine-grained access) (included in the 1.0.123 desktop installer)

中文：
- **用户分组（用户组 / 组织单元）**：管理员控制台新增「用户分组」页签（`GET/POST/DELETE /ag-ui/usergroups`，另有 `/mine` 供登录用户查自己所属组）。把**平台账号**编组（与“群聊知聚”是不同概念），用于按组授权。持久化于扩展区 `userGroups`，重启不丢。
- **数字员工按组授权**：`AgentDefinition.AllowedGroupIds`——在数字员工编辑表单新增「🔐 允许访问的用户组」多选。未配置 = 全员可用（向后兼容）；配置后仅命中白名单的用户组成员可**看到 / 单聊 / 拉入/建群**（创建者与系统管理员始终放行）。服务端在目录列表、单聊、建群、加成员四个关口统一拦截（403 `AGENT_PERMISSION_DENIED`）。
- **技能库按组授权**：`AgentSkillDefinition.AllowedUserGroupIds`——受限技能对非归属者/非管理员隐藏，挂载到数字员工时非授权用户拒绝（403 `SKILL_PERMISSION_DENIED`）。知识库已有“群级共享”语义，与本机制互补。
- 详见 `docs/RBAC.md` §6。

English:
- **User groups (organizational units)**: a new “User Groups” tab in the admin console (`GET/POST/DELETE /ag-ui/usergroups`, plus `/mine` for a signed-in user's own groups) to organize **platform accounts** (a different concept from chat groups) for group-based authorization. Persisted in the `userGroups` section across restarts.
- **Group-based agent authorization**: `AgentDefinition.AllowedGroupIds` — the agent edit form gains an “🔐 Allowed user groups” multi-select. Empty = open to everyone (backward compatible); when set, only members of the listed groups may **see / direct-chat / add to a circle** (creator and system admins always pass). Enforced server-side at the catalog list, direct chat, group create and member add (403 `AGENT_PERMISSION_DENIED`).
- **Group-based skill authorization**: `AgentSkillDefinition.AllowedUserGroupIds` — restricted skills are hidden from non-owners/admins and cannot be mounted onto an agent by unauthorized users (403 `SKILL_PERMISSION_DENIED`). Knowledge bases already model group-based sharing, complementing this mechanism.
- Details in `docs/RBAC.md` §6.

---

# AG-UI 群聊桌面版 1.0.121 发布说明
# AG-UI Group Chat Desktop 1.0.121 Release Notes

**版本说明**：1.0.120 之后的上一桌面包为 1.0.121（已构建 Windows 1.0.121 MSI，含记忆拟人类型 / 组织构建自动配记忆人格 / 组织连接自动成对 / 记忆管理按角色分层 / 企业合规 / 单聊等等）。1.0.120 为再往上一版（单聊 kind=direct、实时会话吊销、SDK 上行串行化、200MB 上传/导入）。以下各节为此前的功能增量。
**Version note**: The previous desktop package after 1.0.120 was 1.0.121 (a Windows 1.0.121 MSI was built, adding memory personality types / org auto-assigned memory personas / auto-paired assignment+escalation links / role-tiered memory scope / enterprise compliance / direct chats etc.); before that 1.0.120 (direct chats `kind=direct`, instant session teardown, SDK send serialization, 200MB upload/import). The sections below are the earlier feature increments.

## 组织构建连接自动成对（指派 + 提升）（已随 1.0.121 桌面安装包发布）
# Org building auto-pairs assignment + escalation links (shipped in the 1.0.121 desktop installer)

中文：
- **组织构建连接自动成对（指派 + 提升）**：当编排方案 / 待落库最终稿只填了向上「问题提升」连接（`escalationAgentId`）、向下任务指派名册（`assignmentIds`）为空或不全时，系统会在**生成解析**（`AgentOrchestrator.Parse` → `InferAssignments`）与**共享落库引擎**（`OrgApplyEngine.EnsureAssignmentsForLeaders`）两处，自动把直接提升到该主管的下属并入其 `assignmentIds`——去重、保序、只增不改；生成器提示与内置 `org_design` 技能正文也已明确要求成对连接。净效果：无论经网页一键编排、内置组织架构构建师（`org_plan_draft`）还是 `org_commit` 建出的团队，都同时具备「主管向下指派 + 下属向上提升」双向连接，不会退化成单向提升链。**库中已有旧组织不回写**——重新生成 / 重新 apply 即生效（解析推断 / 并入既有名册 / 落库兜底的单元与集成测试已存在）。

English:
- **Org building auto-pairs assignment + escalation links**: when an orchestration plan / committed final draft only sets upward problem-escalation links (`escalationAgentId`) and leaves the downward task-assignment rosters (`assignmentIds`) empty or partial, the system auto-merges each leader’s direct subordinates (the agents that escalate to it) into that leader’s `assignmentIds` — deduplicated, order-preserving, additive-only — at two stages: generation parse (`AgentOrchestrator.Parse` → `InferAssignments`) and the shared persist engine (`OrgApplyEngine.EnsureAssignmentsForLeaders`). The generator prompt and the built-in `org_design` skill body now explicitly instruct paired connections. Net effect: any team created via one-click orchestration, the built-in org architect (`org_plan_draft`) or `org_commit` lands with both directions (managers assign downward AND subordinates escalate upward) and never degrades into a one-way escalation chain. Legacy orgs already in the catalog are untouched — regenerate / re-apply to benefit. Related unit/integration tests exist (parse inference, merge into existing rosters, apply-stage backstop).

---

## 组织构建自动按岗位适配「记忆拟人类型」（已随 1.0.121 桌面安装包发布）
# Org building auto-assigns per-role “memory personality” (shipped in the 1.0.121 desktop installer)

中文：
- **组织构建自动适配记忆拟人类型**：网页「一键组织编排」与内置组织架构构建师（`org_plan_draft`）产稿时，会按每个岗位的实际职责让模型挑选记忆拟人 preset（`memoryProfile`：broad/deep/slowToLearn/cueDependent/fastForgetting），随方案 JSON 一起预览，并在 apply / `org_commit` 落库后自动写入新创建的数字员工——统筹主管通常是 `deep` 深记型、高频客服/一线是 `broad` 广记型、需回想客户过往的顾问/售后是 `cueDependent` 存得住想不起型、值班/速查岗是 `fastForgetting`、重复套路岗是 `slowToLearn`。容错解析：模型写 preset key 字符串或 `{"memoryType":…}` 对象均可；未知/缺失不报错，缺失按岗位称呼/职责关键词启发式兜底，仍无把握则保持 `null`=沿用全局召回。**向后兼容**：历史方案/手写最终稿 JSON 没有该字段 → 解析为 null、原样落库。预览逐岗位回显「记忆: 🧠…」标签便于落库前核对。详见 `docs/memory-personality.md` §4.4。

English:
- **Org building now auto-assigns memory personalities**: one-click orchestration and the built-in org architect (`org_plan_draft`) ask the model to pick a memory-persona preset per role (`memoryProfile`: broad/deep/slowToLearn/cueDependent/fastForgetting) that is shown in the preview and written into each newly created employee on apply / `org_commit` — e.g. leads/deep, high-volume frontline/broad, account-success & after-sales/cueDependent, duty & quick-lookup/fastForgetting, repetitive routine/slowToLearn. Parsing is tolerant (preset-key string or `{"memoryType":…}` object); unknown/missing values never abort the build — missing falls back to role-keyword heuristics, otherwise stays `null` (platform-global recall, backward compatible; legacy plans / hand-written JSON without the field still parse and apply exactly as before). Preview rows show a “记忆:” tag per employee for verification before committing. See `docs/memory-personality.md` §4.4.

---

## 数字员工「记忆拟人类型」（已随 1.0.121 桌面安装包发布）
# Digital-employee memory personality types (shipped in the 1.0.121 desktop installer)

中文：
- **记忆拟人类型（按类型召回）**：编辑数字员工 →「记忆与权限」新增「🧠 记忆类型（拟人召回）」区——五档预设（广记型 / 深记型 / 难录入型 / 存得住想不起型 / 快速遗忘型）+ 口吻模式（平实引述 / 先概括要点）+ 人设口吻 + 高级微调（群/个人 TopK 与相似度阈值）。回复前按该员工类型执行抽取：调取群记忆 / 个人记忆的条数与阈值不同（经检索层真正生效），存得住想不起型在用户给回忆提示（「记得吗 / 上次 / 之前」）时临时放宽提取，快速遗忘型默认只见最近 21 天的记忆（不删落库数据）；注入记忆时附一句与类型相符的“召回口吻”软性说明（广记型提示用「我记得好像是…」式谨慎措辞，不凭空补全）。写入侧仅对本员工本人发言微调：<b>深记型</b>自动刻为「重要」记忆、<b>快速遗忘型</b>在开启自动遗忘时保留更短（其余写入无差别）。未配置 = 沿用平台全局，完全向后兼容。记忆特性专项说明：`docs/memory-personality.md`。

English:
- **Memory personality types (type-driven recall)**: editing an employee → “Memory & Permissions” now has a “🧠 Memory type” section — five presets (Broad / Deep / Slow-to-learn / Stored-but-cue-dependent / Fast-forgetting) plus a recall/digest speaking style, an optional persona tone, and advanced tuning (group/personal TopK and similarity thresholds). Recall runs per type before every reply: hit counts and thresholds differ for group/personal memory (enforced at the store layer); the cue-dependent type temporarily widens retrieval when the user gives recall cues (“remember? / last time / before”), and fast-forgetting only sees memories from the last 21 days by default (stored data is never deleted); a type-consistent soft “recall tone” note accompanies injected memories (broad types hedge with “I think it was…” instead of inventing). On the write side, only the employee's own posts are mildly typed: <b>Deep</b> posts are auto-marked “Important”, <b>Fast-forgetting</b> posts get shorter retention when auto-forget is on (everything else writes identically). Unset means platform-global behavior — fully backward compatible. Dedicated memory-feature spec: `docs/memory-personality.md`.

---

## 记忆管理按角色分层（已随 1.0.121 桌面安装包发布）
# Role-tiered memory-management scope (shipped in the 1.0.121 desktop installer)

中文：
- **记忆管理范围按角色划分**：平台管理员（Admin / SuperAdmin）跨全部知聚查看 / 治理任意记忆；知聚<b>群主 / 群管理员</b>可在其知聚内整群治理（对他人记忆分级 / 删除、整群遗忘、向该群导入记忆）；<b>普通成员</b>仅可查看所在知聚记忆，分级 / 删除 / 遗忘只作用于本人发言；Operator（只读运维）不因平台角色获得记忆治理特权。记忆列表逐条回传 `canManage`、`/memory/groups` 回传每群 `canManageAll`，记忆管理界面按所选知聚提示当前遗忘范围（整群 / 仅本人），并修复了非管理员“全部知聚”视图可能越权枚举其它知聚记忆的缺口（数据范围改为仅自己所在群）。

English:
- **Memory-management scope is now role-tiered**: platform admins (Admin / SuperAdmin) may view / govern memory across every group; a circle's <b>owner / admin</b> may govern the whole circle they manage (tier / delete others' memories, group-wide forgetting, importing into that group); <b>ordinary members</b> may only view memory of circles they belong to and may tier / delete / forget only their own posts; Operator (read-only ops) gains no memory-governance privilege. Each listed memory returns `canManage` and `/memory/groups` returns per-circle `canManageAll`; the memory UI hints the current forget scope per selected circle (whole circle / own posts only), and a former gap was closed where non-admins using the “all circles” view could enumerate other circles' memory (data scope is now restricted to their own circles).

---

## 企业合规（已随 1.0.121 桌面安装包发布）
# Enterprise compliance (shipped in the 1.0.121 desktop installer)

中文：
- **账号注销与数据擦除**：「我的资料 → 危险操作 → 注销账户」（需密码确认，`DELETE /ag-ui/account`）与管理员「用户管理 → 彻底删除」（`DELETE /ag-ui/admin/users/{userId}`）。删除语义：创建的知聚在存在其他用户成员时**转让群主后自行退群**、否则**解散**；加入的他人知聚自行退群；其发言**语义记忆**与**个人知识库**物理删除；**现存群中的发言匿名化**（正文 / 附件 / 提及 / 推理 / 链 / 计划清空、昵称改「已注销用户」，保留消息行与时间线）；**其创建的数字员工 / 技能孤儿清理**（不再被现存群引用的连同触发注册删除，仍被他人知聚引用的保留）；账号行 + 全部会话 + TOTP 清除（四种存储实现 `IUserStore.RemoveUser`）。防呆：最后一名超级管理员不可删；管理员不可经管理接口删除自己。
- **我的数据导出（可携权）**：资料弹窗新增「⬇ 导出我的数据」（`GET /ag-ui/account/export`）——任意登录用户可把账号资料、知聚清单、本人发言（含附件元信息）、本人语义记忆、个人知识库元数据、本人创建的数字员工 / 技能完整定义导成一个 JSON 下载，供注销前留存 / 迁移。**注销弹窗带「先导出」引导**：距上次导出超过 30 天（以服务端审计为准，跨设备一致）时提示并一键先导出。
- **孤儿定义运营盘点**：管理员控制台新增「孤儿盘点」页签（`GET /ag-ui/admin/orphans`）——列出 Owner 账号已注销的数字员工 / 技能及其引用上下文；未被引用可直接删除，仍被引用的可**接管**到自己名下再常规管理（删除有安全闸，防悬空知聚）。
- **审计日志检索 + CSV 导出 + 独立表持久化**：`AuditLogService` 支持操作者 / 操作名 / 目标 ID / 时间范围过滤；`GET /ag-ui/admin/audit`（过滤查询）、`/ag-ui/admin/audit.csv`（RFC 4180 + UTF-8 BOM 全量导出）；管理员控制台「审计日志」页签；**数据库模式（PG/MySQL/SQLite）为独立表 `agui_audit`**（写入 / 检索 / 导出 / 保留裁剪全走 SQL，保留策略 5000 条，重启保留；memory / Redis 模式为持久化扩展区）。
- **消息 👍/👎 按钮改版**：改用与复制 / 重新回答 / 撤回等头部按钮同款描边 SVG 图标（不再用 emoji 字符），并与其他头部按钮一致的「悬停消息才显示」，已评价态保留 👍绿 / 👎红高亮。
- **全局智能检索**：顶栏「🔎 全局搜索」跨知聚一次检索消息 / 语义记忆 / 知识库（`GET /ag-ui/search?q=`），结果严格按可见性过滤（成员 / 客服 staff / 顾客参与者各见其当见，不泄露他人会话与定向消息），点击消息 / 记忆可跳转到所在知聚。
- 详见 `docs/enterprise-compliance.md`。

English:
- **Account deletion & data erasure**: self-service “Delete account” in profile (password-confirmed, `DELETE /ag-ui/account`) and admin “permanently delete” in user management (`DELETE /ag-ui/admin/users/{userId}`). Semantics: groups the user owns are transferred (then they leave) when other user members exist, otherwise disbanded; groups they merely joined are left; their message **memories** and **personal knowledge bases** are physically erased; their **messages in surviving groups are anonymized** (content / attachments / mentions / reasoning / chains / plans cleared, nickname becomes “deleted user”, message rows and the timeline stay); their **owned employee / skill definitions are orphan-cleaned** (unreferenced ones are removed together with trigger registrations; ones still referenced by other people’s groups are kept); the account row, all sessions and TOTP are removed (four `IUserStore.RemoveUser` storage backends). Guards: the last super admin can never be deleted; admins cannot delete themselves via the admin API.
- **My-data export (portability)**: a new “⬇ Export my data” action in the profile (`GET /ag-ui/account/export`) lets any signed-in user download a JSON file with their profile, group memberships, their own messages (incl. attachment metadata), their semantic memories, knowledge-base metadata, and full definitions of the employees / skills they created — to keep or migrate before deletion. The **delete-account dialog now nudges you to export first** when the last export is more than 30 days old (server-side audit, consistent across devices) with a one-click export.
- **Orphan-definition inventory**: a new Admin Console “Orphan Inventory” tab (`GET /ag-ui/admin/orphans`) lists employees/skills whose owner account is gone, with their reference context; unreferenced ones can be deleted, referenced ones can be **adopted** into the current admin and managed normally (deletion is guarded to prevent orphaning groups).
- **Audit log search + CSV export + dedicated-table persistence**: `AuditLogService` filters by actor / action / targetId / time range; `GET /ag-ui/admin/audit` (filtered query), `/ag-ui/admin/audit.csv` (RFC 4180 + UTF-8 BOM full export); Admin Console “Audit Log” tab; **database storage (PG/MySQL/SQLite) uses a dedicated `agui_audit` table** (SQL-backed write/search/export/retention pruning capped at 5,000 and surviving restarts; memory/Redis fall back to the persisted section).
- **Message 👍/👎 restyle**: the like/dislike buttons now use the same outline SVG icon set as the copy / regenerate / recall head buttons (no longer raw emoji glyphs), keep the 👍 green / 👎 red selected states, and share the same hover-to-show behaviour.
- **Global intelligent search**: the top-bar “🔎 Global Search” searches messages / semantic memories / knowledge bases across all your groups at once (`GET /ag-ui/search?q=`), with strict visibility scoping (members, support-circle staff and customer participants each see only what they may — no leaking of other customers’ sessions or directed messages); clicking a message / memory jumps into its group.
- Details in `docs/enterprise-compliance.md`.

---

## 聊天多附件问答增强（已随 1.0.121 桌面安装包发布）
# Multi-attachment Q&A enhancement for chat (shipped in the 1.0.121 desktop installer)

中文：
- **聊天多附件问答增强（让每个附件都能被引用）**：向数字员工提问时一条消息可带多达 9 个附件；模型上下文现在会收到一份“附件清单”——每个附件带编号与 `attachmentId`、并注明“已注入正文 / 仅元数据”，可对任意一个单独调用 `read_attachment` 读取。可提取文本的附件（txt/md/code/pdf/docx/xlsx/pptx）按“每文件 12K 字符 + 总预算 60K”逐文件注入（此前为共享 24K 且先到先得，首个大文件会挤掉后续附件，表现成“只认第一个附件”）；`read_attachment` 工具支持**分段续读**（`startIndex`/`maxChars`，返回结束偏移供下一次继续），长文档与未自动注入的附件都能被完整引用；历史追问自动回放的文档正文预算同步放大到 24K。图片走独立视觉通道（每轮上限不变）。

English:
- **Multi-attachment Q&A now references every file**: a single chat message may carry up to 9 attachments; the model context now gets an “attachment inventory” — every file numbered with its own `attachmentId` and its status (inlined vs. metadata-only), and any file can be read on demand via `read_attachment`. Extractable text files (txt/md/code/pdf/docx/xlsx/pptx) are inlined per file (12K chars each within a 60K total, instead of a shared 24K served first-come-first-served that let the first big file crowd out later attachments and look like “only the first file was used”). `read_attachment` now supports **paginated reads** (`startIndex` / `maxChars`, returning the end offset for the next call), so long documents and non-inlined attachments can be fully referenced; the historical follow-up inline budget is also raised to 24K. Images keep their separate vision channel (per-turn cap unchanged).

---

## 新增（中文）
- **数字员工单聊（kind=direct）**：数字员工管理列表每行新增「💬 私聊」——对该数字员工点一下即开始/复用与之的一对一**私有双人群**（`POST /ag-ui/agents/direct`，幂等）；不同用户与同一数字员工的单聊各自独立、互不可见（会话隔离）；单聊默认私密（`isPrivate=true`，语义记忆仅在本私群可检索）。在单聊里发**普通（未 @）消息即视为对其直达触发**，无需手动 @。Web 与桌面一致可用（Playwright 用例 `tools/direct-chat-flow.mjs` 已全绿）。
- **实时会话吊销/禁用/改密即时断线**：登出、修改密码、管理员禁用或重置密码会**立即终止**该账号已建立的 WebSocket / SSE 实时连接（服务端主动关闭），不再等到断线才失效；会话在服务端吊销即刻生效。
- **SDK 实时通道健壮性**：断连回调在异常断连时只触发一次（此前 WS/SSE 会先带异常、再带 null 触发两次）；上行 WebSocket 发送真正串行化（SemaphoreSlim 覆盖发送本身），避免并发发送竞态。
- **上传 / 导入大包放行**：Kestrel 请求体上限与 FormOptions 同步放宽到 200MB（此前仅放宽表单解析，Kestrel 默认 30MB 仍会先拦大包），`/import`、`/upload` 大包可用。
- **组织架构构建师走“一键式”结构化出稿**：挂 `org_design` 的组织角色（如 org_architect）多挂载一个 `org_plan_draft` 工具，复用与网页「一键组织编排」同一生成引擎（`AgentOrchestrator`），一次产出「岗位 + 各岗 skillIds + 技能(kind 按 shell/http/prompt/dotnet、executionLocation 按 server/client) + 岗位连接」的整支成稿 JSON——避免整支被手写成只见 pure prompt 的软稿。只出稿不落库，须用户明确认可后再经既有 `org_commit`（仅管理员）落库。
- **协调 JSON 的 answer 真实换行也剥得干净**：模型在 `answer` 里放真实换行/未转义内容导致整包 `JsonDocument.Parse` 失败时，收尾（`UnwrapCoordinationAnswer` / 递归补查解析）会走容错提取把正文剥出来，不再把 `{"needsMore":…,"answer":…}` 整段 JSON 泄漏/截断给用户；真实换行保留成正文排版。
- **Client 技能绝不落到服务端当 bash 跑**：服务端执行器对 `ExecutionLocation=Client` 的技能一律拒跑并给指引（应经本机桥/该用户机器执行）；明显 Windows PowerShell 正文在非 Windows 宿主（Docker/Linux 服务器、非 Windows 本机宿主）直接报“需 PowerShell 环境”，而不是出现 `Not running in PowerShell / command not found / 退出码2` 这类误导性假报错。Windows 桌面自托管的 PowerShell 执行路径不受影响。
- **执行配置可在线热改（新增“执行阶段”角色级开关同理）**：管理员控制台新增「执行参数」页（`/ag-ui/admin/execution`），可在线调平台的时序/重试/TTL、执行顺序与阶段开关并一键「恢复默认」（`/defaults`），保存即对后续调用生效并经 `executionRuntime` 持久化、重启恢复；单个数字员工可在「编辑 → 执行阶段」为自身关闭桥接/交接/组织路由（`DisableBridge/DisableRelay/DisableOrgRoute`）。模型装配、记忆、协调计划总开关、网络安全收敛项与 RBAC 门槛仍属需改 appsettings/重启或固定不变——边界见 `docs/execution-configuration.md`。
- **普通编辑不再误清交接/流水线**：因角色编辑 PUT 为整表替换，普通编辑页现在会沿用在“一键编排/组织”等处配置的整轮交接（`RelayToAgentId`）、编排流水线（`Pipeline`）与差异化审批名单（`RequireApprovalToolNames`），避免随手保存时被静默清空。

## New (English)
- **Digital-employee direct chat (`kind=direct`)**: each digital employee row in the management list gains a “💬 chat” action — one click starts/reuses a one-to-one **private two-member group** with that employee (`POST /ag-ui/agents/direct`, idempotent); different users chatting with the same employee get isolated, mutually invisible sessions; direct chats are private by default (`isPrivate=true`, semantic memory only retrievable inside that private group). Inside the chat, **a plain (un-@) message means “talk to it” and triggers it directly** — no manual at-mention. Works identically in Web and desktop (Playwright flow `tools/direct-chat-flow.mjs` is green).
- **Immediate realtime teardown on logout / disable / password reset**: logging out, changing the password, or an admin disabling/resetting an account now **terminates that account’s established WebSocket / SSE connections right away** (server-initiated close), instead of waiting for the next disconnect; session revocation takes effect immediately.
- **SDK realtime robustness**: `Disconnected` now fires exactly once on an abnormal close (WS/SSE previously fired with the exception and then with `null`); WebSocket sends are fully serialized (a semaphore covers the actual `SendAsync`), removing concurrent-send races.
- **Large upload/import bodies allowed**: the Kestrel request-body limit is raised to 200MB together with `FormOptions` (previously only the form parser was relaxed, so Kestrel’s 30MB default still rejected big bodies); big `/import` and `/upload` now work.
- **Org architect drafts a whole organization the one-shot way**: an org role mounted on `org_design` (e.g., org_architect) gains an `org_plan_draft` tool that reuses the same generator as the web one-click organization orchestration (`AgentOrchestrator`) — one structured pass yields the full team JSON (roles + per-role `skillIds` + skills where `kind` is picked shell/http/prompt/dotnet and `executionLocation` server/client + connections), instead of free-chat producing mostly pure-prompt drafts. It only drafts (no write); commit still goes through `org_commit` (admin-gated) after explicit user agreement.
- **Tolerant unwrap when the coordination JSON has real newlines in answer**: when the `answer` contains genuine line breaks / unescaped content so whole-object `JsonDocument.Parse` fails, final-reply wrappers (`UnwrapCoordinationAnswer` / recursive parse) fall back to extracting the answer text instead of leaking or truncating `{"needsMore":…,"answer":…}`; real newlines are kept as line breaks.
- **Client skills are never mis-run as server bash**: the server executors refuse `ExecutionLocation=Client` with clear guidance (run on the originating machine via NativeBridge/browser); obviously PowerShell bodies on a non-Windows host (Docker/Linux server, non-Windows self-host) return a clear “PowerShell environment required” instead of the misleading `Not running in PowerShell / command not found / exit code 2`. Windows desktop self-host PowerShell execution is unchanged.
- **Execution config now tunable at runtime (plus role-level stage toggles)**: new Admin Console “Execution Params” page (`/ag-ui/admin/execution`) lets you tune platform timeouts / retries / TTLs / stage switches and stage order online and restore defaults in one click (`/defaults`); saving applies immediately to later calls and persists via `executionRuntime`. A single employee can disable bridge / relay / org-routing for itself under “Edit → Execution stages” (`DisableBridge/DisableRelay/DisableOrgRoute`). Model/provider wiring, memory, the coordinator-plan master switch, network-security toggles and RBAC gates remain appsettings/restart or fixed — the boundary is documented in `docs/execution-configuration.md`.
- **Plain editing no longer wipes relay / pipeline / per-role approvals**: because role PUT is full-replace, the ordinary edit page now carries forward the relay (`RelayToAgentId`), pipeline (`Pipeline`) and per-role approval list (`RequireApprovalToolNames`) set elsewhere — preventing a silent clear on casual save.

---

# AG-UI 群聊桌面版 1.0.119 发布说明（上一版本）
# AG-UI Group Chat Desktop 1.0.119 Release Notes (previous point release)

## 新增（中文）
- **内部协调 JSON 的“整段二次剥壳”**：智能体消息收尾（EndAgentMessage）时把整段正文再归一一次——若它就是 {\"needsMore\":…,\"answer\":…} 协调 JSON，落库与广播前统一替换为用户可读的 answer；即便此前被拆成多段流式发给用户，也会在完结前被纠正。
- 承上：Client（本机）技能只在该“发起请求的用户所在机器”执行（A 口径）、本机无桥报“执行失败，没有安装桥”；试运行结果独立弹窗、结果真正落到当前机器等系列改进持续有效。

## New (English)
- **Second-pass cleanup of coordination JSON at message end**: at agent-message End, if the whole content is an internal {\"needsMore\":…,\"answer\":…} object, it is rewritten to its user-facing answer before store/broadcast — even if earlier streamed in fragments.
- Carried: Client (local) skills run only on the originating user’s machine (policy A); no bridge → “execution failed: no bridge installed”; trial results in a dialog and truly landing on the current machine.

**版本说明**：1.0.119 为上一 Windows 桌面点版本（当前为 1.0.121），主题为「内部协调 JSON 整段二次剥壳（收尾归一）」，并包含 Client 技能 A 口径系列修复；已构建 Windows 1.0.119 MSI。本机桥日志写系统临时目录。
**Version note**: 1.0.119 is a previous Windows desktop point release (current: 1.0.121), themed “second-pass whole-message cleanup of coordination JSON”, including the Client-skill policy-A series; a Windows 1.0.119 MSI was built. Native-bridge logs live in the system temp directory.

---

# AG-UI 群聊桌面版 1.0.118 发布说明（上一版本）
# AG-UI Group Chat Desktop 1.0.118 Release Notes (previous point release)

## 新增（中文）
- **Client（本机）技能只在该“发起请求的用户所在机器”执行**：接受 A 口径——凡 `ExecutionLocation=Client` 的技能只能跑在提问者消息所附的本机桥那台机器（`msg.BridgeClient`）；该用户本机无桥 / 未上报一律返回“执行失败，没有安装桥”，不再回落任何 agent/平台级桥。桌面版（宿主即本机）直接执行，无桥限制。
- **修复“内部协调 JSON 泄漏到聊天”**：把 `{"needsMore":…,…,"answer":…}` 这类协调 JSON 的剥壳铺到各最终答复出口；若模型把内部决策 JSON 原样当正文，只把 `answer` 给用户看。
- 附带：本机/client 试运行结果独立弹窗显示、结果真落到当前机器（系列 1.0.117 已梳理的改进继续有效）。

## New (English)
- **Client (local) skills execute only on the requesting user’s machine**: under policy A, any `ExecutionLocation=Client` skill runs only on the bridge of the originator’s message (`msg.BridgeClient`); if that user has no bridge / didn’t report one, it returns “execution failed: no bridge installed” and never falls back to an agent/platform bridge. Desktop (host == the user machine) executes directly without a bridge.
- **Stop internal coordination JSON from leaking into the chat**: unwrap `{"needsMore":…,…,"answer":…}` to just `answer` at every final-reply funnel; won’t affect normal prose.
- Carried over: trial-run results shown in a dedicated dialog and really landing on the current machine (1.0.117 improvements).

**版本说明**：1.0.118 是其上一桌面点版本，主题为「Client 技能只跑在发起用户那台机器（A 口径）+ 收尾剥离内部协调 JSON」；本机桥日志写系统临时目录。
**Version note**: 1.0.118 is the previous point release, themed “Client skills run only on the originating user’s machine (policy A) + strip internal coordination JSON from final replies”.

---

# AG-UI 群聊桌面版 1.0.117 发布说明（上一版本）
# AG-UI Group Chat Desktop 1.0.117 Release Notes (previous point release)

## 新增（中文）
- **本机(client)技能的“试运行”真正落到当前机器**：技能库试运行 `/run` 里，`ExecutionLocation=Client` 的 **dotnet(C#)** 与本机 **shell** 技能（如系统内置的 `服务状态`）会经<br>本机桥（优先按浏览器上报的 `clientId` 路由；否则落在平台级桥，即当前机器）在本机**真实编译/执行并回传结果**——不再误把 PowerShell 正文交给服务端 Linux bash 而报 `Get-Service: command not found`。（前置：本机已启动 `AguiGroupChat.NativeBridge`。）
- **`/run` 对任何 Client 技能都不再回落服务端**：凡 `ExecutionLocation=Client`、但没有“进程内可直接执行”形态（http / prompt 标成 client 等）的边缘情形，试运行返回明确指引（请到挂载它的数字员工对话、由其本机桥/前端在本机执行，或改 return server 后再试），而不再静默用服务端 Runner 跑一个“名为客户端技能”的东西。
- **技能库“试运行”结果展示升级为独立弹窗**：在技能库列表（或编辑表单）点 ▶ 后，结果出现在专门“试运行结果”弹窗（列表场景）或表单下方（编辑场景），不再写进不可见角落。
- **生成工具的“要不要思考”改为按任务自动判定**：结构化/格式型生成（技能生成、一键编排、试运行建议入参、技能自测与修复、图谱抽取、群名等）一律用常规对话模型，不因全局“思考模式”切换到 reasoner（reasoner 对严格 JSON/短 token 又慢又易空/超时）；复杂方案型任务在常规模型上“先想后给”（先简述取舍再给最终 JSON）。角色人设、指派引导等开放型生成仍随全局思考模式。
- **长任务并发保护与可“停止”**：技能生成 / 组织编排预览在跑时，其它可触发长生成的按钮置灰、防并发；只读生成任务提供“停止生成”（AbortController 取消，不写库，安全）；写库的“确认并创建(apply)”刻意不暴露停止以保数据完整性。
- **修复“生成为空/很慢”**：技能/编排等 use `deepseek-reasoner`（思考模式开时）会慢慢至空——现在是仅对话等重要推理才走 reasoner，结构化工具用常规模型（实测技能生成数秒、一键编排 10s 内 done）。

## New (English)
- **Client-located skills now truly trial-run on the current machine**: in the skill-library trial run (`/run`), `ExecutionLocation=Client` **dotnet (C#)** and local **shell** skills (e.g. the built-in `服务状态`) are sent over the native bridge (prefer the reported `clientId`; else the platform bridge = the current machine) and really compile/execute locally and return results — no longer handing a PowerShell body to the server’s Linux bash (the `Get-Service: command not found` error). Prerequisite: run `AguiGroupChat.NativeBridge` on this machine.
- **`/run` never server-runs a Client skill**: any `ExecutionLocation=Client` skill lacking an in-process executable form (e.g. http/prompt mislabelled client) now returns clear guidance instead of silently running server-side.
- **Trial-run results shown in a dedicated result dialog** (list view) or under the edit form (editing view).
- **“Thinking vs not” is now auto-decided by task**: structured/formatting generators (skill generation, one-click orchestration, trial-input suggestion, self-test & repair, graph/entity extraction, group naming) always use the regular chat model — they no longer fall into `deepseek-reasoner` just because global “thinking mode” is on (reasoner is slow and often empty on rigid JSON / short tokens). Complex plan-shaped tasks do a “deliberate-first-then-JSON” pass on the fast model, while persona/role/assignment-guidance generators still follow global thinking.
- **Long-run concurrency guard + cancellable “Stop”**: while skill/orchestration previews generate, other long-trigger buttons are greyed out; read-only generation offers a Stop (AbortController; safe, writes nothing); the persisting “Confirm & create” is deliberately not cancellable to protect data integrity.
- **Fixed “generation returns empty / too slow”**: structured tools use the fast model (measured: skill generation seconds; orchestration done in <10s).

**版本说明**：1.0.117 是其上一桌面点版本，主题为「本机(client)技能的试运行真正落到当前机器 + 生成工具“按任务自动决定思考” + 长任务互斥/可取消 + 试运行结果独立弹窗」；已构建 Windows 1.0.117 MSI。本机桥桥日志改写入系统临时目录。
**Version note**: 1.0.117 is the previous point release themed “Client-located trial runs land on the real current machine via the bridge; generators decide thinking type by task; long-run tasks gray out & are cancellable; trial results shown in a dialog”. A Windows 1.0.117 MSI was built. Native-bridge logs moved to the system temp directory.

---

# AG-UI 群聊桌面版 1.0.116 发布说明（1.0.117 之前的点版本）
# AG-UI Group Chat Desktop 1.0.116 Release Notes (previous point release)

## 新增（中文）
- **新的技能类型 `dotnet`（C#）**：技能正文可写 C# 源码（含 `public static string Run(string input)` 入口），运行时经 Roslyn 编译后执行。服务端（`server`）执行的 dotnet 在 Hub 进程内的可回收 `AssemblyLoadContext` 中运行（受限引用白名单 + `AllowUnsafe=false` + 超时 / 输出上限）；独立桥 `AguiGroupChat.NativeBridge` 新增 `DotnetRunner`，能在本机执行 `kind=dotnet` 隧道任务（Source = C#），因此浏览器所在主机的客户端 dotnet 技能由本机桥运行——浏览器自身无法编译 C#，客户端 dotnet 需触发者批准后经桥执行。
- **dotnet 权限模型（管理员建、人人可跑）**：`dotnet` 与 `shell`/`http` 同属特权桶——**仅系统管理员可创建 / 修改 / 删除**；自然语言生成 `/generate` 也只为系统管理员产出 `dotnet`。但**对现有 dotnet 技能的运行对全体登录用户开放**：任何登录用户可试运行服务端 dotnet、或经数字员工 / 客户端执行由本机桥运行，无需管理员。
- **技能库“试运行”自动建议示例入参**：试运行前先经 `POST /ag-ui/skills/{skillId}/suggest` 由模型依据技能描述 / 正文自动推导并预填一段代表性的示例输入，再 `POST /run` 执行，降低试运行门槛。
- **一键组织编排 apply 冒烟自测 + 自修复**：落库前先对服务端执行的技能做冒烟：`prompt` 用样例跑一次、`http` 仅静态校验 method/url（不外呼）、server shell 仅当 `Agents.SkillAutoTestServerShell`（`AGENTS_SKILL_AUTOTEST_SHELL`，默认 true）开启才盲跑，client / 隧道类跳过；失败项由大模型自动修复（最多 3 次）。apply 返回 `smoke[]`（`{skillId,skipped,ok,attempts,repaired,lastError}`），UI 在创建后于通知中心展示。
- **Shell 脚本写盘 BOM 修复**：服务端 `SkillRunner` 曾以带 UTF-8 BOM 的方式写 shell 脚本，导致 bash 下即使是合法首条命令也会失败；现改为写入<b>无 BOM 的 UTF-8</b>。
- docker-compose 新增暴露 `AGENTS_SKILL_AUTOTEST_SHELL`（对应选项 `Agents.SkillAutoTestServerShell`）。

## New (English)
- **New skill kind `dotnet` (C#)**：a skill body can be C# source exposing `public static string Run(string input)`，compiled at run time with Roslyn and executed. Server-side (`server`) dotnet runs inside the Hub process in a collectible `AssemblyLoadContext`（constrained reference allowlist + `AllowUnsafe=false` + timeout / output caps）；the standalone bridge `AguiGroupChat.NativeBridge` gains a `DotnetRunner` that can execute `kind=dotnet` tunnel tasks (Source = C#) on the local host, so client dotnet skills on the browser's host run via the bridge——a browser itself cannot compile C#，and client dotnet runs over the bridge after the triggerer approves.
- **dotnet permission model（admin-create，everyone-can-run）**：`dotnet` shares the privileged bucket with `shell`/`http`——**only system admins can create / edit / delete**；natural-language `/generate` also yields `dotnet` only for system admins. But **running an existing dotnet skill is open to all logged-in users**：any user can trial-run a server dotnet skill，or have a client-executed one run over the native bridge，no admin needed just to run.
- **Skill library “trial run” auto-suggests an example input**：before running，`POST /ag-ui/skills/{skillId}/suggest` has the model derive and pre-fill a representative example input from the skill description / body，then `POST /run` executes it.
- **Orchestration apply now smoke-tests + self-repairs**：before persisting，server-executed skills are smoke-tested——`prompt` runs once against a sample，`http` is only config-linted (method / url validity，no outbound call)，server `shell` is blind-run only when `Agents.SkillAutoTestServerShell`（`AGENTS_SKILL_AUTOTEST_SHELL`，default true）is on，client / tunnel kinds are skipped; failures are auto-repaired by the model（up to 3 attempts）。Apply returns a `smoke[]`（`{skillId,skipped,ok,attempts,repaired,lastError}`）that the UI surfaces in the notification center after creation.
- **Shell script BOM fix**：the server-side `SkillRunner` used to write shell scripts with a UTF-8 BOM，breaking even a valid first command under bash；scripts are now written as BOM-less UTF-8.
- docker-compose now exposes `AGENTS_SKILL_AUTOTEST_SHELL`（option `Agents.SkillAutoTestServerShell`）。

**版本说明**：1.0.116 是其上一桌面点版本，主题为「dotnet（C#）技能 + 试运行建议与编排冒烟自检」并包含 shell BOM 修复。
**Version note**: 1.0.116 was the previous point release, recording dotnet (C#) skills, trial-run input suggestion & orchestration smoke/self-repair, plus the shell BOM fix.

---

# AG-UI 群聊桌面版 1.0.108 发布说明
# AG-UI Group Chat Desktop 1.0.108 Release Notes

## 新增（中文）
（提交 d14f210）
- **图片理解（视觉）**：群消息携带图片附件时，该轮数字员工回复自动路由到视觉模型、以多模态 base64 内联看图作答；纯文本消息仍走常规 / 思考模型。视觉默认模型 `deepseek-v4-flash-vision-exp`（可用 `Agents:VisionModel` 覆盖，`Agents:VisionEnabled` 默认开启为总开关；`mock` 提供方不支持视觉）。图片从附件库读取、无需另存文件，发图 / 审批协作流程不变。本期已构建 Windows 1.0.108 MSI。

## New (English)
(commit d14f210)
- **Image understanding (vision)**: when a group message carries an image attachment, that turn is auto-routed to a vision model that sees the image (fed inline as base64 multimodal content); plain text messages keep the normal / thinking model. Default vision model `deepseek-v4-flash-vision-exp` (override with `Agents:VisionModel`; `Agents:VisionEnabled` defaults on as the master switch; the `mock` provider has no vision). The image is read from the attachment store — no extra file — and the image-upload / approval workflow is unchanged. A Windows 1.0.108 MSI was built for this release.

**版本说明**：1.0.107 → 1.0.108 为点版本，主题为「图片理解（视觉）」，并已构建 Windows 1.0.108 MSI。
**Version note**: 1.0.107 → 1.0.108 is a point release adding image understanding (vision); a Windows 1.0.108 MSI was built.

---

# AG-UI 群聊桌面版 1.0.107 发布说明
# AG-UI Group Chat Desktop 1.0.107 Release Notes

## 新增（中文）
（提交 b60b9c7）
- **自动编排重名去重（自动改名避重）**：生成的数字员工 / 技能 id 与原库同名时，自动追加 `_2/_3` 改名继续保存，不再整体失败、不覆盖已有资产，并自动同步方案内引用（技能挂载、上下级连接、客服知聚成员、返回 id）。
- **组织架构交互**：双击数字员工节点可直接打开编辑表单；组织关系连线同一对端点间的多条线会**横向错开**避免完全重叠；编辑返回上下文优化——从组织架构进入编辑，退出后回组织架构，再关闭回数字员工管理列表（从列表进入则回列表）。

## New (English)
(commit b60b9c7)
- **Auto-rename on orchestration id collisions**: when a generated digital-employee / skill id collides with an existing library entry, it is auto-renamed with a `_2/_3` suffix and saved anyway — no more whole-apply failures, no overwriting existing assets — and all in-plan references (skill mounts, up/down connections, support-circle members, returned ids) are remapped to the final ids.
- **Org-chart interactions**: double-clicking a digital-employee node now opens its edit form; edges connecting the same pair of endpoints are **laterally offset** so they no longer overlap; edit-return context is optimized — editing from the org chart returns to the org chart after exit, then closes back to the digital-employee list (editing from the list returns to the list).

**版本说明**：1.0.106 → 1.0.107 为点版本，主题为「自动编排重名去重 + 组织架构交互」。全量 714 个单元 / 集成测试通过。
**Version note**: 1.0.106 → 1.0.107 is a point release focused on auto-rename collision handling during orchestration and org-chart editing/rendering interactions. All 714 unit / integration tests pass.

---

# AG-UI 群聊桌面版 1.0.106 发布说明
# AG-UI Group Chat Desktop 1.0.106 Release Notes

## 新增（中文）
（提交 386c7ee）
- **客服知聚打字指示（typing）**：客服 / 数字员工输入时，顾客参与者能看到「客服正在输入」；顾客输入时客服能看到；顾客之间互不可见，与消息隔离一致（此前客服知聚里 typing 双向都不达）。
- **智能体上下文作用域**：客服知聚的智能体上下文窗口现在会包含本次触发顾客的隔离会话（顾客自己的提问 + 定向回给该顾客的客服消息），客服能「记得」该顾客之前聊过的内容，不再像新对话一样重答；其他顾客的私聊不进上下文。

## New (English)
(commit 386c7ee)
- **Support-circle typing indicators**: while a staff member / digital employee is typing, customer participants see “staff is typing”; while a customer types, staff see it; customers never see each other's typing, consistent with message isolation (previously typing was not delivered in either direction in support circles).
- **Agent context scoping**: the support-circle agent context window now includes the triggering customer's isolated conversation (the customer's own questions plus staff replies directed to that customer), so staff can “remember” that customer's prior dialogue instead of answering as if it were a fresh chat; other customers' private chats stay out of context.

**版本说明**：1.0.105 → 1.0.106 为点版本，主题为「客服知聚 typing 与智能体上下文修复」。全量 714 个单元 / 集成测试通过。
**Version note**: 1.0.105 → 1.0.106 is a point release that fixes support-circle typing delivery and scopes the agent context to the triggering customer. All 714 unit / integration tests pass.

---

# AG-UI 群聊桌面版 1.0.105 发布说明
# AG-UI Group Chat Desktop 1.0.105 Release Notes

## 新增（中文）
- **客服知聚权限修复随本版首发**：普通顾客（参与者、非成员）可批准其触发的客服技能执行（此前会被「决策者不是群成员」拒绝）；网关仍强校验必须是触发者本人。本版桌面包首次实际包含该修复。（提交 4dd9e94）
- **自动化验证工具增强**：`tools/ui-orchestrate-flow.mjs` 现在会一并清理编排创建的数字员工与技能，避免留下测试残留；技能删除带保护（仅删不再被现存数字员工引用的，避免误删用户团队同名的真实技能）。

## New (English)
- **Support-circle permission fix ships in this build**: ordinary customers (participants, non-members) can now approve the customer-service skill execution they triggered (previously rejected as “decider is not a group member”); the gateway still requires the approver to be the triggerer. This is the first desktop build to actually include the fix. (commit 4dd9e94)
- **Automation tooling hardening**: `tools/ui-orchestrate-flow.mjs` now also deletes the digital employees and skills it creates during orchestration to avoid test residue; skill deletion is protected so it only removes skills no longer referenced by any remaining digital employee, avoiding accidental deletion of same-named real skills used by your teams.

**版本说明**：1.0.104 → 1.0.105 为点版本，主题为「随本版正式带上客服知聚权限修复 + 文档与验证工具同步」。全量 711 个单元 / 集成测试通过。
**Version note**: 1.0.104 → 1.0.105 is a point release that formally ships the support-circle permission fix along with doc and verification-tool updates. All 711 unit / integration tests pass.

---

# AG-UI 群聊桌面版 1.0.104 发布说明
# AG-UI Group Chat Desktop 1.0.104 Release Notes

## 新增（中文）
- **一键组织编排·生成过程可视化（方案 C）**：新增流式 SSE 端点 `POST /ag-ui/agents/orchestrate/stream`，DeepSeek 逐 token 流式吐出生成过程，前端实时展示原始生成文本，并基于已见 JSON 实时统计「已识别 N 名数字员工 / M 个技能」；生成结束下发完整结构化方案供确认。`AgentOrchestrator` 新增 `StreamTextAsync`（真实模型流式 / mock 分片模板）。
- **编排 apply 可勾选「同时创建客服知聚」**：落库组织方案时可把方案里的数字员工作为客服团队建群（`GroupKind.Support`），并逐个注册触发规则（`@` 即可应答），直接把方案上线服务顾客。
- **客服知聚权限修复**：普通顾客（参与者、非成员）现在可以批准其触发的客服技能执行——客服知聚的核心业务流程打通（此前会被「决策者不是群成员」拒绝）；网关仍强校验必须是触发者本人。

## New (English)
- **One-click org orchestration — generation process visualization (Plan C)**: new SSE endpoint `POST /ag-ui/agents/orchestrate/stream` streams DeepSeek's output token-by-token; the frontend shows the raw generation in real time and live counts of “N digital employees / M skills identified”; a complete structured plan is delivered when generation finishes for confirmation. `AgentOrchestrator` gains `StreamTextAsync` (streams for real models; chunked template for mock).
- **Orchestrate apply can opt in to “create a support circle”**: when persisting the plan, the plan's digital employees can be assembled into a support circle (`GroupKind.Support`) with trigger rules registered per employee (@ triggers a reply), putting the plan online to serve customers immediately.
- **Support-circle permission fix**: ordinary customers (participants, non-members) can now approve the customer-service skill execution they triggered — the core support-circle flow now works (it used to be rejected as “decider is not a group member”); the gateway still strictly requires the approver to be the triggerer themselves.

## 修复（中文）
- **本机 shell 技能经隧道执行修复**：通过编排 / 表单创建的本地（client 执行）shell 技能若未携带 `ClientRunner`，现在会自动从命令体生成（bfbce39），且网关执行时兜底从命令体合成——已有技能无需重建即可在本机经隧道执行（此前提示「该技能非本机 shell，无法经隧道执行」）。
- **编排方案兼容真实模型的 `JSON 对象` 技能体**：http/shell 技能 body 常被模型写成 JSON 对象，解析时经 `FlexibleBodyConverter` 归一化为字符串（9d6b9e5）。

## Fixed (English)
- **Local shell skills now execute over the tunnel**: local (`client`) shell skills created via orchestration / form without a `ClientRunner` are now auto-derived from the command body (bfbce39), and the gateway synthesizes one at execution time as a fallback — existing skills work over the tunnel without re-creation (previously “this skill is not a local shell type, can't run over the tunnel”).
- **Orchestration tolerates JSON-object skill bodies from real models**: http/shell skill bodies are often emitted as JSON objects; parsing now normalizes them to strings via `FlexibleBodyConverter` (9d6b9e5).

## 工具（中文）
- 新增 `tools/ui-orchestrate-flow.mjs`（Playwright）浏览器自动化：一键编排 → 建客服知聚 → API 核验 → 清理，见 `tools/README-playwright.md`。

## Tools (English)
- Added `tools/ui-orchestrate-flow.mjs` (Playwright) browser automation covering orchestrating → creating a support circle → API verification → cleanup; see `tools/README-playwright.md`.

**测试**：全量 711 个单元 / 集成测试通过。
**Tests**: all 711 unit / integration tests pass.

---

# AG-UI 群聊桌面版 —— 增补：RBAC 权限分层 + 安全加固
# AG-UI Group Chat Desktop — Addendum: RBAC layering + security hardening

## 新增（中文）
- **平台级 RBAC 分层**：账号增加 `PlatformRole`（`user / operator / admin / superadmin`）。首个注册账号自举为超级管理员；新增 Operator（只读运维）角色；`GET|POST /ag-ui/admin/roles` 由超级管理员管理角色矩阵；`/status`、`/usage`、`/audit`、`/bridge-health`、`/bridge-capabilities`、`/metrics` 降为 Operator 可读。管理员控制台「用户管理」新增角色下拉（仅超级管理员可见）。
- **群级 RBAC 收敛**：不允许把成员标为 Owner（Owner 仅群主转让得到）；授予/撤销群管理员仅群主可操作；新增 `POST /ag-ui/group/transfer-owner` 群主转让（转让后原群主降为 Admin）。
- **频道级 RBAC 保持**：`canInvokeAgents` / `canApprove` 由群主/管理员按成员管理，默认全允许。
- **安全加固**：HTTP API 移除 `?memberId=` 身份回退；客户端技能桥新增 `ClientTool:RequireAdmin` 部署开关（共享多用户部署建议开启）；模型 API Key / TOTP 密钥落盘加密（`SecretVault`）；快照可选 HMAC 签名并防静默清空。

## New (English)
- **Platform RBAC layering**: accounts gain a `PlatformRole` (`user / operator / admin / superadmin`). The first registered account self-bootstraps as Super Admin; a read-only `Operator` role is introduced; `GET|POST /ag-ui/admin/roles` lets a Super Admin manage the role matrix; `/status`, `/usage`, `/audit`, `/bridge-health`, `/bridge-capabilities`, `/metrics` are now readable by Operator+. The admin console's User Management gains a role dropdown (visible to Super Admin only).
- **Group RBAC tightening**: members can no longer be marked `Owner` via member update (`Owner` is only obtained through ownership transfer); only the Owner may grant/revoke group admin; add `POST /ag-ui/group/transfer-owner` (the previous owner becomes a group admin after transfer).
- **Channel RBAC preserved**: `canInvokeAgents` / `canApprove` per-member limits remain manageable by owner/admin, defaulting to allow.
- **Security hardening**: HTTP APIs no longer trust a `?memberId=` identity fallback; the client-skill bridge gains a `ClientTool:RequireAdmin` deployment switch (recommended for shared multi-user deployments); model API keys & TOTP secrets are encrypted at rest via `SecretVault`; snapshots support optional HMAC signing and are protected from silent data loss.

详情见 `docs/RBAC.md`。See `docs/RBAC.md` for details.

---

# AG-UI 群聊桌面版 1.0.103 发布说明
# AG-UI Group Chat Desktop 1.0.103 Release Notes

## 新增（中文）
- **新增：客服知聚（`kind=support`）**。在公有 / 私有知聚之上新增客服知聚：创建者建群时拉入的**客服团队**（真人用户或数字员工，`Role=Admin`）为知聚的全部成员，可看到**所有会话**；客服知聚对所有用户可见、可进入，无需邀请。
- **顾客非成员、会话隔离**：普通用户进入客服知聚时**不会加入成员表**（不占成员名额、不出现在成员清单），而是登记为一个带 30 分钟活动 TTL 的**顾客参与者**，获得与客服团队聊天的**独立会话**；每位顾客之间彼此隔离（A 看不到 B 的会话），客服回复某顾客定向到该顾客，客服之间的内部沟通仅客服可见。
- **接口**：`POST /ag-ui/group/create` 增加 `kind=normal|support`；`GET /ag-ui/group/discover` 发现全部客服知聚（对已登录用户可见，含 `isMember`/`hasEntered`）；`POST /ag-ui/group/{groupId}/enter` 进入（非成员登记为顾客参与者）。
- **前端**：创建弹窗增加「普通知聚 / 🛟 客服知聚」选择；侧栏自动展示可进入的客服知聚（蓝色标签 + 非成员「进入」标）；进入后作为参与者直接聊天。

## New (English)
- **New: support circles (`kind=support`)**. On top of public/private circles: the invited **support team** (humans and agent employees, `Role=Admin`) is the circle's entire membership and sees **every conversation**; a support circle is discoverable and enterable by all users without invitation.
- **Customers are non-members with isolated conversations**: entering a support circle does **not** add you to the member roster (no headcount, not listed); you register as a time-limited **customer participant** (30-min activity TTL) with your **own isolated conversation** with staff. Customers are isolated from each other; staff replies target the specific customer; internal staff chats stay staff-only.
- **APIs**: `POST /ag-ui/group/create` now takes `kind=normal|support`; `GET /ag-ui/group/discover` lists support circles (visible to any logged-in user, with `isMember`/`hasEntered`); `POST /ag-ui/group/{groupId}/enter` to enter (registers non-members as customer participants).
- **Frontend**: create dialog gains a "Normal / 🛟 Support circle" picker; the sidebar automatically shows enterable support circles (blue badge + an "Enter" chip for non-members); entered participants can chat directly.

---

# AG-UI 群聊桌面版 —— 新增：内网穿透（反向隧道）
# AG-UI Group Chat Desktop — New: NAT traversal (reverse tunnel)

## 新增（中文）
- **新增：反向隧道（HTTP/SSE）让「无公网 IP」的内网机本机桥被公网 Hub 调用**。内网机上的本机桥主动出站连公网 Hub 并注册（`--agent` 绑定数字员工）；Hub 把对该员工的客户端技能任务沿隧道下行推给内网桥执行，结果回传模型继续作答——无需入站公网端口、无需第三方隧道。
- **用法**：`NativeBridge --tunnel <hub> [--agent <id>] --tunnel-token <token>`；Hub 侧 `NativeTunnel__Token`（或 appsettings `NativeTunnel:Token`）校验。前端桥地址仍填 Hub 自身即可，网关经隧道转发。
- **平台级桥（信任整个平台）**：不再强制指定数字员工 id——`--tunnel` 时不填 `--agent` 即注册为平台级桥（`*`），一座桥服务任意数字员工的客户端技能；某员工有专属桥时优先用专属桥，否则回落到平台级桥。
- **安全加固**：逐 agent 专属隧道令牌（`NativeTunnel:AgentTokens__<agentId>`，优先于全局 `NativeTunnel:Token`）；`POST /ag-ui/native-tunnel/result` 回传需携带该 agent 有效令牌（本机桥自动带 `--agent/--tunnel-token`），防伪造结果 / 无令牌刷接口；`connect` 与 `result` 端点带内存滑动窗口限流（默认 120 / 600 次每 IP 每分钟，可配）。
- **简化：网页端取消「本机工具桥」手动配置**。移除「修改资料」里的桥地址 / 令牌输入与「一键检测」——本机 shell 执行统一经反向隧道自动路由（起桥 `--tunnel` 即生效，前端无需填任何桥配置）；无隧道桥时回落到服务器端 `/ag-ui/client-tool`。桌面版本就无需桥配置。

## New (English)
- **New: reverse tunnel (HTTP/SSE) so an intranet local bridge with no public IP can be called by the public Hub**. The bridge on the intranet host dials out to the public Hub and registers (binding a digital employee via `--agent`); the Hub pushes that employee's client-skill task down the tunnel for the bridge to execute, posting the result back so the model can continue—no inbound public port, no third-party tunnel. Usage: `NativeBridge --tunnel <hub> [--agent <id>] --tunnel-token <token>`; the Hub validates via `NativeTunnel__Token` (or appsettings `NativeTunnel:Token`). The frontend bridge URL still points at the Hub itself; the gateway forwards over the tunnel.
- **Platform-wide bridge (trust the whole platform)**: `--agent` is now optional under `--tunnel`—omit it to register as a platform-wide bridge (`*`) that serves any employee's client skills; an employee-specific bridge takes precedence when present, otherwise execution falls back to the platform-wide bridge.
- **Security hardening**: per-employee tokens (`NativeTunnel:AgentTokens__<agentId>`, takes precedence over global `NativeTunnel:Token`); the `POST /result` endpoint now requires a valid token for that agent (the bridge sends `--agent`/`--tunnel-token` automatically); in-memory sliding-window rate limiting on both `connect` and `result` (default 120 / 600 per IP per minute, configurable).
- **Simplified: the web UI's manual “Native Tool Bridge” config is gone**. Removed the bridge URL / token fields and one-click Detect from Profile; local shell execution now routes automatically through the reverse tunnel (just start the bridge with `--tunnel`), falling back to the server-side `/ag-ui/client-tool` when no tunnel bridge is connected.

---

# AG-UI 群聊桌面版 1.0.102 发布说明
# AG-UI Group Chat Desktop 1.0.102 Release Notes

## 修复与改进（中文）
- **新增：客户端执行技能（`ExecutionLocation=Client`，本机执行）**。技能可配置为「客户端执行」：服务端不执行，而是由<b>前端/本机桥</b>在浏览器所在主机执行并把结果回传（shell 走本机 PowerShell/沙箱；http 走浏览器 fetch）。前端在卡的聊天历史内确认后执行，结果回灌模型继续。
- **新增：独立「本机工具桥（NativeBridge）」**。Docker + 浏览器在本机（如 aibook）时，shell 类客户端技能需在<b>浏览器所在主机</b>执行而非 Docker 容器。新增独立项目 `src/AguiGroupChat.NativeBridge`（回环监听、令牌鉴权、CORS 白名单、沙箱/超时/截断），并在「修改资料 → 本机工具桥」提供 **🔍 一键检测** 自动读地址+令牌。桌面版无需（桌面壳即本机）。
- **新增：编排计划内客户端技能「本机一键执行全部」**。数字员工定计划时若一次选中多个客户端技能，网关把它们合并成一张 `client_tool_batch` 卡，一次确认后前端逐个本机执行、逐条点亮计划卡、最后综合回归。
- **新增：递归补查闭环（方案 C）**。数字员工基于已收集结果作答时，若发现信息不足（缺磁盘/内存/日志等关键数据），会**主动继续调技能/派下属补齐**，直到信息充分才给最终结论——不再中途停下问「要不要继续」。
- **新增：用自然语言生成技能配置**。技能库「🤖 用自然语言生成技能」：输入需求（如「检查本机磁盘使用情况」），大模型产出名称/类型/命令/描述/执行位置/ClientRunner，自动填入表单供微调保存（可选「优先本机执行」）。端点 `POST /ag-ui/skills/generate`。
- **增强：技能型智能体也走编排计划**。开启 `CoordinatorPlanning` 后，仅挂了技能（无下属/提升目标）的数字员工被 @ 时也进入计划编排（多技能批量 + 递归补查），不再回落为普通单工具调用。Docker 默认 `CoordinatorPlanning=true`。
- **修复：审批/交互卡点击后即时隐藏**。已决策（resolved）或客户端技能执行中（running）的卡片即时消失且不随任何重渲染复活（同步移除 DOM + 渲染层空化）。
- **修复：编译/运行、多技能规划、计划步骤上限**。多技能组合体检规划（6→8 步上限）与规划 prompt 引导（全面检查时多选互补技能）。

## Fixed & Improved (English)
- **New: client-execution skills (`ExecutionLocation=Client`)**. A skill can be marked "client execution" so the server does not run it; the frontend/native bridge runs it on the browser's host machine and posts the result back (shell via local PowerShell sandbox; http via browser fetch). Confirmation happens inline on the chat card, then the result is fed back to the model.
- **New: standalone NativeBridge**. For Docker + a browser on the local machine (e.g. aibook), shell client skills must run on the <b>browser's host</b> rather than the Docker container. Added `src/AguiGroupChat.NativeBridge` (loopback, token auth, CORS allowlist, sandbox/timeout/truncation) plus a **one-click Detect** in Profile -> Native Tool Bridge to read address & token. Desktop needs none (the desktop shell is the local host).
- **New: batch "run all locally" for client skills inside a plan**. When a plan selects several client skills, the gateway merges them into one `client_tool_batch` card: confirm once, the frontend runs each locally, lights up each plan step, then synthesizes a combined answer.
- **New: recursive gather-and-answer loop (Plan C)**. While answering from gathered results, if the info is insufficient (missing disk/memory/logs etc.), the digital employee proactively keeps invoking skills/direct reports until the answer is complete—never stopping to ask "continue?" in the middle.
- **New: generate skill definitions from natural language**. In the skill library, "generate from plain text": describe a request (e.g. "check local disk usage"), and the LLM produces name/kind/command/description/execution location/ClientRunner, filled into the form for review (optionally "prefer local execution"). Endpoint `POST /ag-ui/skills/generate`.
- **Enhancement: skill-only agents also take the coordinator plan**. With `CoordinatorPlanning` on, an agent that only has skills (no subordinates/escalation) also routes through plan orchestration when @-mentioned (multi-skill batch + recursive gathering) instead of plain single-tool calls. Docker defaults `CoordinatorPlanning=true`.
- **Fix: interaction cards hide immediately on decision**. A resolved or running client-tool card disappears instantly and cannot reappear on any re-render (synchronous DOM removal + empty render).

## 使用提示（中文）
客户端技能：桌面版开箱即用（桌面壳本机执行）；Docker + 本机浏览器需在「修改资料 → 本机工具桥」一键检测填入地址与令牌。自然语言生成技能需已配置 DeepSeek 等模型 key。

## Usage Note (English)
Client skills work out of the box in the desktop edition (the desktop shell executes locally); for Docker + a local browser, use Profile -> Native Tool Bridge -> Detect to fill the address & token. Generating skills from text requires a configured model key (e.g. DeepSeek).

---
文件：`AguiGroupChat-Desktop-1.0.102.msi` / Docker（postgres+ollama+web）
File: `AguiGroupChat-Desktop-1.0.102.msi` / Docker (postgres+ollama+web)

---

# AG-UI 群聊桌面版 1.0.80 发布说明
# AG-UI Group Chat Desktop 1.0.80 Release Notes

## 修复与改进（中文）
- **修复：桌面版技能库不持久化（重启丢失）**。桌面后端装配漏注册技能库持久化（`DesktopApp` 缺 `RegisterSkillPersistence()`，Web 版有）——导致桌面版里新建 / 编辑的技能不写盘、重启即丢，且数字员工经 `skillDefIds` 引用的技能在重启后会被当「不存在」跳过而不被调用。已补上 `RegisterSkillPersistence()`，桌面技能库跨重启保持。
- **新增：HTTP 技能访问本机 / 内网开关（`Agents:AllowPrivateSkillEndpoints`）**。默认关（保留 SSRF 防护，拒绝本机/内网）；确需调用本机 / 内网接口（本地 Ollama / 内网 API）时置 `true` 放行，放行后仍保留 http/https 白名单与重定向逐跳校验。桌面 `appsettings.json`，Docker `AGENTS_ALLOW_PRIVATE_SKILL_ENDPOINTS`。
- **修复：桌面版保存技能失败（405）**。桌面版后端装配漏注册技能库 API（`DesktopApp` 未调用 `MapSkillApi()`，Web 版有）；导致桌面版里技能库新增 / 编辑 / 试运行全部返回 405。已补上 `MapSkillApi()`，桌面与 Web 技能库功能一致。
- **修复：指派链路前缀末级对象重复显示**。多级下派到叶子作答时，最末端对象在「代为处理」前缀里会出现两次（如 `（Exchange连接测试助手 代为处理）` ×2）。修复 `AgentGateway` 路由链构建：末级自答时不再重复叠加末级关系节点，链路各层只出现一次。
- **修复：组织架构未保存改动时「优化指派」会出错**。新增未保存检测——新拖的指派/提升连线未点「保存」时，点「优化指派」会提示「请先保存」而不再基于过时后端数据生成。
- **指派判断只看下一层**：组织架构里的数字员工只依据<b>自己直接下一层</b>的提示词/职责判断是否指派（不向上钻、不引入更深层叶子），同时保留下层<b>多指派</b>（多候选排序 + 回退）与「收到指派后继续嵌套指派」（多级深钻链）。
- **新增：组织架构「优化指派」提示词生成**：组织架构图每个节点新增「优化指派」按钮，按该数字员工的<b>直接下一层</b>（AssignmentIds）自动生成一段「管理下一层任务指派」指引（只依据下一层挑下游、不越级、行不通则返回 NONE），预览后可追加到其 Instructions。端点 `POST /ag-ui/agents/{agentId}/optimize-assignment`（需登录，仅创建者/管理员）。
- **修复：任务指派无法按组织架构深钻到最后一层**。此前指派到达下一层时仅做「单候选贪心」：若该层语义分析返回 NONE 就提前终结，即使真正匹配的数字员工在更深层。现改为<b>多候选排序 + 递归探测回退</b>：指派持续向下钻取，某子分支无解时回退到下一候选，直到命中能作答的末端层；并向模型传入候选昵称+职责，语义匹配更准。
- **图谱 RAG 默认关闭（本次交付默认禁用，可手动开启）**：为排除图谱检索对问答/下派效果的干扰，本次 <b>Docker 与桌面版默认 `GraphEnabled=false`</b>（向量语义记忆不受影响）。需要图谱时再于 `.env`（`MEMORY_GRAPH_ENABLED=true`）或 `appsettings.json`（`Agents:Memory:GraphEnabled=true`）开启。
- **图谱 RAG 注入收敛（提升检索效果）**：图谱定位为补强而非并重——新增 `GraphMaxSectionChars=700` 注入预算，收紧默认 `GraphTopK=3→2`、`GraphMinScore=0.30→0.45`、`GraphHops=2→1`、`GraphMaxNodes=40→12`，避免图谱挤占、稀释向量切片。
- **修复：数字员工“未调用已关联的 HTTP 技能”**。根因是 HTTP 技能在创建/更新时被<b>强制置为需审批</b>（`SkillApi.BuildDef` 把 shell/http 一律写死 `RequiresApproval=true`）——技能虽已挂载，但模型一调用就进审批卡，自动化流程没有人工批，看起来就是“没被调用”。现将 `RequiresApproval` 改为：<b>仅 shell 技能强制需审批</b>，HTTP / 提示词技能跟随创建者勾选（可<b>关闭审批以自动调用</b>），需要本机/内网时另由 `Agents:AllowPrivateSkillEndpoints=true` 放行。前端技能表单相应放开 HTTP 的“需审批”开关。
- **修复：数字员工保存失败（请求格式错误 / 缺少字段）**。前端错误展示改为<b>优先显示后端返回的具体原因</b>（如“引用的技能不存在于技能库：xxx”），不再被通用错误码文案掩盖；同时排查并修正了数字员工 `skillDefIds` 引用不存在技能导致保存 400 的数据问题。
- **新增：数字员工表单「可调用子数字员工」**：把下层（或其他）数字员工挂为上层角色的可调用技能——模型需要其领域能力时可自动调起、引用其答复（即“上层正好需要下层能力时触发”）。此前后端与文档都已支持 `Skills（子代理）`，但前端表单不再暴露入口，导致无法配置；已在「技能与知识」段补回多选择器 + 每项调用说明，`skillId` 留空后端自动生成 `skill_<目标ID>`。
- **修复：所有桌面实例退出后后台进程残留**。根因是运行期 `instance-count`（记录多少个 UI 窗口在共享同一个后端）在异常退出后可能残留非零，导致后续每次启动把计数不断抬升、永远到不了 0，`/ag-ui/shutdown` 也就不会触发，后端进程残留。修复分三层：① 后端进程启动时把残留计数<b>归零</b>（新进程必然没有已存活的 UI）；② 后端自监视停机兜底——实例计数为 0 且无活动连接持续约 15s、或即使计数残留为正但无活动连接持续约 2 分钟时，后端自行优雅停机（显式 `Environment.Exit` 冲刷落盘）；③ 正常链路（计数归零 → HTTP shutdown）仍即时退出。已实测：无 UI 时后端自动退出、有 UI（计数=1）时后端保持在线。
- **新增：确定性编排计划（Coordinator Plan，`Agents:CoordinatorPlanning`）**：针对“技能/数字员工难以被可靠触发”的痛点，提供“问题 → 按<b>组织架构与技能配置</b>定计划 → <b>依次激活</b>对应数字员工与技能执行 → 聚合答复”的编排方式。开启后，带指派白名单/提升目标的路由型数字员工收到问题时，会把<b>可达的下游员工清单</b>与<b>可调用技能清单</b>显式列给模型，由其产出结构化执行计划（dispatch 派谁 / skill 调什么 / answer 汇总），再确定性逐项激活；任何环节失败自动回退到原有递归指派，不阻断主流程。桌面默认开启，Docker `AGENTS_COORDINATOR_PLANNING`；默认关（未启用时行为不变）。
- **增强：支撑<b>技能→技能 / 员工→技能的依赖链</b>**。编排计划会识别技能正文里的输入占位（`${query}` 等，见清单中技能的【需要输入：…】），并明确引导计划<b>先 dispatch 掌握该输入的员工拿值，再调用技能</b>（技能步骤自动收到上一步结果作输入）。这解决了“某技能的运行需要另一员工/前序步骤提供参数（如 Exchange 连接测试技能需要 OWA 地址，而地址由配置管理员提供）”的依赖场景；并补充了该依赖链的回归测试。
- **新增：编排计划可视化**。当确定性编排计划运行时，界面会把<b>执行计划步骤</b>广播为 `TEXT_MESSAGE_PLAN`（前端计划卡渲染）：如「指派「配置管理员」→ 调用技能「连接测试」→ 综合答复」逐项勾选展示，让用户看清“问题 → 按组织/技能定的计划 → 依次激活了谁”。
- **增强：编排计划<b>边执行边逐条点亮</b>**。计划拆分重构为「先规划 → 随消息流逐项执行」：消息开播即广播“全部待执行”的计划卡，每完成一步（派下属 / 调技能）动态把对应步骤勾选为完成并<b>实时刷新计划卡</b>，全部完成后综合答复——用户能实时看到每一步在现场点亮。测试断言：多次计划广播、首帧全未完成、末帧全部完成且含“调用技能”。
- **修复：技能需要的“值”喂不干净（如 Exchange 测试技能要 OWA 地址，却拿到带解释的文字）**。参数化技能（正文含 `${query}`）执行前，编排计划会：① 前置的取值步骤（派给配置管理员等）提示<b>只输出所需的值本身</b>；② 即便员工返回了带解释的文字，也会用 `ExtractCleanValueForSkill` <b>提取出干净的 URL/地址</b>再作为技能输入。新增 4 个取值提取单测 + 依赖链回归（断言先派后调、多次点亮）。
- **修复：协调计划派给子员工时丢上下文（配置管理员答“数据不足”）**。根因：协调计划经 `child.RunAsync` 直接调子员工时，`MemoryContextProvider` 仍按<b>宿主</b>的 `AmbientContext` 注入知识库/记忆——于是派给绑了配置库的“配置管理员”时它拿不到自己的配置库（`mail.lingtong.com`），只会答“数据不足/请提供地址”。修复：执行 dispatch 步骤时把 ambient 上下文<b>切换到子员工本人</b>（Group/Topic/触发者不变，仅 AgentId/Nickname 改为子员工），子员工据此检索自己的知识库。
- **相同修复顺带覆盖编排流水线（Pipeline）与角色交接（Relay）**：这两处直接 `child/relay.RunAsync` 调子员工/被交接方，同样会因宿主 ambient 上下文拿不到自己的知识库记忆；已做一致处理（Pipeline 每步、Relay 整轮都切到对方）。
- **“代为处理”表述对齐**：协调计划卡上被指派的步骤显示为「指派「X」代为处理」，与消息正文的前缀「（X 代为处理）」一致。
- **有计划卡时不再在正文重复“（X 代为处理）前缀”**：既然计划卡已把“指派给谁”表达清楚了，协调计划路径的消息正文不再叠加「（X 代为处理）」前缀，避免冗余；无计划卡的非编排路径仍保留前缀。
- **计划卡文字调整**：指派步骤由「指派「X」代为处理」改为「**为「X」分配工作**」。

## Fixed & Improved (English)
- **Fix: router agents no longer self-answer and swallow issues that should be dispatched (IT Service Desk → Exchange Expert scenario)**. Previously the "should I answer" semantic check ran before dispatch, so the IT Service Desk self-answered "outlook can't connect to exchange" instead of dispatching to a dedicated Exchange expert. Now <b>router nodes (with an assignment whitelist) dispatch first</b> - drill to the best-matching specialist layer before self-answering, and only fall back to self-serving when no specialist claims it; multi-level deep drilling to the last layer is also supported.
- **Assignment judgment only looks at the next layer**: an org-chart agent decides whether to assign based solely on its <b>direct subordinates'</b> prompts/roles (no up-drilling, no bringing in deeper specialist leaves), while keeping down-layer <b>multi-assignment</b> (multi-candidate ranking + fallback) and <b>nested assignment</b> after receiving a task (multi-level deep-drill chain).
- **New: org-chart "Optimize dispatch" prompt generator**: each org-chart node gains an "Optimize dispatch" button that auto-generates a "manage next-layer dispatch" guidance paragraph from the employee's <b>direct next layer</b> (AssignmentIds) - pick a subordinate based only on the next layer, no override, return NONE if none fits - previewable and appendable to its Instructions. Endpoint `POST /ag-ui/agents/{agentId}/optimize-assignment` (login required, owner/admin only).
- **Fix: task assignment cannot drill down the org tree to the last layer**. Assignment previously used a greedy single-candidate pick at the next layer: if that layer's semantic analysis returned NONE, the chain terminated early even though the real matching agent sat deeper. Now it is <b>multi-candidate ranking + recursive probe with fallback</b>: assignment keeps drilling downward, and when one branch fails it falls back to the next candidate until it reaches an answering leaf (supports inference down the org tree to the end); the model is also given each candidate's nickname + role so semantic matching is more accurate.
- **Tightened Graph RAG injection (better retrieval quality)**: the graph is now a supplement, not an equal-weight block - a `GraphMaxSectionChars=700` injection budget and tighter defaults (`GraphTopK=3→2`, `GraphMinScore=0.30→0.45`, `GraphHops=2→1`, `GraphMaxNodes=40→12`) keep it from crowding out or diluting the vector chunks.
- **Fix: digital employee "does not call its associated HTTP skill"**. Root cause: HTTP skills were hard-forced to `RequiresApproval=true` at create/update (`SkillApi.BuildDef` hard-coded shell/http), so although the skill was mounted, the model's call immediately hit an approval card that no human approved in the automated flow - looking like "not called". Now `RequiresApproval` follows the creator's choice for <b>HTTP / prompt</b> skills (can be <b>turned off to auto-invoke</b>), while <b>shell skills stay always-requiring-approval</b>; access to local/intranet targets is governed separately by `Agents:AllowPrivateSkillEndpoints=true`. The skill form's "requires approval" toggle is now enabled for HTTP.
- **Fix: digital employee save failure (request format error / missing fields)**. Error display now <b>prefers the backend's specific message</b> (e.g. "referenced skill not found in library: xxx") instead of being masked by a generic code, and a data issue where an agent's `skillDefIds` referenced a nonexistent skill causing a 400 on save was diagnosed and fixed.
- **New: "Callable sub employees" in the digital-employee form**: attach a lower (or any other) digital employee as a callable skill of an upper role - the model auto-invokes and cites it when it needs that employee's capability (i.e. "the upper employee triggering a lower employee's capability when needed"). The backend and docs already supported `Skills` (sub-agents) but the form no longer exposed an entry to configure it; a multi-selector plus a per-item call description is now restored under the "Skills & Knowledge" section, with `skillId` auto-generated (`skill_<targetAgentId>`) when left blank.
- **Fix: background process lingers after all desktop instances exit**. The runtime `instance-count` (which records how many UI windows share the one backend) could be left non-zero after an abnormal exit, so every subsequent launch kept inflating it and it never reached 0 - the `/ag-ui/shutdown` call never fired and the backend process lingered. The fix has three layers: ① the backend process <b>resets any stale count to 0 on startup</b> (a newly started backend has no live UI by definition); ② a backend <b>self-monitoring watchdog</b> gracefully stops the backend when the instance count is 0 with no active connections for ~15s, or even if the count is stale-positive but there are no active connections for ~2 minutes (`Environment.Exit` flushes persistence); ③ the normal path (count hits 0 -> HTTP shutdown) still exits immediately. Verified: the backend exits when no UI is attached and stays online when a UI (count=1) is present.
- **New: deterministic orchestration plan (Coordinator Plan, `Agents:CoordinatorPlanning`)**. To address the unreliable triggering of skills / digital employees, this adds a "question -> build a plan from the <b>org chart & skill config</b> -> <b>activate</b> the selected digital employees & skills in sequence -> aggregate the answer" orchestration. When enabled, a router-type digital employee (with an assignment whitelist / escalation target) receives a question, explicitly enumerates the <b>reachable subordinate employees</b> and <b>callable skills</b> to the model, has it produce a structured execution plan (dispatch who / invoke which skill / how to summarize), then activates each step deterministically; any failure falls back to the original recursive dispatch without blocking the flow. Enabled by default in the desktop; Docker `AGENTS_COORDINATOR_PLANNING`; default off (behavior unchanged when not enabled).
- **Enhanced: supports <b>skill->skill / employee->skill dependency chains</b>**. The orchestration plan now detects the input placeholders in a skill body (`${query}` etc., shown as 【需要输入：…】 in the inventory) and explicitly guides the plan to <b>first dispatch the employee that holds that input, then invoke the skill</b> (the skill step automatically receives the previous step's result as its input). This addresses cases where one skill's execution needs a parameter supplied by another employee / earlier step (e.g. the Exchange connectivity-test skill needs the OWA address, which the config admin provides); a regression test covers the chain.
- **New: orchestration-plan visualization**. When the deterministic orchestration plan runs, the UI now receives the <b>execution-plan steps</b> as a `TEXT_MESSAGE_PLAN` (rendered as a plan card): e.g. "dispatch to 配置管理员 -> invoke the connectivity-test skill -> synthesize the answer", shown step-by-step, so the user can see "question -> plan from the org/skills -> who was actually activated". Covered by a test that asserts the plan event contains a skill step in the dependency-chain scenario.
- **Enhanced: orchestration plan lights up step-by-step in real time**. The plan was split into "first plan, then execute step-by-step while the message streams": as soon as the message starts, the plan card is broadcast with all steps pending; each step (dispatch to an employee / invoke a skill) marks its own row complete and <b>refreshes the plan card live</b>, and after all steps the final synthesized answer streams. The test asserts multiple plan broadcasts, first frame all pending, last frame all done and containing an "invoke skill" step.
- **Fix: coordinator lost context when dispatching to a sub employee (config admin answering "insufficient data")**. Root cause: the coordinator invoked the sub employee via its own `child.RunAsync`, but `MemoryContextProvider` injected knowledge/memory based on the <b>host's</b> ambient context - so when it dispatched to a config admin bound to a config knowledge base, that admin could not see its own config base (`mail.lingtong.com`) and only answered "insufficient data / please provide the address". Fix: when executing a dispatch step, the ambient context is now <b>switched to the sub employee</b> (Group/Topic/triggerer stay the same, only AgentId/Nickname are the sub employee's) so it retrieves its own knowledge base.
- **The same fix also covers the orchestration pipeline (`Pipeline`) and role handoff (`Relay`)**: both invoke sub/passee agents via `child/relay.RunAsync` directly and would lose their own knowledge/memory under the host's ambient context; they are now handled consistently (each pipeline step and the whole relay run switch the ambient context to the counterpart).

## 使用提示（中文）
全新安装或想彻底重置：先完全退出桌面版，再删除 `%LocalAppData%\AguiGroupChat\data\` 目录后启动，第一个注册账号即为管理员。启用图谱记忆：`appsettings.json` → `Agents:Memory:GraphEnabled=true`。组织下派靠「智能体管理 → 组织架构」配置各角色的任务指派白名单（AssignmentIds）与问题提升目标。

## Usage Note (English)
For a clean start: fully quit the app, delete `%LocalAppData%\AguiGroupChat\data\`, then launch - the first account you register becomes the admin. To enable graph memory set `Agents:Memory:GraphEnabled=true` in `appsettings.json`. Configure per-role assignment whitelists (`AssignmentIds`) and escalation targets via "Agent Management → Org Chart".

---
文件：`AguiGroupChat-Desktop-1.0.80.msi` / Docker（postgres+ollama+web，`MEMORY_GRAPH_ENABLED=true`）
File: `AguiGroupChat-Desktop-1.0.80.msi` / Docker (postgres+ollama+web, `MEMORY_GRAPH_ENABLED=true`)

---

# AG-UI 群聊桌面版 1.0.79 发布说明
# AG-UI Group Chat Desktop 1.0.79 Release Notes

## 改进（中文）
- **图谱 RAG 注入收敛（提升检索效果）**：之前图谱子图（最多 40 实体 + 50 边）与向量切片平权强塞进 prompt，反而稀释了向量检索结果。本次把图谱定位为<b>补强而非并重</b>：段落强引导语「仅作参考、涉及具体事实以向量/知识库切片原文为准」，并新增 `GraphMaxSectionChars=700` 总字符预算——先保留种子/近层实体与其连接的高价值边，超出部分丢弃；同时收紧默认召回 `GraphTopK=3→2`、`GraphMinScore=0.30→0.45`、`GraphHops=2→1`、`GraphMaxNodes=40→12`，让图谱只在查询很贴近实体时少量补充，避免挤占、稀释向量切片。

## Improved (English)
- **Tightened Graph RAG injection (better retrieval quality)**: previously the graph subgraph (up to 40 entities + 50 edges) was injected with equal weight alongside the vector chunks, which diluted the vector results. The graph is now positioned as a <b>supplement</b>: the section carries explicit guidance "for reference only; specifics should defer to the vector/KB snippets", and a new `GraphMaxSectionChars=700` char budget keeps seed/nearby entities and the high-value edges connecting them first, dropping anything beyond; defaults are also tightened (`GraphTopK=3→2`, `GraphMinScore=0.30→0.45`, `GraphHops=2→1`, `GraphMaxNodes=40→12`) so the graph only adds a little when the query is very close to an entity, without crowding out the vector chunks.

## 使用提示（中文）
全新安装或想彻底重置：先完全退出桌面版，再删除 `%LocalAppData%\AguiGroupChat\data\` 目录后启动，第一个注册账号即为管理员。启用图谱记忆：`appsettings.json` → `Agents:Memory:GraphEnabled=true`。已启用用户若觉得图谱仍偏噪声，可进一步调低 `GraphMaxSectionChars` 或关闭 `GraphEnabled`。

## Usage Note (English)
For a clean start: fully quit the app, delete `%LocalAppData%\AguiGroupChat\data\`, then launch - the first account you register becomes the admin. To enable graph memory set `Agents:Memory:GraphEnabled=true` in `appsettings.json`. If you still find the graph noisy, lower `GraphMaxSectionChars` further or turn `GraphEnabled` off.

---
文件：`AguiGroupChat-Desktop-1.0.79.msi` / Docker（postgres+ollama+web，`MEMORY_GRAPH_ENABLED=true`）
File: `AguiGroupChat-Desktop-1.0.79.msi` / Docker (postgres+ollama+web, `MEMORY_GRAPH_ENABLED=true`)

---

# AG-UI 群聊桌面版 1.0.78 发布说明
# AG-UI Group Chat Desktop 1.0.78 Release Notes

## 修复（中文）
- **修复：长文档上传知识库向量化失败（返回「embedding 不可用」）**。本地 embedding（LLamaSharp / bge-m3）超长文本分段的字符/token 比值误设为 2.0，导致单次 embedding 的字数上限（context×2=4096）超过模型真实 context，凡正文切片约 2000+ 字符的文档（如员工手册类 docx）都会被整片交给模型、返回空向量而入库失败。已改为 0.9，长切片自动切成多段（每段 ≤ context×0.9 字）分别向量化、再取平均，长文档可正常入库。

## Fixed (English)
- **Fix: long documents fail vectorization on KB upload** (reported as "embedding unavailable"). The safe chars/token ratio for long-text segmenting in the local embedding (LLamaSharp / bge-m3) was mistakenly set to 2.0, so the single-shot character cap (context×2=4096) exceeded the model's real context; any document whose chunks exceed ~2000 characters (e.g. employee-handbook docx) was passed whole to the model, returned an empty vector, and failed to ingest. Changed to 0.9 - long chunks are now split into multiple segments (each ≤ context×0.9 chars), embedded separately, then averaged, so long documents ingest correctly.

## 使用提示（中文）
全新安装或想彻底重置：先完全退出桌面版，再删除 `%LocalAppData%\AguiGroupChat\data\` 目录后启动，第一个注册账号即为管理员。启用图谱记忆：`appsettings.json` → `Agents:Memory:GraphEnabled=true`。

## Usage Note (English)
For a clean start: fully quit the app, delete `%LocalAppData%\AguiGroupChat\data\`, then launch - the first account you register becomes the admin. To enable graph memory set `Agents:Memory:GraphEnabled=true` in `appsettings.json`.

---
文件：`AguiGroupChat-Desktop-1.0.78.msi`（约 584 MB，已内置本地 embedding 模型）
File: `AguiGroupChat-Desktop-1.0.78.msi` (~584 MB, bundles the local embedding model)

---

# 上一版：1.0.77
# Previous: 1.0.77

## 新增（中文）
- **知识库图谱 RAG（Graph RAG）**：上传到知识库的文档在入库时也会抽取「实体-关系-实体」建入隔离域 `kb:{KbId}` 的图谱；检索知识库时对绑定知识库做「语义召回种子实体 + n 跳图遍历」，把可达子图与向量切片并列注入 prompt，补强知识文档中的关系型知识。知识库图谱与群记忆图谱按域隔离、互不污染；删除知识库时同步清其图谱。
- **系统状态页：RAG 检索方式可视化**：管理员「系统状态」页新增两行——「向量语义记忆」（开/关）与「图谱方式（Graph RAG）」（已生效 · 实体 N / 关系 M，或未启用），直观展示当前 RAG 是否使用图谱方式。

## New (English)
- **Knowledge-base Graph RAG**: knowledge-base documents are now also entity/relation-extracted into the isolated `kb:{KbId}` graph on ingest; knowledge retrieval performs semantic seed recall + n-hop traversal over the bound knowledge bases and injects the reachable subgraph alongside the vector chunks, augmenting relational knowledge in documents. KB graphs and group-memory graphs are domain-isolated and cross-contamination-free; deleting a KB also removes its graph.
- **System-status RAG visualization**: the admin "System Status" page now shows two rows — "Vector semantic memory" (On/Off) and "Graph mode (Graph RAG)" (Active · N entities / M relations, or Not enabled), making it clear whether RAG is currently using the graph approach.

---
文件：`AguiGroupChat-Desktop-1.0.77.msi`
File: `AguiGroupChat-Desktop-1.0.77.msi`
