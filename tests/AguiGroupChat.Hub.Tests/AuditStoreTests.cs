using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence.Relational;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>审计日志独立表（agui_audit）测试：SQLite 共享内存库验证写入 / 过滤查询 / 导出 / 保留裁剪。</summary>
public sealed class AuditStoreTests
{
    [Fact]
    public void AuditStore_Sqlite_RoundTrip_Query_Prune()
    {
        // cache=shared 内存库：保证建表 / 写入 / 查询同库；库名唯一防并行污染
        var db = new SqliteStore($"Data Source=file:audit-{Guid.NewGuid():N}?mode=memory&cache=shared");
        db.EnsureSchema();
        var log = new AuditLogService(new RelationalAuditStore(db));

        for (var i = 0; i < 3; i++)
        {
            log.Record("data.export", "user_1", "admin1", detail: $"导出 {i}");
            Thread.Sleep(2); // 保证 ts 递增便于顺序断言
        }
        log.Record("admin.user.disable", "user_2", "admin2", targetType: "user", targetId: "user_9");

        Assert.Equal(4, log.Count);
        // 最新在前
        Assert.Equal("admin.user.disable", log.Query(10)[0].Action);
        Assert.Equal("data.export", log.Query(10)[3].Action);
        // 过滤：操作者（userId 或用户名命中其一）、操作名、目标 ID
        Assert.Equal(3, log.Query(10, actor: "admin1").Count);
        Assert.Single(log.Query(10, action: "admin.user.disable"));
        Assert.Single(log.Query(10, targetId: "user_9"));
        Assert.Empty(log.Query(10, targetId: "no-such-target"));
        // 导出正序（CSV 用）
        Assert.Equal(4, log.QueryAll().Count);
        Assert.Equal("data.export", log.QueryAll()[0].Action);

        // 保留策略：裁剪到最新 2 条
        Assert.Equal(2, log.Prune(2));
        Assert.Equal(2, log.Count);
        Assert.Equal("admin.user.disable", log.Query(10)[0].Action);
    }

    [Fact]
    public void AuditStore_Sqlite_FiltersByDetail()
    {
        var db = new SqliteStore($"Data Source=file:audit-detail-{Guid.NewGuid():N}?mode=memory&cache=shared");
        db.EnsureSchema();
        var log = new AuditLogService(new RelationalAuditStore(db));

        log.Record("group.quotaOverride", "user_1", "admin1", targetType: "group", targetId: "g1",
            detail: "每日 Token 配额覆写 → 50（0 = 本知聚不限）；有效期至 2026-12-31 00:00Z");
        log.Record("group.quotaOverride", "user_1", "admin1", targetType: "group", targetId: "g2",
            detail: "每日 Token 配额覆写 → 100（0 = 本知聚不限）");
        log.Record("model.override", "user_1", "admin1", targetType: "group", targetId: "g3", detail: "模型 → x");

        // detail 子串过滤（与 actor / action / targetId 组合）
        var hit = log.Query(10, detail: "有效期至");
        Assert.Single(hit);
        Assert.Equal("g1", hit[0].TargetId);
        Assert.Equal(2, log.Query(10, action: "group.quotaOverride").Count);
        Assert.Single(log.Query(10, action: "group.quotaOverride", detail: "有效期至"));
        Assert.Empty(log.Query(10, detail: "不存在的子串"));
        Assert.Single(log.QueryAll(detail: "有效期至"));
    }

    [Fact]
    public void AuditLog_InMemory_FiltersByDetail()
    {
        var log = new AuditLogService();
        log.Record("group.quotaOverride", "user_1", "admin1", targetType: "group", targetId: "g1",
            detail: "每日 Token 配额覆写 → 50；有效期至 2026-12-31 00:00Z");
        log.Record("group.quotaOverride", "user_1", "admin1", targetType: "group", targetId: "g2",
            detail: "每日 Token 配额覆写 → （取消，回退全局）");

        Assert.Single(log.Query(10, detail: "有效期至"));
        Assert.Equal("g1", log.Query(10, detail: "有效期至")[0].TargetId);
        Assert.Single(log.QueryAll(detail: "有效期至"));
        Assert.Equal(2, log.Query(10, action: "group.quotaOverride").Count);
    }
}
