using AguiGroupChat.Hub.Infra;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>操作审计日志（4.3）测试：记录 / 查询 / 容量上限。</summary>
public sealed class AuditLogTests
{
    [Fact]
    public void Record_And_Query_NewestFirst()
    {
        var log = new AuditLogService();
        log.Record("data.export", "user_1", "admin1", detail: "导出全部数据");
        log.Record("interaction.resolve", "user_2", "zhangsan", groupId: "g1", targetId: "i1", result: "ok", detail: "批准");
        log.Record("interaction.resolve", "user_2", "zhangsan", groupId: "g1", targetId: "i2", result: "denied", detail: "拒绝");

        var entries = log.Query(10);
        Assert.Equal(3, entries.Count);
        // 倒序：最新在前
        Assert.Equal("i2", entries[0].TargetId);
        Assert.Equal("denied", entries[0].Result);
        Assert.Equal("data.export", entries[2].Action);
        Assert.Equal("admin1", entries[2].ActorUsername);
        Assert.Equal("zhangsan", entries[1].ActorUsername);
    }

    [Fact]
    public void Query_LimitsToRequestedCount()
    {
        var log = new AuditLogService();
        for (var i = 0; i < 5; i++) log.Record("settings.model", "user_1", "admin", detail: "修改配置");
        Assert.Equal(2, log.Query(2).Count);
        Assert.Equal(5, log.Count);
    }

    [Fact]
    public void RingBuffer_DropsOldestBeyondCapacity()
    {
        var log = new AuditLogService();
        for (var i = 0; i < 6000; i++) log.Record("data.reset", "user_1", "admin", detail: $"i={i}");
        // 容量 5000：最旧的 1000 条被丢弃
        Assert.Equal(5000, log.Count);
        var latest = log.Query(1)[0];
        Assert.Contains("i=5999", latest.Detail!);
    }

    [Fact]
    public void Snapshot_Restore_RoundTripsEntries_AndContinuesSequence()
    {
        var log = new AuditLogService();
        log.Record("data.export", "user_1", "admin1", detail: "导出 1");
        log.Record("admin.user.disable", "user_2", "admin2", targetType: "user", targetId: "user_9");
        log.Record("user.account.delete", "user_1", "admin1", targetType: "user", targetId: "user_9", detail: "删除账号");

        // 快照 → 新实例恢复（模拟服务重启后从持久化 section 恢复）
        var fresh = new AuditLogService();
        fresh.Restore(log.Snapshot());

        Assert.Equal(3, fresh.Count);
        var restored = fresh.Query(10);
        Assert.Equal(3, restored.Count);
        Assert.Equal("user.account.delete", restored[0].Action); // 最新在前
        Assert.Equal("data.export", restored[2].Action);
        Assert.Equal("删除账号", restored[0].Detail);

        // 恢复后继续记录：序号推进，不产生重复 ID
        fresh.Record("settings.model", "user_1", "admin1", detail: "改配置");
        var ids = fresh.Query(10).Select(e => e.Id).ToHashSet();
        Assert.Equal(4, ids.Count); // 4 条各不相同
    }
}
