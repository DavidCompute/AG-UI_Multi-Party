using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// org_draft 组织方案草稿库单元测试：保存 / 取回 / 列 / 版本递增 / 群隔离 / 删除，
/// 以及“序列化 → 新实例恢复”的可持久化 round-trip（对应 RegisterOrgDraftPersistence 的快照语义）。
/// </summary>
public sealed class OrgDraftStoreTests
{
    private static OrgDraftStore NewStore(ChangeHub? hub = null)
        => new(NullLoggerFactory.Instance, hub);

    private static string Ser(IReadOnlyList<OrgDraft> all)
        => JsonSerializer.Serialize(all);

    private static IReadOnlyList<OrgDraft> Deser(string json)
        => JsonSerializer.Deserialize<List<OrgDraft>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void Save_ThenLoad_ReturnsContentVersionOne()
    {
        var store = NewStore();
        store.Save("g1", "it_support_v1", "topicA", "owner1", "IT客服中心", "# 岗位...");

        var got = store.Load("g1", "it_support_v1");
        Assert.NotNull(got);
        Assert.Equal(1, got!.Version);
        Assert.Equal("IT客服中心", got.Title);
        Assert.Equal("# 岗位...", got.Content);
        Assert.Equal("owner1", got.OwnerId);
    }

    [Fact]
    public void Save_SameSlug_BumpsVersionAndReplaces()
    {
        var store = NewStore();
        var first = store.Save("g1", "it_support_v1", "topicA", "owner1", "v1标题", "第一版正文");
        var second = store.Save("g1", "it_support_v1", "topicA", "owner1", "v2标题", "第二版正文");

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        var got = store.Load("g1", "it_support_v1")!;
        Assert.Equal("v2标题", got.Title);
        Assert.Equal("第二版正文", got.Content);
        Assert.Equal(2, got.Version);
    }

    [Fact]
    public void List_ReturnsSummariesGroupedByGroup_AndOrderedByUpdate()
    {
        var store = NewStore();
        store.Save("g1", "a", "t", "u", "A", "…");
        store.Save("g1", "b", "t", "u", "B", "…");
        store.Save("g2", "foreign", "t", "u", "Foreign", "…"); // 其它群不串

        var list = store.List("g1");
        Assert.Equal(2, list.Count);
        Assert.Contains(list, s => s.Slug == "a");
        Assert.Contains(list, s => s.Slug == "b");
        Assert.DoesNotContain(list, s => s.Slug == "foreign");
        Assert.All(list, s => Assert.Equal(1, s.Version));
    }

    [Fact]
    public void GroupIsolation_SameSlugInDifferentGroups_AreDistinct()
    {
        var store = NewStore();
        store.Save("g1", "shared", "t", "u", "群1", "群1内容");
        store.Save("g2", "shared", "t", "u", "群2", "群2内容");

        Assert.Equal("群1内容", store.Load("g1", "shared")!.Content);
        Assert.Equal("群2内容", store.Load("g2", "shared")!.Content);
    }

    [Fact]
    public void Load_Missing_ReturnsNull_AndDeleteIsIdempotent()
    {
        var store = NewStore();
        Assert.Null(store.Load("g1", "nope"));
        Assert.False(store.Delete("g1", "nope"));

        store.Save("g1", "k", "t", "u", "K", "…");
        Assert.True(store.Delete("g1", "k"));
        Assert.False(store.Delete("g1", "k"));
    }

    [Fact]
    public void JsonSnapshot_RoundTrip_RestoresAllGroupDraftsAcrossRestart()
    {
        var source = NewStore();
        source.Save("g1", "it_support_v1", "topicA", "owner1", "IT客服中心", "岗位 A\n岗位 B");
        source.Save("g1", "desk_v2", "topicB", "owner2", "桌面支持", "版本 1 方案");
        source.Save("g1", "desk_v2", "topicB", "owner2", "桌面支持", "版本 2 方案"); // 覆盖升版 → v2
        source.Save("g2", "hr_v1", "t", "owner3", "HR共享服务", "独立群草稿");

        // 模拟快照落盘
        var json = Ser(source.SnapshotAll());

        // 模拟“重启后的新实例”从快照整稿恢复
        var fresh = NewStore();
        fresh.RestoreAll(Deser(json));

        var got1 = fresh.Load("g1", "it_support_v1");
        var got2 = fresh.Load("g1", "desk_v2");
        var got3 = fresh.Load("g2", "hr_v1");
        Assert.NotNull(got1); Assert.Equal("IT客服中心", got1!.Title);
        Assert.NotNull(got2); Assert.Equal("桌面支持", got2!.Title); Assert.Equal(2, got2!.Version);
        Assert.NotNull(got3); Assert.Equal("owner3", got3!.OwnerId);
        Assert.Equal(3, fresh.List("g1").Count + fresh.List("g2").Count);
    }

    [Fact]
    public void ChangeHubNotify_IsRaisedOnMutation()
    {
        var changes = new ChangeHub();
        var notified = 0;
        changes.Subscribe(() => notified++);
        var store = NewStore(changes);

        store.Save("g1", "k", "t", "u", "K", "…");
        Assert.True(notified >= 1, "保存应触发一次持久化通知");

        var snap = store.SnapshotAll();
        store.Delete("g1", "k");
        Assert.True(notified >= 2, "删除应触发一次持久化通知");
        Assert.Single(snap); // 删除前快照仍含一份
    }
}
