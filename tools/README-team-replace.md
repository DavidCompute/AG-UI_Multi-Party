# 团队整批替换（team-replace）操作说明

让「组织架构构建师」把一套组织方案反复磨稿，直至满意后，由**管理员**把该团队的现存对象整批替换为最终稿。不改平台安全边界：替换 = 先删旧批、再按最终稿整批落库（都走系统已验证的通道）。

## 闭环流程（推荐用法）

1. **出稿**：在群里 @组织架构构建师，先一句话说建什么团队，看首版方案；
2. **改稿**：之后随时提修改意见（删谁/新增谁/给谁加技能/调层级连线），它基于现状增量改，直到你满意；
3. **导出最终稿**：把最终认可的“角色 + 技能 + 连接”整理成一份 apply 兼容的 JSON（结构见下），存为 `plan.json`；
4. **整批替换**：管理员在本机执行脚本，把旧批换掉、把这份最终稿落成新实体；
5. **核对**：`/ag-ui/agents` 里旧的角色已消失、新的一批已出现；若开了 `createSupportCircle` 同步建好客服知聚。

> 你可以把同一个 `plan.json` 反复用于“同一支团队定期升级” —— 每轮把最终稿替换到库里即可；旧批名单会随之更新。

## 运行（需要管理员账号）

```bash
node tools/team-replace.mjs \
  --base http://localhost:5200 \
  --user <管理员> --pass <密码> \
  --old sales_agent,support_agent \
  --agent-file ./plan.json
```

- `--old`：要替换掉的**旧批**数字员工 ID 列表（逗号分隔）。
- `--agent-file`：最终方案的 JSON 文件（结构同 `POST /ag-ui/agents/orchestrate/apply`）。
- 删除在 apply 之前先进：**任何一个旧对象删失败，脚本会中止，不会落到一半**（不部分写入）。
- 只删旧批（不改稿）：省略 `--agent-file`；只落新稿（不删旧）：省略 `--old`。

## 反复修改同一支（推荐：`--name`，始终只留最新一版）

想让一支团队“改一版 → 落一版”，且**库里始终只有最新那一版**（不改到满地都是新一批），用 `--name` 给团队起个 key：

```bash
# ① 首次建一支（内部记下这一版产生的对象 id）
node tools/team-replace.mjs --base http://localhost:5200 --user david --pass 123456 \
  --name my-team --agent-file ./v1.json

# ② 再多轮改：同一 --name 会自动清掉上一版（含上一版为这支自建的技能），再按新稿落库
node tools/team-replace.mjs --base http://localhost:5200 --user david --pass 123456 \
  --name my-team --agent-file ./v2.json
```

- 每次“删上版→落新版”：数字员工会先被删除、再按最终稿整体重建；本支历史自建的技能也会被退役后再用**干净原始 id** 重建（不会累积出 `_2/_3`）。
- 状态记录在 `tools/.team-state/<key>.json`，同一支反复覆盖只靠同一个 `--name`，无需你手工记得旧 id。
- 若某版要把整支清掉（不留新版）：`node tools/team-replace.mjs --base … --name my-team`（不带 `--agent-file` 即只清不建）。

## plan.json 结构（apply 兼容）

```json
{
  "title": "IT 客服中心 v3",
  "createSupportCircle": false,
  "skills": [
    {
      "skillId": "ticket_router",
      "name": "工单路由",
      "description": "按问题分类分派工单",
      "kind": "prompt",
      "body": "请结合模板与请求直接综合作答。",
      "executionLocation": "server",
      "requiresApproval": false
    }
  ],
  "agents": [
    {
      "agentId": "front_desk",
      "nickname": "一线客服",
      "description": "客服一线入口",
      "instructions": "负责接单与初步定位。",
      "triggerMode": "mentioned",
      "skillIds": ["ticket_router"],
      "assignmentIds": [],
      "escalationAgentId": "l2_support",
      "relayToAgentId": null
    },
    {
      "agentId": "l2_support",
      "nickname": "二线技术支持",
      "description": "疑难升级处理",
      "instructions": "负责二线处理并回传。",
      "triggerMode": "mentioned",
      "skillIds": [],
      "assignmentIds": ["front_desk"],
      "escalationAgentId": null,
      "relayToAgentId": null
    }
  ]
}
```

要点：
- `skills[].kind`：`prompt` / `shell` / `http` / `dotnet`；其中 `shell`/`http`/`dotnet`（可执行任意命令/外部请求/运行 C#）**仅系统管理员可建**（apply 内部同样校验）。
- 角色内部的 `skillIds` / `assignmentIds` / `escalationAgentId` / `relayToAgentId` 用方案里的**原 id**即可，apply 会自行去重并重映射引用。
- 角色或技能同名：apply 自动追加 `_2/_3` 改名，不会覆盖你已有的其它资产。

## 提示
- 只影响你指定的旧批与最终稿，**不会**动系统里别的数字员工 / 技能（演示已验证 `org_architect` 不受影响）。
- 该脚本只做“删旧 + apply 落新”，保持平台原有的审批/权限护栏；对普通账号/共用账号用它慎、仅管理员掌握可视为配置变更。
