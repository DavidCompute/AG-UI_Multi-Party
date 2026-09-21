using System.Text.RegularExpressions;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.DependencyInjection;

namespace AguiGroupChat.Web;

/// <summary>
/// 附件生命周期的实现：算出“还有谁引用这个附件”，据此回收无人引用的文件。
///
/// <para>
/// 为什么放在 Web 项目：引用源横跨三层——消息与群成员 / 群头像在 Hub，知识库文档与数字员工头像在
/// Agents，技能试运行产物在 Web。只有 Web 同时引用 Hub 与 Agents，因此判定只能在这里做
/// （Hub 不能反向依赖 Agents；接口 <see cref="IAttachmentLifecycle"/> 定义在 Hub，实现在此）。
/// </para>
///
/// <para>
/// 这份“仍被引用”的集合是<b>回收的唯一安全边界</b>：少算一类引用就会删掉仍在用的文件。
/// 当前覆盖：消息附件（含撤回消息——撤回只是置标志、附件仍在消息上，且撤回后附件已不可达，
/// 这里**按引用算**，宁可不删）、用户 / 群成员 / 群 / 数字员工头像、知识库文档、技能试运行产物。
/// 图库图片与技能定义不引用 <c>att_</c>（图库是 <c>data/images</c> 下的另一套文件）。
/// </para>
/// </summary>
internal sealed class AttachmentLifecycle : IAttachmentLifecycle
{
    /// <summary>头像 URL 里的附件 ID（<c>/ag-ui/files/att_xxx/…</c>）。</summary>
    private static readonly Regex AvatarAttachmentPattern = new(
        @"/ag-ui/files/(att_[A-Za-z0-9_-]+)/", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AttachmentStore _store;
    private readonly IGroupStore _groups;
    private readonly IUserStore _users;
    private readonly AgentCatalog _agents;
    private readonly KnowledgeBaseCatalog _knowledgeBases;
    private readonly SkillRunArtifactStore _skillArtifacts;

    public AttachmentLifecycle(
        AttachmentStore store,
        IGroupStore groups,
        IUserStore users,
        AgentCatalog agents,
        KnowledgeBaseCatalog knowledgeBases,
        SkillRunArtifactStore skillArtifacts)
    {
        _store = store;
        _groups = groups;
        _users = users;
        _agents = agents;
        _knowledgeBases = knowledgeBases;
        _skillArtifacts = skillArtifacts;
    }

    public AttachmentStorageStats Inspect(TimeSpan gracePeriod)
    {
        var referenced = ReferencedIds();
        var cutoff = DateTime.UtcNow - gracePeriod;
        var total = 0;
        long totalBytes = 0, orphanBytes = 0, unreferencedBytes = 0;
        var referencedCount = 0;
        var orphanFiles = 0;
        var unreferencedFiles = 0;

        foreach (var id in _store.EnumerateAttachmentIds())
        {
            total++;
            var file = _store.ResolvePath(id);
            var size = file is null ? 0 : new FileInfo(file).Length;
            totalBytes += size;
            if (referenced.Contains(id)) { referencedCount++; continue; }

            // 无引用：分「仍在宽限期内」与「现在就能回收」两档分别计数。
            // 为何要分开报：文件是“先上传、后随消息发送”的，宽限期内的无引用文件不能删；
            // 但只报“可回收 0”会让管理员误以为“没有任何浪费”（实测：186 个无引用文件全落在 7 天宽限期内）。
            unreferencedFiles++;
            unreferencedBytes += size;
            if (file is not null && File.GetLastWriteTimeUtc(file) > cutoff) continue;
            orphanFiles++;
            orphanBytes += size;
        }

        return new AttachmentStorageStats(total, totalBytes, referencedCount,
            unreferencedFiles, unreferencedBytes, orphanFiles, orphanBytes,
            (int)gracePeriod.TotalHours);
    }

    public AttachmentReclaimResult DeleteIfUnreferenced(IReadOnlyCollection<string> attachmentIds)
    {
        // 只算一次引用集合：候选通常几十个，但引用集合要扫全库消息，避免逐个重复扫
        var referenced = ReferencedIds();
        var deleted = 0;
        long bytes = 0;
        var skipped = 0;
        foreach (var id in attachmentIds.Distinct(StringComparer.Ordinal))
        {
            if (referenced.Contains(id)) { skipped++; continue; }
            var file = _store.ResolvePath(id);
            var size = file is null ? 0 : new FileInfo(file).Length;
            if (_store.Delete(id)) { deleted++; bytes += size; }
        }
        return new AttachmentReclaimResult(deleted, bytes, skipped);
    }

    public AttachmentReclaimResult ReclaimOrphans(TimeSpan gracePeriod, bool dryRun)
    {
        var referenced = ReferencedIds();
        var cutoff = DateTime.UtcNow - gracePeriod;
        var deleted = 0;
        long bytes = 0;
        foreach (var id in _store.EnumerateAttachmentIds())
        {
            if (referenced.Contains(id)) continue;
            var file = _store.ResolvePath(id);
            if (file is null) continue;
            if (File.GetLastWriteTimeUtc(file) > cutoff) continue; // 宽限期内：可能“刚上传还没发送”
            var size = new FileInfo(file).Length;
            if (dryRun) { deleted++; bytes += size; continue; }
            if (_store.Delete(id)) { deleted++; bytes += size; }
        }
        return new AttachmentReclaimResult(deleted, bytes, 0);
    }

    /// <summary>仍被引用的附件 ID 集合（消息 + 头像 + 知识库文档 + 技能试运行产物）。</summary>
    private HashSet<string> ReferencedIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // 群列表只取一次：下面既要逐群扫消息，也要逐群取群头像与成员头像
        var groups = _groups.AllGroups();

        // 1) 消息附件（全部群、全部话题；含已撤回消息——撤回后附件已不可达，但此处按“仍被引用”处理，宁可不删）
        foreach (var group in groups)
            foreach (var message in _groups.AllMessages(group.GroupId))
                foreach (var att in message.Attachments ?? [])
                    if (!string.IsNullOrWhiteSpace(att.AttachmentId)) ids.Add(att.AttachmentId);

        // 2) 头像（用户 / 群 / 群成员 / 数字员工）：头像就是站内附件 URL
        foreach (var user in _users.ListUsers()) AddAvatarRef(user.Avatar, ids);
        foreach (var group in groups)
        {
            AddAvatarRef(group.GroupAvatar, ids);
            foreach (var member in _groups.ListMembers(group.GroupId)) AddAvatarRef(member.Avatar, ids);
        }
        foreach (var def in _agents.ListDefinitions()) AddAvatarRef(def.Avatar, ids);

        // 3) 知识库文档（把文档上传到知识库时，正文也存成站内附件）
        foreach (var kb in _knowledgeBases.ListAll())
            foreach (var doc in kb.Documents)
                if (!string.IsNullOrWhiteSpace(doc.AttachmentId)) ids.Add(doc.AttachmentId);

        // 4) 技能试运行产物（产出者本人可下载 / 预览）
        foreach (var artifact in _skillArtifacts.Snapshot())
            if (!string.IsNullOrWhiteSpace(artifact.AttachmentId)) ids.Add(artifact.AttachmentId);

        return ids;
    }

    private static void AddAvatarRef(string? url, HashSet<string> ids)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var m = AvatarAttachmentPattern.Match(url);
        if (m.Success) ids.Add(m.Groups[1].Value);
    }
}

/// <summary>附件治理（回收能力）的 DI 装配：Web 与桌面两个组合根都要调用。</summary>
public static class AttachmentGovernanceExtensions
{
    /// <summary>
    /// 注册 <see cref="IAttachmentLifecycle"/>。不注册时“同时删除附件”与存储回收一律不可用
    /// （不报错，只是不提供删除）；注册后判定口径见 <see cref="AttachmentLifecycle"/>。
    /// </summary>
    public static IServiceCollection AddAttachmentGovernance(this IServiceCollection services)
    {
        services.AddSingleton<IAttachmentLifecycle, AttachmentLifecycle>();
        return services;
    }
}
