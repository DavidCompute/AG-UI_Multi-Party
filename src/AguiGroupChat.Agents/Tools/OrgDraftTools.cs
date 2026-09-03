using AguiGroupChat.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// 组织设计稿草稿工具：给组织编排类智能体（挂载 <c>org_design</c> 技能者）的一簇内置工具，
/// 让它们在群内以「slug 命名草稿」保存 / 取回一份或多份组织方案，从而能在跨话题、消息被挤出上下文窗口后
/// 仍可靠地继续多轮修改。</summary>
/// </remarks>
/// 纪律由智能体指令与 <c>org_design</c> 技能共同约束：草稿只表达「方案文本」，不自动落库创建真组织 /
/// 技能；真正创建仍须管理员在一键编排中人工确认。</remarks>
public sealed class OrgDraftTools
{
    private const int MaxTitleChars = 200;
    private const int MaxContentChars = 40_000; // 组织方案正文上限（够容纳完整岗位+技能+连接的结构化设计稿）
    private const int MaxDraftsPerGroup = 60;

    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    public OrgDraftTools(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _services = services;
        _logger = loggerFactory.CreateLogger<OrgDraftTools>();
    }

    private OrgDraftStore? Store() => _services.GetService<OrgDraftStore>();

    private static string CleanSlug(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        // 兼容简单中文 / 数字 / 连字符降级：仅保留小写字母数字与连字符下划线，其余折叠为连字符
        var kept = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_') kept.Append(c);
            else kept.Append((c is ' ' or '\t') ? '-' : '\0');
        }
        var slug = new string(kept.ToString().Where(ch => ch != '\0').ToArray());
        return slug.Length > 40 ? slug[..40] : slug;
    }

    /// <summary>保存一份组织 / 客服 / 办公团队的设计稿为草稿（同 slug 覆盖并升级版本），返回保存结果。</summary>
    public string SaveDraft(
        [System.ComponentModel.Description("草稿短标识（英文/数字，标识这份组织方案，如 it_support_v1）。同一群内同 slug 会覆盖旧版本。")]
        string slug,
        [System.ComponentModel.Description("标题（组织 / 团队名称），如「IT 客服中心」")] string title,
        [System.ComponentModel.Description("结构化组织方案正文（岗位 + 每岗技能 + 连接关系，Markdown / JSON）。")] string content)
    {
        var ctx = AgentGateway.AmbientContext.Value;
        if (ctx is null) return "当前不在智能体运行上下文，无法保存草稿。";
        var draftSlug = CleanSlug(slug);
        if (draftSlug.Length == 0) return "草稿标识不合法：请提供字母/数字/下划线/连字符的短标识（≤40 字符）。";
        title = (title ?? "").Trim();
        content = (content ?? "").Trim();
        if (title.Length == 0) title = draftSlug;
        if (title.Length > MaxTitleChars) title = title[..MaxTitleChars];
        if (content.Length == 0) return "草稿正文为空，无法保存。";
        if (content.Length > MaxContentChars) content = content[..MaxContentChars];
        var store = Store();
        if (store is null) return "草稿库暂不可用（服务未启用），无法保存。";

        // 群内草稿总量上限保护：超限时不自动新建（避免存档自增失控），提示先用列表确认或删除旧稿
        var summaries = store.List(ctx.GroupId);
        var exists = summaries.Any(s => string.Equals(s.Slug, draftSlug, StringComparison.Ordinal));
        if (!exists && summaries.Count >= MaxDraftsPerGroup)
            return $"本群草稿已达上限（{MaxDraftsPerGroup}）。请先用 org_draft_list 查看，删除不用的草稿后再保存。";

        var saved = store.Save(ctx.GroupId, draftSlug, ctx.TopicId, ctx.TriggerUserId, title, content);
        return $"已保存草稿「{draftSlug}」版本 v{saved.Version}（标题：{saved.Title}，{saved.Content.Length} 字）。后续可用 org_draft_load slug={draftSlug} 取回继续修改。";
    }

    /// <summary><c>org_draft_list</c>：列出本群已保存的组织方案草稿（仅摘要），供挑选后 load。</summary>
    public string ListDrafts()
    {
        var ctx = AgentGateway.AmbientContext.Value;
        if (ctx is null) return "当前不在智能体运行上下文，无法读取草稿。";
        var store = Store();
        if (store is null) return "草稿库暂不可用。";
        var list = store.List(ctx.GroupId);
        if (list.Count == 0) return "本群还没有保存过组织方案草稿。先让用户描述需求并产出方案后，调用 org_draft_save 保存即可。";
        return "本群已有组织方案草稿：\n" + string.Join("\n", list.Select(d =>
            $"- slug={d.Slug}｜标题：{d.Title}｜版本 v{d.Version}｜更新 {DateTimeOffset.FromUnixTimeMilliseconds(d.UpdatedAtMs).ToLocalTime():MM-dd HH:mm}"));
    }

    /// <summary>取回本群指定 slug 的组织方案草稿全文（用于基于旧稿修改后再次 save 覆盖升级）。</summary>
    public string LoadDraft(
        [System.ComponentModel.Description("要取回的草稿短标识，如 it_support_v1")] string slug)
    {
        var ctx = AgentGateway.AmbientContext.Value;
        if (ctx is null) return "当前不在智能体运行上下文，无法读取草稿。";
        var draftSlug = CleanSlug(slug);
        if (draftSlug.Length == 0) return "请提供草稿短标识（org_draft_list 可见）。";
        var store = Store();
        if (store is null) return "草稿库暂不可用。";
        var draft = store.Load(ctx.GroupId, draftSlug);
        if (draft is null) return $"未找到草稿「{draftSlug}」。可用 org_draft_list 查看本群全部草稿。";
        return $"草稿「{draftSlug}」（版本 v{draft.Version}，标题：{draft.Title}，最后修改 {DateTimeOffset.FromUnixTimeMilliseconds(draft.UpdatedAtMs).ToLocalTime():yyyy-MM-dd HH:mm}）：\n\n{draft.Content}";
    }

    /// <summary>删除本群指定 slug 的组织方案草稿（清除后不可恢复）。</summary>
    public string DeleteDraft(
        [System.ComponentModel.Description("要删除的草稿短标识")] string slug)
    {
        var ctx = AgentGateway.AmbientContext.Value;
        if (ctx is null) return "当前不在智能体运行上下文，无法删除草稿。";
        var draftSlug = CleanSlug(slug);
        if (draftSlug.Length == 0) return "请提供草稿短标识。";
        var store = Store();
        if (store is null) return "草稿库暂不可用。";
        return store.Delete(ctx.GroupId, draftSlug)
            ? $"已删除草稿「{draftSlug}」。"
            : $"未找到草稿「{draftSlug}」，无需删除。";
    }
}
