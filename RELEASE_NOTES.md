# AG-UI 群聊桌面版 1.0.75 发布说明
# AG-UI Group Chat Desktop 1.0.75 Release Notes

## 修复（中文）
- **首个注册用户无法成为管理员**：旧版会在删除数据后重新播种内建的演示账号（`zhangsan`，且因其是"首个注册"而自动成为管理员；`lisi`），导致你注册的真实账号拿不到管理员权限。本版**彻底移除了演示账号播种逻辑**，删库重开后第一个注册的新账号即成为管理员。
- **数字员工管理列表空白**：新建/已有数字员工在列表中不显示，是前端国际化把标题里的动态计数徽标覆盖掉了，导致列表渲染中断。本版修复，列表与计数恢复正常。
- **已选群但无消息时误提示"选择一个群开始对话"**：进群后消息区为空时会误显示"选择群"的引导（语义像是在让你再去选群）。本版区分了"未选群"与"已选空群"：已选群无消息时改为提示"该知聚还没有消息，发第一条开始对话吧"，输入 / 发送 / 附件组件正常可用。
- **界面底部出现游离的 `""` 字符**：页脚残留了两个引号字符的零星文本，已清除。
- **知识库 RAG 检索不准 / 找不到信息**：① 切片窗口与重叠改为**可配置**（默认 **4096 字符** / 重叠 **512**，可在 `.env` 用 `MEMORY_KNOWLEDGE_CHUNK_SIZE` / `MEMORY_KNOWLEDGE_CHUNK_OVERLAP` 调整）；② 切分升级为**智能切分**，优先沿换行 / 句末标点收尾，避免在句子中间硬切切断语义，显著提升检索命中。
- 安装包同步内置最新前端国际化与登录态保持等改进。
- **知识库“有这个词但搜不到”**：纯语义检索对“专享福利假”等稀有词 / 长篇目录文本容易因相似度低于阈值而丢命中。本版新增**关键词召回兜底**：在切分命中集合内用 BM25 对查询词做第二次评分，把词面命中但向量漏掉的片段补回来，避免“明明有内容却回答没有”。
- **技能激活智能体时检索错知识库**：智能体 1 通过技能调用带知识库的智能体 2 时，原来会错误地检索智能体 1 的知识库（技能调用复用宿主环境上下文导致）。本版在技能调用期间把上下文切到**目标智能体**，智能体 2 会按自己的知识库检索后再回复。

## Fixes (English)
- **First registered user couldn't become admin**: the old build re-seeded built-in demo accounts (`zhangsan`, who automatically became admin as the "first registered" user; and `lisi`) after a data reset, so your real account never got admin rights. This version **completely removes demo-account seeding** - after clearing data, the first account you register is now the admin.
- **Agent management list appeared empty**: new/existing agents were not shown because the i18n text overwrite removed the title's dynamic count badge and broke list rendering. Fixed - the list and count now render correctly.
- **Selected empty group wrongly showed "select a group" prompt**: when a group had no messages, the empty area mistakenly showed the "select a group" guide (as if you had not picked one). Now it distinguishes "no group selected" from "an empty chosen group" - an empty chosen group shows "No messages yet in this group. Say hi to start the conversation", with the composer fully usable.
- **Stray `""` characters at the bottom of the UI**: leftover quote characters were rendered at the page footer; removed.
- **Knowledge-base RAG retrieval missed / failed to find info**: ① chunk window & overlap are now **configurable** (default **4096 chars** / **512 overlap**; adjust via `MEMORY_KNOWLEDGE_CHUNK_SIZE` / `MEMORY_KNOWLEDGE_CHUNK_OVERLAP` in `.env`); ② chunking upgraded to **smart splitting** that cuts at line breaks / sentence-ending punctuation instead of mid-sentence, notably improving retrieval recall.
- Bundles the latest frontend i18n and login-session persistence improvements.
- **KB has the term but `can't find it`**: pure vector search can drop hits for rare terms like `专享福利假` or long table-of-contents text when similarity falls below the threshold. This version adds a **keyword-recall fallback**: after retrieving candidate chunks, it re-scores them with BM25 against the query terms and recovers fragments that match by wording but were missed by the vector search - so a term that clearly exists in the document is no longer reported as `not found`.
- **Skill invocation searched the wrong knowledge base**: when agent 1 uses a skill to invoke a KB-enabled agent 2, the old build incorrectly searched agent 1's knowledge base (the skill call reused the host's ambient context). This version switches the context to the **target agent** during the skill call, so agent 2 retrieves from its own knowledge base and then replies.

## 使用提示（中文）
全新安装或想彻底重置：先完全退出桌面版，再删除 `%LocalAppData%\AguiGroupChat\data\` 目录后启动，第一个注册账号即为管理员。

## Usage Note (English)
For a clean start: fully quit the app, delete `%LocalAppData%\AguiGroupChat\data\`, then launch - the first account you register becomes the admin.

---
文件：`AguiGroupChat-Desktop-1.0.75.msi`（约 584 MB，已内置本地 embedding 模型）
File: `AguiGroupChat-Desktop-1.0.75.msi` (~584 MB, bundles the local embedding model)
