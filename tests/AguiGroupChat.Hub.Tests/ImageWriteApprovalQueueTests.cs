using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Web;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>图库写入审批队列（1.0.279）：入队 / 列表 / 取出，以及条数与每人上限。</summary>
public sealed class ImageWriteApprovalQueueTests
{
    private static PendingImageWrite? Add(ImageWriteApprovalQueue q, string owner, out string? error,
        string lib = "img_1", int bytes = 4)
        => q.Add(owner, "skill_x", "agent_x", lib, "a.png", "image/png", new byte[bytes], "cap", null, out error);

    [Fact]
    public void AddListTake_Works()
    {
        var q = new ImageWriteApprovalQueue();
        var a = Add(q, "user_1", out var e1);
        var b = Add(q, "user_2", out var e2);
        Assert.Null(e1);
        Assert.Null(e2);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(2, q.Count);
        Assert.Equal(2, q.List().Count);

        Assert.True(q.TryTake(a!.Id, out var taken));
        Assert.Equal(a.Id, taken!.Id);
        Assert.Equal(1, q.Count);
        Assert.False(q.TryTake("iwp_bogus", out _));
    }

    [Fact]
    public void ResolvedHistory_KeepsNewestAndCaps()
    {
        var q = new ImageWriteApprovalQueue();
        Assert.Empty(q.RecentResolved());

        for (var i = 0; i < ImageWriteApprovalQueue.MaxHistory + 5; i++)
            q.RecordResolved(new ResolvedImageWrite
            {
                PendingId = "iwp_" + i,
                Status = i % 2 == 0 ? "approved" : "rejected",
                LibId = "img_1",
                FileName = $"f{i}.png",
                AtMs = i,
            });

        var all = q.RecentResolved(ImageWriteApprovalQueue.MaxHistory + 5);
        Assert.Equal(ImageWriteApprovalQueue.MaxHistory, all.Count);
        Assert.Equal("iwp_54", all[0].PendingId); // 最新在前
        Assert.Equal("iwp_5", all[^1].PendingId); // 最旧的 5 条已淘汰

        var page = q.RecentResolved(3);
        Assert.Equal(3, page.Count);
        Assert.Equal(new[] { "iwp_54", "iwp_53", "iwp_52" }, page.Select(x => x.PendingId));
    }

    [Fact]
    public void PerOwnerCap_IsEnforced()
    {
        var q = new ImageWriteApprovalQueue();
        for (var i = 0; i < ImageWriteApprovalQueue.MaxPerOwner; i++)
            Assert.NotNull(Add(q, "user_spam", out _));

        var rejected = Add(q, "user_spam", out var error);
        Assert.Null(rejected);
        Assert.Contains("上限", error);

        // 其他用户不受影响
        Assert.NotNull(Add(q, "user_other", out _));
    }

    [Fact]
    public void GlobalCap_IsEnforced()
    {
        var q = new ImageWriteApprovalQueue();
        // 每个用户最多 20，故用 3 个用户填满 50
        var added = 0;
        for (var u = 0; u < 3 && added < ImageWriteApprovalQueue.MaxItems; u++)
            for (var i = 0; i < ImageWriteApprovalQueue.MaxPerOwner && added < ImageWriteApprovalQueue.MaxItems; i++)
            {
                Assert.NotNull(Add(q, "user_" + u, out _));
                added++;
            }
        Assert.Equal(ImageWriteApprovalQueue.MaxItems, q.Count);
        Assert.Null(Add(q, "user_new", out var error));
        Assert.Contains("上限", error);
    }

    [Fact]
    public void RestoreHistory_TrimsAndSkipsInvalid()
    {
        var q = new ImageWriteApprovalQueue();
        var rows = Enumerable.Range(0, ImageWriteApprovalQueue.MaxHistory + 3)
            .Select(i => new ResolvedImageWriteDto("iwp_" + i, "approved", null, "img_1", null, $"f{i}.png", null, null, null, false, null, i))
            .Append(new ResolvedImageWriteDto("", "approved", null, "img_1", null, "bad.png", null, null, null, false, null, 1))
            .ToList();

        q.RestoreHistory(rows);

        var all = q.RecentResolved(ImageWriteApprovalQueue.MaxHistory + 10);
        Assert.Equal(ImageWriteApprovalQueue.MaxHistory, all.Count);
        Assert.Equal("iwp_0", all[0].PendingId); // 保持传入顺序（新→旧）
        Assert.DoesNotContain(all, x => x.PendingId.Length == 0);
    }

    [Fact]
    public void ClearHistory_ReturnsCountAndEmpties()
    {
        var q = new ImageWriteApprovalQueue();
        q.RecordResolved(new ResolvedImageWrite { PendingId = "iwp_1", Status = "approved", LibId = "img_1", FileName = "a.png", AtMs = 1 });
        q.RecordResolved(new ResolvedImageWrite { PendingId = "iwp_2", Status = "rejected", LibId = "img_1", FileName = "b.png", AtMs = 2 });

        Assert.Equal(2, q.ClearHistory());
        Assert.Empty(q.RecentResolved());
        Assert.Equal(0, q.ClearHistory());
    }

