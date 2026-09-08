# AG-UI 群聊界面详细设计说明书

> 适用版本：仓库 `main`（含数字员工单聊 `kind=direct`、头像默认占位、私密知聚锁角标等近期改动）。
> 本说明书描述**界面设计与交互**，与代码一一对应；实现以
> `src/AguiGroupChat.Web/wwwroot/index.html`（页面骨架）、`app.js`（交互逻辑）、`style.css`（视觉）、
> `i18n/{en,zh}.js`（文案）为准。后端协议细节见协议标准与 `README`。

---

## 1. 总体设计

### 1.1 范围与技术形态

- 单页应用（SPA）：一个 `index.html` + 原生 JavaScript `app.js`（无框架、无构建），`style.css` 负责全部样式。
- 通信：WebSocket（默认全双工）/ SSE（只读下行）+ HTTP（上行与管理）。
- 本地化：`en.js`（源）/ `zh.js`（镜像）双语字典，`t(key)` 运行时取词。
- 面向“多人群聊 + 多智能体协作”：侧栏列知聚，中央聊天，右栏成员；覆盖普通知聚、私密知聚、客服知聚（support）、数字员工单聊（direct）四类会话外观。

### 1.2 视觉与主题

- CSS 变量驱动双主题：`:root` 为深色默认，`html[data-theme="light"]` 覆盖浅色变量（见 `style.css` 顶部）。
- 主题切换：顶栏 `#themeBtn`（☀️/🌙）；选择持久化于 `localStorage["agui.theme"]`，应用启动即恢复。
- 语义色：`--accent`（主操作/我自己）、`--agent`（数字员工紫）、`--ok/--warn/--err`（在线/告警/错误）、`--panel*`（卡片层次）、`--muted`（次要文本）。
- 页面本体不滚动；滚动只发生在内部容器（群列表、成员、消息、管理列表）。
- 字号/布局：顶栏 44px 级；左列表条目约 40px；消息气泡最大宽度约 80% 容器。

### 1.3 页面骨架（index.html 三层结构）

```
topbar（品牌 + 顶栏操作）
├─ 品牌区      #brandEl：logo(#brandLogo) + 名称(#brandName) + 副标题(#brandSub)
├─ 操作区      #langBtn(中/EN) #themeBtn(深浅) #helpBtn(帮助)
│             #agentManageBtn(🤖 数字员工) #notifBtn(#notifBadge 通知)
│             #meChip(自己头像 #meAvatar + 昵称 #meNickname) #connStatus(在线点)
└─ 会话主区（三栏 flex）
   ├─ aside.left   ─ 知聚列表：#refreshGroupsBtn #createGroupBtn / #groupList
   ├─ main         ─ 聊天：#chatGroupName #chatGroupMeta / #searchBtn #groupSettingsBtn
   │                  #topicBar(话题) / #messages(虚拟滚动) / #typingRow
   │                  #mentionPicker #replyBar #attachList
   │                  输入：#visibilitySelect #mentionAllBtn #mentionChips #input
   │                  #discussBtn(多智能体讨论) #attachBtn #voiceBtn #canvasBtn #sendBtn
   │                  #chatResizer(输入区高度拖拽) #attachInput
   └─ aside.right  ─ 成员：#memberCount #refreshMembersBtn #addMemberBtn / #memberList
```

模态/抽屉均以覆盖层 div 挂在 `body` 下（如 `#agentModal`、`#orgModal`、`#skillModal`、`#kbModal`、
`#adminModal` 及各设置弹窗），用 `hidden` class 控制显隐。

### 1.4 登录 / 注册与会话

- 覆盖层 `#authOverlay`：登录/注册页签（`#authTabLogin`/`#authTabRegister`），品牌区、`#authLangBtn`、表单
  `#authUsername/#authPassword/#authNickname`、`#authRemember`（保持登录状态）、`#authSubmit`、`#authError`。
- 行为：
  - 注册即登录；首个注册账号自动成为管理员（并自举平台 SuperAdmin），新账号默认 User。
  - 会话令牌存 `agui.auth`：勾选“保持登录”写 `localStorage`（跨重启），否则 `sessionStorage`。
  - 启动 `tryRestoreSession()`：读本地凭据→`GET /ag-ui/user/me` 恢复角色/头像，失败(401)则回登录页；
    自动带上回上次知聚（`agui.lastGroup.<uid>`）与该群最后话题。
