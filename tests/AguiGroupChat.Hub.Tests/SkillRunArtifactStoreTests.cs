using AguiGroupChat.Web;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能试运行产物归属：产出者本人可读、他人不可读；过期与超量会被裁掉。
///
/// <para>
/// 为什么值得单测：这是**附件访问校验上新增的一路放行**。放行写宽了就是跨用户读取他人稿子的越权口子，
/// 写窄了（或忘了裁）就是自己刚生成的稿子打不开 / 登记表无限膨胀。两者都不该靠“上线看看”。
/// </para>
/// </summary>
public sealed class SkillRunArtifactStoreTests
{
    /// <summary>可控时钟（保留期裁剪要能确定性地跨过 7 天）。</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Fact]
    public void RegisteredArtifact_IsReadableByProducerOnly()
    {
        var store = new SkillRunArtifactStore();
        store.Register("att_run1", "user_a");

        Assert.True(store.IsOwnedBy("att_run1", "user_a"));
        Assert.False(store.IsOwnedBy("att_run1", "user_b"));
        Assert.False(store.IsOwnedBy("att_other", "user_a"));
        Assert.False(store.IsOwnedBy("", "user_a"));
        Assert.False(store.IsOwnedBy("att_run1", ""));
    }

    [Fact]
    public void EmptyInputs_AreIgnoredInsteadOfThrowing()
    {
        var store = new SkillRunArtifactStore();
        store.Register(null, "user_a");
        store.Register("att_x", null);
        store.Register("  ", "user_a");

        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void ExpiredEntries_AreDropped()
    {
        var clock = new FakeClock();
        var store = new SkillRunArtifactStore(clock);
        store.Register("att_old", "user_a");

        clock.Advance(SkillRunArtifactStore.Retention + TimeSpan.FromMinutes(1));
        // 裁剪发生在下一次登记时（无需后台定时器）
        store.Register("att_new", "user_a");

        Assert.False(store.IsOwnedBy("att_old", "user_a"));
        Assert.True(store.IsOwnedBy("att_new", "user_a"));
    }

    [Fact]
    public void JustWithinRetention_IsKept()
    {
        var clock = new FakeClock();
        var store = new SkillRunArtifactStore(clock);
        store.Register("att_keep", "user_a");

        clock.Advance(SkillRunArtifactStore.Retention - TimeSpan.FromMinutes(1));
        store.Register("att_new", "user_a");

        Assert.True(store.IsOwnedBy("att_keep", "user_a"));
    }

    [Fact]
    public void PerUserOverflow_KeepsTheMostRecent()
    {
        var clock = new FakeClock();
        var store = new SkillRunArtifactStore(clock);
        var total = SkillRunArtifactStore.MaxPerUser + 5;
        for (var i = 0; i < total; i++)
        {
            store.Register($"att_{i:D4}", "user_a");
            clock.Advance(TimeSpan.FromSeconds(1)); // 保证时间序确定（不依赖同毫秒的插入顺序）
        }

        var kept = store.Snapshot();
        Assert.Equal(SkillRunArtifactStore.MaxPerUser, kept.Count);
        // 最旧的被裁掉，最新的留下
        Assert.False(store.IsOwnedBy("att_0000", "user_a"));
        Assert.True(store.IsOwnedBy($"att_{total - 1:D4}", "user_a"));
    }

    [Fact]
    public void OtherUsersAreNotAffectedByAnotherUsersOverflow()
    {
        var clock = new FakeClock();
        var store = new SkillRunArtifactStore(clock);
        store.Register("att_b_keep", "user_b");
        for (var i = 0; i < SkillRunArtifactStore.MaxPerUser + 5; i++)
        {
            store.Register($"att_a_{i:D4}", "user_a");
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(store.IsOwnedBy("att_b_keep", "user_b"));
    }

    [Fact]
    public void SnapshotAndRestore_RoundTrip()
    {
        var store = new SkillRunArtifactStore();
        store.Register("att_1", "user_a");
        store.Register("att_2", "user_b");

        // 走一遍序列化（持久化实际路径），确认 record 能被 System.Text.Json 往返
        var json = System.Text.Json.JsonSerializer.Serialize(store.Snapshot());
        var records = System.Text.Json.JsonSerializer.Deserialize<List<SkillRunArtifactStore.Artifact>>(json)!;

        var restored = new SkillRunArtifactStore();
        restored.Restore(records);

        Assert.True(restored.IsOwnedBy("att_1", "user_a"));
        Assert.True(restored.IsOwnedBy("att_2", "user_b"));
        Assert.False(restored.IsOwnedBy("att_1", "user_b"));
    }

    [Fact]
    public void Restore_ReplacesPreviousState_AndDropsInvalidRows()
    {
        var store = new SkillRunArtifactStore();
        store.Register("att_stale", "user_a");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // 必须新到不过期（过期的行会被裁剪掉）

        store.Restore([
            new SkillRunArtifactStore.Artifact("att_ok", "user_a", now),
            new SkillRunArtifactStore.Artifact("", "user_a", now),          // 附件 ID 空：丢弃
            new SkillRunArtifactStore.Artifact("att_noowner", "  ", now),   // 归属空：丢弃
        ]);

        Assert.False(store.IsOwnedBy("att_stale", "user_a"));
        Assert.True(store.IsOwnedBy("att_ok", "user_a"));
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public void ClearAll_EmptiesEverything()
    {
        var store = new SkillRunArtifactStore();
        store.Register("att_1", "user_a");

        store.ClearAll();

        Assert.Empty(store.Snapshot());
        Assert.False(store.IsOwnedBy("att_1", "user_a"));
    }
}
