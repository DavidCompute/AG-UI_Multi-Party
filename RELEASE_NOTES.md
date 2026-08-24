# AG-UI 群聊桌面版 1.0.75 发布说明
# AG-UI Group Chat Desktop 1.0.75 Release Notes

## 修复（中文）
- **首个注册用户无法成为管理员**：旧版会在删除数据后重新播种内建的演示账号（`zhangsan`，且因其是"首个注册"而自动成为管理员；`lisi`），导致你注册的真实账号拿不到管理员权限。本版**彻底移除了演示账号播种逻辑**，删库重开后第一个注册的新账号即成为管理员。
- **数字员工管理列表空白**：新建/已有数字员工在列表中不显示，是前端国际化把标题里的动态计数徽标覆盖掉了，导致列表渲染中断。本版修复，列表与计数恢复正常。
- **已选群但无消息时误提示"选择一个群开始对话"**：进群后消息区为空时会误显示"选择群"的引导（语义像是在让你再去选群）。本版区分了"未选群"与"已选空群"：已选群无消息时改为提示"该知聚还没有消息，发第一条开始对话吧"，输入 / 发送 / 附件组件正常可用。
- 安装包同步内置最新前端国际化与登录态保持等改进。

## Fixes (English)
- **First registered user couldn't become admin**: the old build re-seeded built-in demo accounts (`zhangsan`, who automatically became admin as the "first registered" user; and `lisi`) after a data reset, so your real account never got admin rights. This version **completely removes demo-account seeding** - after clearing data, the first account you register is now the admin.
- **Agent management list appeared empty**: new/existing agents were not shown because the i18n text overwrite removed the title's dynamic count badge and broke list rendering. Fixed - the list and count now render correctly.
- **Selected empty group wrongly showed "select a group" prompt**: when a group had no messages, the empty area mistakenly showed the "select a group" guide (as if you had not picked one). Now it distinguishes "no group selected" from "an empty chosen group" - an empty chosen group shows "No messages yet in this group. Say hi to start the conversation", with the composer fully usable.
- Bundles the latest frontend i18n and login-session persistence improvements.

## 使用提示（中文）
全新安装或想彻底重置：先完全退出桌面版，再删除 `%LocalAppData%\AguiGroupChat\data\` 目录后启动，第一个注册账号即为管理员。

## Usage Note (English)
For a clean start: fully quit the app, delete `%LocalAppData%\AguiGroupChat\data\`, then launch - the first account you register becomes the admin.

---
文件：`AguiGroupChat-Desktop-1.0.75.msi`（约 584 MB，已内置本地 embedding 模型）
File: `AguiGroupChat-Desktop-1.0.75.msi` (~584 MB, bundles the local embedding model)