- 登出（`#meMenuLogout`）：清 `agui.auth` 与本地会话态，服务端吊销令牌并断开该账号实时连接。

### 1.5 实时连接与断线重连

- 登录后建立 WS（`/ws?token=`），服务端 `GROUP_CONNECTED` 握手；顶栏 `#connStatus` 显示在线/离线。
- 断线自动指数退避重连（`state.reconnectDelay`），重连后恢复：
  - 重新订阅 `subscribedGroups`（快照回来前不重复建 rooms）；
  - 已订阅群重新进入取快照 `GROUP_STATE_SNAPSHOT`，未读以服务端为准刷新。
- 会话吊销 / 改密 / 账号禁用：服务端会立即断开既有实时连接，前端回登录态。

### 1.6 顶栏用户菜单（#meMenu）

按登录态与角色显示：`个人资料 / 修改密码 / 模型配置`；`管理控制台 / 系统状态`（Admin+）；
`记忆管理`、`白标设置`、`数据备份`（管理员）；`退出`。普通 User 只看到个人项。

### 1.7 通知中心（#notifPanel）

- 由 `#notifBtn`（带未读 `#notifBadge`）开合；`#notifList` 内聚合应用内通知（审批卡结果、编排冒烟结论、
  出错回执、定时任务等），点击通知可跳转其来源知聚；`#notifClear` 全部已读。

---

## 2. 主会话界面

### 2.1 左侧：知聚列表

数据：`GET /ag-ui/member/{me}/groups`（活跃排序由 `groupUnread.lastMessageAt` 实时维护）。

群条目结构（`renderGroupList`）：

| 视觉位 | 说明 |
|---|---|
| 头像/图标 | 有 `groupAvatar` → 圆形图；无 → 类别图标：客服知聚 🛟、单聊(direct) 💬、普通 👥 |
| 私密锁 | 仅私密知聚：🔒 角标**内嵌头像右下角**，占头像约 1/4 面积（`#.private-lock`，右下一角小圆），群名前不再加“🔒 ” |
| 群名 | `.group-name` 文本；过长省略号，hover title 完整名 |
| 标签 | 客服知聚 `support-tag`；非成员未进入的客服知聚右上 `进入` 小标（`.support-enter`） |
| 未读 | 有未读时 `.unread-badge`（>99 显示 99+） |
| 计数 | `.count` 成员数 |

- `+` 新建知聚（`#createGroupBtn`）：弹窗选人（勾选成员，支持搜索）、填名；不填名可 AI 生成；
  可开“🔒 私密知聚”；客服知聚需在创建类型中选。加人/建群后自动为数字员工注册触发规则。
- 点击条目 `selectGroup(groupId)`：先处理客服知聚进入/直聊补入，再订阅 + 拉快照 + 重置话题记忆，进入聊天。
- 客服知聚若发现但未加入，会被追加进列表并可“进入”（见 §7）。

### 2.2 中间：聊天区

- 顶栏：`#chatGroupName`（客服知聚前缀“🛟”，私密前缀“🔒”，普通为群名）+ `#chatGroupMeta`
  （知聚主 · 我的身份；客服知聚附加会话提示）+ 右侧 `搜索`、`知聚设置`。
- `#topicBar`：话题切换（主话题 `main` 恒在），可新建、“以此消息新建话题”、清空/删除话题（管理员）。
- 消息流 `#messages`：虚拟滚动渲染（固定行高估算 + 底部吸附自动跟随），新消息/流式增量自动贴底；
  上下留白/加载旧消息按需分页。
- 单条消息（`msgDom`）：
  - 头像：对端有头像显示图；无则默认 emoji（用户 🧑 / 数字员工 🤖）；头像失败隐藏（回退到默认）。
  - 头部：发送者昵称 + 时间 + 动作（引用回复 / 复制 / 停止生成(流式中) / 重新回答(智能体) / 撤回(本人3分钟内) /
    “以此新建话题”）。
  - 正文：支持 Markdown（标题/列表/表格/代码块/引用），先经 DOMPurify 消毒再渲染，外链 `_blank`。
  - 智能体消息扩展：`thinking` 可折叠思考块、`plan-card`（编排计划步骤卡片）、技能调用链卡片、
    `[工具] 调用中…→完成收起`、审批卡（HITL）、附件块（图片网格/音频条/文件）。
  - 撤回后原地置灰并显示“已撤回”。
