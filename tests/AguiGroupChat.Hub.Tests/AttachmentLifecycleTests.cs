using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using AguiGroupChat.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 附件回收的<b>安全边界</b>：只有“确实无人引用”的文件才能删。
///
/// <para>
/// 为何单独钉住：清空 / 删除话题、撤回消息、账号擦除都只删消息、不删附件文件（刻意的：附件是用户资料），
/// 于是磁盘上会积累孤儿。回收能力一旦把“仍在被引用”的文件当孤儿删掉，就是**静默数据丢失**：
/// 附件可能还被消息、知识库文档、用户 / 群 / 群成员 / 数字员工头像、技能试运行产物引用。
/// 这里逐类验证：引用在 → 一个字节都不删。
/// </para>
/// </summary>
public sealed class AttachmentLifecycleTests : IDisposable
{
    private const string ByMessage = "att_bymessage00001";
    private const string ByKb = "att_bykb000000001";
    private const string ByAvatar = "att_byavatar00001";
    private const string BySkillRun = "att_byskillrun001";
    private const string Orphan = "att_orphan0000001";

    private readonly string _dir;
    private readonly AttachmentStore _store;
    private readonly InMemoryGroupStore _groups = new(historyLimit: 100);
    private readonly IAttachmentLifecycle _lifecycle;

    public AttachmentLifecycleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "agui-attlife-" + Guid.NewGuid().ToString("N")[..8]);
        _store = new AttachmentStore(_dir);
        foreach (var id in new[] { ByMessage, ByKb, ByAvatar, BySkillRun, Orphan })
            _store.RestoreFile(id, id + ".txt", [1, 2, 3]);

        var users = new InMemoryUserStore();
        var options = new AgentOptions { Provider = "mock" };
        var services = new ServiceCollection().BuildServiceProvider();
        var agents = new AgentCatalog(options, NullLoggerFactory.Instance, services);
        var kbs = new KnowledgeBaseCatalog(options, services, NullLoggerFactory.Instance);
        var artifacts = new SkillRunArtifactStore();

        // 三类上层引用源：消息 / 群成员与群头像 / 知识库 / 技能产物
        _groups.AddGroup(new Group
        {
            GroupId = "g1", GroupName = "g1", OwnerId = "user_1", CreateTime = 1,
        });
        _groups.AddMessage(new GroupMessage
        {
            MessageId = "msg_1", GroupId = "g1", ThreadId = "g1", SenderId = "user_1",
            SenderType = MemberType.User, SenderNickname = "u", Content = "带附件",
            Timestamp = 1,
            Attachments = [Info(ByMessage)],
        });
        _groups.AddGroup(new Group
        {
            GroupId = "g2", GroupName = "g2", OwnerId = "user_1", CreateTime = 1,
            GroupAvatar = $"/ag-ui/files/{ByAvatar}/x.png",
        });

        var kb = kbs.CreateKb("kb1", "描述", "user_1");
        kb.Documents.Add(new KbDocument { DocId = "doc_1", FileName = "d.docx", AttachmentId = ByKb });
        artifacts.Register(BySkillRun, "user_1");

        var provider = new ServiceCollection()
            .AddSingleton(_store)
            .AddSingleton<IGroupStore>(_groups)
            .AddSingleton<IUserStore>(users)
            .AddSingleton(agents)
            .AddSingleton(kbs)
            .AddSingleton(artifacts)
            .AddAttachmentGovernance()
            .BuildServiceProvider();
        _lifecycle = provider.GetRequiredService<IAttachmentLifecycle>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响结论 */ }
    }

    private static AttachmentInfo Info(string id) => new()
    {
        AttachmentId = id, Name = id + ".txt", ContentType = "text/plain", Size = 3,
        Url = $"/ag-ui/files/{id}/{id}.txt", Kind = "text",
    };

    [Fact]
    public void Inspect_CountsEveryReferenceKind_AndOnlyUnreferencedAreReclaimable()
    {
        var stats = _lifecycle.Inspect(TimeSpan.Zero);

        Assert.Equal(5, stats.TotalFiles);
        // 4 类引用（消息 / 群头像 / 知识库文档 / 技能产物）都必须被算进去
        Assert.Equal(4, stats.ReferencedFiles);
        Assert.Equal(1, stats.UnreferencedFiles);
        Assert.Equal(1, stats.OrphanFiles);
    }

    [Fact]
    public void GracePeriod_HoldsBackFreshFiles()
    {
        // 宽限期远大于文件年龄 → 仍算“无引用”，但不算“可回收”（上传后还没发送、正在生成的产物）
        var stats = _lifecycle.Inspect(TimeSpan.FromHours(168));

        Assert.Equal(1, stats.UnreferencedFiles);
        Assert.Equal(0, stats.OrphanFiles);
        Assert.Equal(4, stats.ReferencedFiles);
    }

    [Fact]
    public void DeleteIfUnreferenced_DeletesOnlyTheOrphan()
    {
        var result = _lifecycle.DeleteIfUnreferenced([ByMessage, ByKb, ByAvatar, BySkillRun, Orphan]);

        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(4, result.SkippedReferenced);
        Assert.Null(_store.ResolvePath(Orphan));           // 孤儿已删
        Assert.NotNull(_store.ResolvePath(ByMessage));     // 被消息引用的还在
        Assert.NotNull(_store.ResolvePath(ByKb));          // 被知识库引用的还在（最容易漏的一类）
        Assert.NotNull(_store.ResolvePath(ByAvatar));      // 被群头像引用的还在
        Assert.NotNull(_store.ResolvePath(BySkillRun));    // 被技能产物引用的还在
    }

    [Fact]
    public void ReclaimOrphans_DryRun_ReportsWithoutDeleting()
    {
        var result = _lifecycle.ReclaimOrphans(TimeSpan.Zero, dryRun: true);

        Assert.Equal(1, result.DeletedFiles);
        Assert.NotNull(_store.ResolvePath(Orphan)); // 只看不删
    }

    [Fact]
    public void ReclaimOrphans_Delete_RemovesOnlyOrphans()
    {
        var result = _lifecycle.ReclaimOrphans(TimeSpan.Zero, dryRun: false);

        Assert.Equal(1, result.DeletedFiles);
        Assert.Null(_store.ResolvePath(Orphan));
        Assert.NotNull(_store.ResolvePath(ByMessage));
    }

    [Fact]
    public void Inspect_IgnoresNonAttachmentDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "not-an-attachment"));
        Directory.CreateDirectory(Path.Combine(_dir, "att_")); // 前缀但无后缀：不合法

        var stats = _lifecycle.Inspect(TimeSpan.Zero);

        Assert.Equal(5, stats.TotalFiles); // 非法目录名一律不计入（防目录遍历）
    }
}
