using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// 内置组织角色（挂载 org_design 者）的受控原生工具：把“磨好的最终稿”真正提交落库为库中一支，并支持按 team key 覆盖（库里只留最新一版）。
/// <b>仅平台管理员触发才执行写入</b>；普通用户调用只返回“请管理员放行”的提示，绝不写库。
/// </summary>
public sealed class OrgCommitTool
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    public OrgCommitTool(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _services = services;
        _logger = loggerFactory.CreateLogger<OrgCommitTool>();
    }

    /// <summary>
    /// 提交落库。参数：teamKey=该支组织的稳定钥匙（如 it_support 或某客服组）；planJson=与一键编排 apply 同构的最终方案 JSON。
    /// 内部校验触发者是平台管理员后才真正写库；否则仅返回预览与需管理员放行的说明。
    /// </summary>
    public async Task<string> Commit(
        [System.ComponentModel.Description("这支组织的稳定短钥匙（英文/数字/下划线，如 it_support）；同一把钥匙反复提交=覆盖更新、库里只留最新版")] string teamKey,
        [System.ComponentModel.Description("最终方案的 JSON 文本（字段对齐系统一键编排 apply：title/skills/agents；agents 内用 skillIds/assignmentIds/escalationAgentId/relayToAgentId）")] string planJson)
    {
        var ctx = AgentGateway.AmbientContext.Value;
        if (ctx is null) return "当前不在智能体运行上下文，无法落库组织。";
        var auth = _services.GetService<AuthService>();
        // 以“生效平台角色 ≥ Admin（含 SuperAdmin）”为准：避免只认 IsAdmin 标记把超管挡在外面。
        var isAdmin = auth != null && auth.ResolveRole(ctx.TriggerUserId) >= PlatformRole.Admin;
        if (!isAdmin)
            return "你当前没有写入组织的权限。已给出的是方案预览；如需真正落库，请平台管理员（本群的负责人/超管）把这份最终稿放行后再提交，本次不会写入任何数据。";

        var committer = _services.GetService<OrgTeamCommitter>();
        if (committer is null) return "受控组织提交服务暂不可用。";
        try
        {
            var (ok, msg) = await committer.CommitAsync(teamKey, planJson, ctx.TriggerUserId, true);
            return msg;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "org_commit 落库失败：team={Key}", teamKey);
            return "落库过程出错，未写入（已回滚未部分提交）：" + ex.Message;
        }
    }
}