- `#typingRow`：正在输入指示（客服知聚按角色隔离）。
- 右侧聊天/输入区可拖动分割线：`#chatResizer` 调节输入区高度（记忆 `agui.chatResizerH`）。

### 2.3 输入区与富媒体

- 文本域 `#input`：Enter 发送（Shift+Enter 换行）；`#mentionChips` 显示当前 @ 选择，点击 `#mentionChips` 内成员可移除；
  `#mentionAllBtn` 切换 @ 全体；输入 @ 时弹出 `#mentionPicker`（搜索可用成员/数字员工/分身，方向键+Enter 选择）。
- 可见性选择 `#visibilitySelect`：`all` / `mentioned` / `private`（与服务端协议一致；私聊/定向可见列表由前端勾选）。
- 快捷按钮：`#discussBtn`（多位数字员工讨论）、`#attachBtn`（附件：图片可多选/拖拽、文档等）、
  `#voiceBtn`（按住录音，`#voiceStatus/#voiceTimer/#voiceCancel`）、`#canvasBtn`（内置画布标注→导出 PNG 图片附件）、`#sendBtn`。
- 附件上传后 `#attachList` 预览；图片消息内平铺网格；音频以内联播放条展示。
- 引用回复：`#replyBar` 显示被引目标（点 × 取消）；消息内“回复”按钮可发起引用。
- 发送后保留本群 @ 选择记忆（不跨群）；切知聚恢复。

### 2.4 右侧：成员栏

- `#memberCount`、`#refreshMembersBtn`、`#addMemberBtn`（群主/管理员），`#memberList` 成员条目：
  - 头像（无 → 默认 emoji；分身 🪞）+ 右下状态点（在线/离线）+ 角色（owner/admin）+ AI 标签；
  - 在线离线互斥：用户在线时本人显示、分身暂停（🪞 影分身代班规则）；@ 自己可临时召唤分身。
  - 数字员工行可设置“本群触发方式”（`#gtTriggerMode` 等，管理员/群主）。
  - 客服知聚只列客服成员；顾客为非成员参与者，不出现在成员栏。

---

## 3. 头像与默认占位规范（全局）

- 令牌化 URL：站内单斜杠附件路径由 `authedAssetUrl()` 追加 `?token=`；外部 http(s)/data:/blob: 直用。
- 没有自定义头像时的默认显示（**各处必须可见，不许空白**）：

| 场景 | 占位 |
|---|---|
| 用户成员 | 🧑（成员列表 / 提及选择 / 建群勾选 / 消息气泡） |
| 数字员工成员 | 🤖 |
| AI 分身（`twin_`） | 🪞 |
| 数字员工管理行 | 圆形 🤖（`.agent-avatar-ph`） |
| 知聚列表无群头像 | 👥；客服 🛟；单聊 💬 |
| 头像图片加载失败 | 移除 img（其后仍落回默认占位/图标） |

实现：`memberAvatarHtml()` 统一生成 `<span class="member-avatar">[img|avatar-ph]+状态点</span>`；
`memberStatusIconHtml()` 负责叠加右下状态点。头像选择控件预览无图显示默认 emoji。

---

## 4. 数字员工（智能体）管理

入口：顶栏 `#agentManageBtn` → `#agentModal`（大模态，含列表/表单两个视图）。

### 4.1 列表视图（#agentListView）

- 工具栏：搜索 `#agentSearch` 独占整行（右侧带 “×” 清除，输入后点击即清空并恢复列表），其下为
  `#agentOrgBtn`（组织架构图）、`#agentOrchBtn`（一键编排）、`#agentSkillLibBtn`（技能库）、导入 JSON
  `#agentImportBtn`、导出全部、恢复内置组织工具（管理员）、批量删除模式 `#agentBatchBtn`、新增 `#agentAddBtn`。