    [Fact]
    public void History_RoundTripsThroughPersistenceSection()
    {
        var store1 = new CaptureSectionStore();
        var q1 = new ImageWriteApprovalQueue();
        var sp1 = new ServiceCollection()
            .AddSingleton(q1)
            .AddSingleton<ISectionStore>(store1)
            .BuildServiceProvider();
        sp1.RegisterImageWriteApprovalPersistence();

        q1.RecordResolved(new ResolvedImageWrite
        {
            PendingId = "iwp_1", Status = "approved", LibId = "img_1", LibName = "库",
            FileName = "a.png", SkillId = "skill_x", AgentId = "agent_x", AssetId = "asset_1",
            DecidedBy = "david", AtMs = 123,
        });
        q1.RecordResolved(new ResolvedImageWrite
        {
            PendingId = "iwp_2", Status = "rejected", LibId = "img_1", FileName = "b.png", AtMs = 124,
        });

        var snapshot = store1.Snapshots["imageWriteHistory"]();
        var json = JsonSerializer.SerializeToElement(snapshot, AguiJson.Options);

        // 模拟重启：新队列 + 新容器，调用同一扩展区的恢复回调
        var q2 = new ImageWriteApprovalQueue();
        var store2 = new CaptureSectionStore();
        new ServiceCollection()
            .AddSingleton(q2)
            .AddSingleton<ISectionStore>(store2)
            .BuildServiceProvider()
            .RegisterImageWriteApprovalPersistence();

        store2.Restores["imageWriteHistory"](json);

        var restored = q2.RecentResolved();
        Assert.Equal(2, restored.Count);
        Assert.Equal("iwp_2", restored[0].PendingId); // 新→旧
        Assert.Equal("rejected", restored[0].Status);
        var first = restored[1];
        Assert.Equal("approved", first.Status);
        Assert.Equal("库", first.LibName);
        Assert.Equal("asset_1", first.AssetId);
        Assert.Equal("david", first.DecidedBy);
    }

    [Fact]
    public void PeekAndRemove_KeepThenDropBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "agui-iwq-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var q = new ImageWriteApprovalQueue(root);
            var item = Add(q, "user_1", out var error);
            Assert.Null(error);
            var file = Path.Combine(root, item!.Id + ".bin");

            // 1.0.289：批准失败时先 Peek（不移除），字节仍可重试
            var peeked = q.Peek(item.Id);
            Assert.NotNull(peeked);
            Assert.Equal(item.Id, peeked!.Id);
            Assert.Equal(1, q.Count);
            Assert.True(File.Exists(file));
            Assert.Single(q.List());

            // 写成功才 Remove：项与字节都清除
            Assert.True(q.Remove(item.Id));
            Assert.Equal(0, q.Count);
            Assert.False(File.Exists(file));
            Assert.Null(q.Peek(item.Id));
            Assert.False(q.Remove(item.Id));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void PersistedQueue_RestoresAcrossRestart_AndDeletesBytesOnTake()
    {
        var root = Path.Combine(Path.GetTempPath(), "agui-iwq-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var q1 = new ImageWriteApprovalQueue(root);
            var item = Add(q1, "user_1", out var error);
            Assert.Null(error);
            Assert.NotNull(item);
            var file = Path.Combine(root, item!.Id + ".bin");
            Assert.True(File.Exists(file));
            var snapshot = q1.Snapshot();

            // 模拟重启：同一目录 + 恢复元数据
            var q2 = new ImageWriteApprovalQueue(root);
            q2.Restore(snapshot);
            Assert.Equal(1, q2.Count);
            Assert.Equal(item.Bytes, q2.List()[0].Bytes);

            Assert.True(q2.TryTake(item.Id, out _));
            Assert.False(File.Exists(file)); // 取出后字节文件清理
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void Restore_SkipsExpiredItems_AndDeletesTheirBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "agui-iwq-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            const string id = "iwp_expired";
            File.WriteAllBytes(Path.Combine(root, id + ".bin"), [1, 2, 3]);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var q = new ImageWriteApprovalQueue(root);
            q.Restore([new PendingImageWriteDto(id, "user_1", null, null, "img_1", "a.png", "image/png", null, null,
                now - 10_000, now - 1)]);

            Assert.Equal(0, q.Count);
            Assert.False(File.Exists(Path.Combine(root, id + ".bin")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 忽略 */ }
        }
    }

    /// <summary>捕获扩展区注册的回调（无数据库 / 无持久化文件时验证快照 + 恢复语义）。</summary>
    private sealed class CaptureSectionStore : ISectionStore
    {
        public Dictionary<string, Func<object?>> Snapshots { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Action<JsonElement>> Restores { get; } = new(StringComparer.Ordinal);

        public void AddSection(string name, Func<object?> snapshot, Action<JsonElement> restore)
        {
            Snapshots[name] = snapshot;
            Restores[name] = restore;
        }

        public void LoadSections() { }
        public void Flush() { }
    }
}
