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
}