- 行（`renderAgentList`）：头像/占位 + 昵称 + AI 标签 + 🔒(私密) + 🧩技能数 + 📚知识库数 + 描述 +
  id + 触发方式标签 + 桥接标签 + 关键词 + 模型 + 创建者（系统/我/他人）+ 行操作：
  - `📤` 导出单条 JSON；
  - `💬` **单聊**（与数字员工 1:1，见 §4.4）；
  - 创建者/管理员：`✏️` 编辑、`🗑️` 删除（两步确认）；系统内置（无 ownerId）只读不显示编辑/删除。
- 批量模式：勾选可管理项→批量删除。

### 4.2 表单视图（#agentFormView，可折叠分区）

- 基本（昵称/头像上传/AgentId(新建可填)/一句话简介，可“✨ 生成角色设定”）；
- 角色（Instructions 人设）；
- 触发（提及/全量监听/关键词/语境 + 关键词 + 覆盖模型）；
- 定时（Schedule cron，UTC 5 段）；桥接（外部 AG-UI 端点，仅管理员）；技能（挂载技能/子员工/知识库）；杂项
  （个人记忆、私密智能体）；执行阶段（关闭本员工桥接/交接/组织路由）。
- “技能与知识”区行为：
  - 可复用技能（技能库）每行 = 勾选挂载 + 右侧 `✏️ 查看 / 编辑` 入口（`openAgentSkillViewer`），点击直达技能库编辑器；
    编辑器内“返回 / 保存技能”会直接回到本表单（不进技能库列表），并刷新挂载列表。
  - “可调用子数字员工”除手工勾选外，**组织架构已连线（指派 / 提升 / 交接）的目标自动并入勾选**（保存后作为可调用技能生效），
    保证「组织架构」里定义的连接在表单中一致呈现；取消勾选仅移除以子技能方式调用，不影响组织连线。
- 折叠状态记忆 `agui.agentFormSections`。保存走 PUT（全量），携带既有交接/流水线/审批名单不丢失。
- 界面约定：全部搜索输入框（数字员工 / 技能库 / 记忆 / 成员选择 / 消息搜索）右侧均有 “×” 一键清空（`common.clearSearch`）。

### 4.3 一键编排与组织架构

- `#agentOrchBtn` → `#orgOrchModal`：输入一句话需求 → SSE 流式生成方案（`#orgOrchPreview/#orgOrchStream`，
  实时统计岗位/技能）→ 预览 → 确认落库（服务端冒烟自测+自动修复），可勾选“同时创建客服知聚”。
- `#agentOrgBtn` → `#orgModal` 画布（`#orgCanvas` + `#orgSvg`）：拖拽连线（向下指派/向上提升/中继），
  节点横纵坐标持久化 `agui.orgLayout.<uid>`；双击节点直接进入该员工编辑（保存/取消回到画布）；
  节点右上“优化指派”按钮生成下一层指引并可“追加到 Instructions”(`#orgOptModal`)。
- 节点上员工在回复时触发“协调计划/递归补查”，界面以计划卡与子调用链呈现。

### 4.4 数字员工单聊（kind=direct）

- 入口：数字员工管理列表每行的 **💬** 按钮 → `startDirectChat()` 调 `POST /ag-ui/agents/direct`。
- 模型：为该用户与该数字员工建立/复用**独立的私有双人群**（服务端确定性群 ID），不同用户互不共享、彼此隔离；
  群默认私密，记忆仅本私群可检索。
- 头像：单聊会话头像 = 对端数字员工头像（建群即写入群头像；对端换头像在复用进入或资料同步时自动更新）。
  对端无头像时列表图标为 💬。
- 聊天：进入后即普通聊天窗；**发送普通（未 @）消息 = 对其直达触发**（免手动 @），对端以真实模型回复。
- 若已存在单聊，再次点击进入同一会话（幂等）。

---

## 5. 技能库 / 知识库（数字员工子能力）

- `#skillModal`：列表/表单双视图。列表支持搜索、多选批量删除、新建（`#skillAddBtn`）、“自然语言生成技能”。
- 技能表单：名称/SkillId/类型（prompt/shell/http/dotnet）/描述/正文/执行位置（server/client）/
  解释器/需审批开关；试运行（`#sfTest`）结果弹 `#skillRunResultModal`；shell 始终需审批；
  client 技能在本机桥执行，需发起用户批准。
- `#kbModal`（知识库）：创建/管理知识库、上传文档，文档异步向量化入库（状态轮询）。

---

## 6. 客服知聚（support circle，kind=support）

- 外观：列表项带“🛟 客服”标签；若已加入成员列表在群内可直接进入；未加入的（非成员顾客）显示“进入”小标，
  点击调进入接口登记为参与者（不占成员名额）。
- 聊天隔离规则（服务端强制）：
  - 客服 = 群成员（可看全部会话）；顾客 = 非成员参与者，仅见自己与客服的会话；
  - 消息/已读/未读/typing 均按该规则在前端展示（客服看到多顾客；顾客只见自己的）。
  - 顾客可批准自己触发的客服技能（审批卡仅自己可见/可操作）。
- 界面提示：聊天标题/`renderChatMeta` 附“客服会话隔离”提示；客服回复以定向会话串形式出现在该顾客侧。

---

## 7. 我的菜单与管理功能

### 7.1 个人资料（#profileModal）

- 昵称、头像（上传/清除，预览即时，`bindAvatarPicker`）、个人记忆开关（🧠）、AI 分身状态管理
  （启用/触发方式/停用/同步公开群）。
- 头像更换会同步到各群成员资料与实时事件。
- 本机桥（#bridgeAdminBox 内的下载/状态区，所有登录用户可见）：“本机(client)”技能需在发起
  请求的电脑运行本机桥。弹窗顶部为<b>本机桥连接状态行</b>（`#bridgeLocalState`：未检测到 /
  待配置 / 连接中 / 已连接本平台（含 client）/ 仍连着另一平台），旁有“🔄 重新检测并连接”
  （`#bridgeLocalRetry`）与 Windows 安装包（.msi）下载按钮 `#bridgePkgDownload`（直链
  `/ag-ui/native-bridge/download/file`）；管理员额外可见“上传/更新安装包（.msi）”（`#bridgePkgFile`
  + `#bridgePkgUploadBtn` → `POST /ag-ui/native-bridge/download/upload`，仅管理员）与已签发连接令牌
  管理（`#bridgeIssuedList`，`GET/POST /ag-ui/native-bridge/download/tokens[/revoke]`，仅管理员）。
- <b>连接模型（登录即连、登出即断）</b>：MSI 为通用安装包（免装 .NET 运行时；安装即注册 HKCU 开机
  自启，桥以待配置模式启动，回环监听 127.0.0.1:17321）。用户登录/打开资料页时，前端自动发现同机
  桥：若桥未配置本平台或仍连在别的平台，先经 `POST /ag-ui/bridge/teardown` 断开清旧配置，再领取
  一枚 setup 令牌（`POST /ag-ui/native-bridge/download/setup-token`，note=`setup:{userId}`，每次领取
  吊销旧令牌、绑定型、可吊销）并 `POST /ag-ui/bridge/setup {server, setupToken}` 下发本平台地址，
  桥连入；登出时 `POST /ag-ui/native-bridge/download/setup-token/revoke` 吊销该用户全部 setup 令牌，
  并 `POST /ag-ui/bridge/teardown` 让桥断开、清除本机配置。连接只在首次连入时经
  `NativeTunnelApi` 鉴权绑定 client 机器标识，不涉及全站明文隧道令牌（网页/剪贴板/安装包内均不出现）。

### 7.2 修改密码（#pwModal）

旧密码 + 新密码；成功后会吊销旧会话（其它端需重登，本端实时连接由服务端主动断开）。

### 7.3 模型配置（#mcModal / 用户菜单）

运行时填 DeepSeek Endpoint / API Key（留空回退环境变量），保存即时生效并持久化；未配置时登录自动弹出；
apiKey 不回显，仅提示“已配置”。

### 7.4 记忆管理（#memModal）

查看/搜索/遗忘语义记忆、图谱，按群筛选。

### 7.5 数据备份 / 初始化（#backupModal，管理员）

导出全部 zip（账号+员工+群+消息+附件+技能库+组织簿记）；导入（上传 zip→预览勾选群→恢复，附存在性检查）；
危险区“初始化（清空一切）”——输入“确认”后清空数据并跳登录。

### 7.6 管理控制台（#adminModal，按角色分级）

- 用户管理（列表/角色/禁用/重置密码/平台角色授予）；系统状态与用量、审计、桥健康/能力、执行参数（在线热改）、
  配置治理、白标设置；权限随角色（User 无管理菜单 / Operator 只读 / Admin+ 完整 / SuperAdmin 角色管理）。
- 管理页按生效角色显示，角色下拉仅 SuperAdmin 可见。

---

## 8. 本地持久化键一览

| 键 | 用途 |
|---|---|
| `agui.auth` | 会话（token/memberId/nickname） |
| `agui.theme` | 深/浅主题 |
| `agui.lastGroup.<uid>` | 上次进入的知聚 |
| `agui.topicMem.<uid>` | 各群上次话题（groupId→topicId） |
| `agui.orgLayout.<uid>` | 组织架构画布节点坐标 |
| `agui.agentFormSections` | 数字员工表单折叠状态 |
| `agui.skillError.<uid>` | 技能试运行错误本地备查 |
| `agui.chatResizerH` | 输入区高度 |

`agui.theme`/`agui.auth` 之外的用户个性化键均按 `memberId` 隔离。

---

## 9. 事件驱动的界面刷新（重点机制）

- 消息生命周期：`TEXT_MESSAGE_START → CONTENT*(增量) → END`；前端用 `msgIndex` 跨群定位流式消息；
  `Reasoning` 走独立思考通道；`plan/chain/interaction` 事件渲染卡片；实时未读、typing、成员更新、
  已读回执等按事件增量维护，避免整屏重绘。
- 群列表与话题未读随 `onMessageStart/Read` 增量更新；重连/进入时以快照 + 服务端未读为准。
- 角色/在线/分身事件联动成员列表；删除/解散即时移除本地 room 并回落到“选择知聚”空态。
- 技能执行客户端：审批/一键执行经交互卡 + 本机桥/tunnel 回灌继续（卡片点亮/递归计划 UI）。

---

## 10. 本地化与文案维护规范

- 静态文案：`index.html` 用 `data-i18n`/`data-i18n-title`/`data-i18n-placeholder`；
  动态文案：`app.js` 用 `t("key", {param})`；新 key 必须同时加入 `en.js` 与 `zh.js`。
- CI/本地校验：`node .github/workflows/check-i18n-keys.js`（en/zh 对称）、
  `check-orphan-keys.js`（无孤儿 key）必须通过。
- 头像默认 emoji、锁角标等视觉 token 在 `style.css` 顶部/相应组件段统一管理，勿散落内联。

---

## 11. 与后端的接口概览（界面相关）

| 用途 | 端点（节选） |
|---|---|
| 认证/资料 | `/ag-ui/user/register|login|logout|me|profile|password` |
| 群 | `/ag-ui/group/create|update|disband|enter`、快照 `GET /ag-ui/group/{id}`、成员/话题/消息 |
| 实时 | `WS /ws`、`SSE /sse`（Bearer/`?token=`） |
| 数字员工 | `/ag-ui/agents`(GET/POST)、`/{id}`(PUT/DELETE)、`/register`、`/direct`（单聊） |
| 组织编排 | `/ag-ui/agents/orchestrate(/stream)`、`/optimize-assignment` |
| 记忆/搜索/附件 | `/ag-ui/memory/*`、`/ag-ui/upload`、`/ag-ui/files/*`、`/ag-ui/group/search` |
| 管理 | `/ag-ui/admin/*`（用户/角色/执行/治理/状态/审计/桥）、`/ag-ui/settings/model|branding` |
| 本机桥安装包/在线配置 | `/ag-ui/native-bridge/download/info|file`（登录用户）、`upload`（仅管理员）、`tokens|tokens/revoke`（仅管理员）、`setup-token|setup-token/revoke`（登录用户）、本机回环 `GET/POST /ag-ui/bridge/info|setup|teardown` |
| 系统 | `/ag-ui/export|import|import/preview|reset` |

完整契约以协议标准与 README 为准；界面只消费上述接口，语义（可见性/触发/权限）始终由服务端强校验。

---

## 12. 维护说明

- 新增会话类型/功能时，请同步更新：群类别图标（`renderGroupList` fallback）、头像占位规则（§3）、
  若有新文案按 §10 维护，并在本文档对应小节补充。
- 布局/样式改动尽量复用 CSS 变量与既有组件类（`.chip-btn/.icon-btn/.modal-input/.member-avatar/.group-avatar/.msg` 等），
  保持深浅主题双写一致。
