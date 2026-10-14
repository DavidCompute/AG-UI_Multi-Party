using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>副本身份与单副本注册表（跨副本诊断的基础）。</summary>
public sealed class ReplicaRegistryTests
{
    [Fact]
    public void Identity_DefaultsToHostAndPid()
    {
        var id = new ReplicaIdentity();
        Assert.False(string.IsNullOrWhiteSpace(id.Id));
        Assert.Equal(Environment.ProcessId, id.Pid);
        Assert.Contains(":", id.Id); // 主机:进程号
    }

    [Fact]
    public void Identity_HonorsConfiguredId()
    {
        var id = new ReplicaIdentity("pod-a-0");
        Assert.Equal("pod-a-0", id.Id);
    }

    [Fact]
    public void Local_EmptyBeforePublish()
    {
        var reg = new LocalReplicaRegistry(new ReplicaIdentity("r1"));
        Assert.Empty(reg.List());
        Assert.Equal("r1", reg.SelfId);
    }

    [Fact]
    public void Local_ReturnsLastPublishedSelf()
    {
        var reg = new LocalReplicaRegistry(new ReplicaIdentity("r1"));
        reg.Publish(new ReplicaSnapshot { ReplicaId = "r1", Invocations = 3 });
        reg.Publish(new ReplicaSnapshot { ReplicaId = "r1", Invocations = 5 });
        var list = reg.List();
        Assert.Single(list);
        Assert.Equal(5, list[0].Invocations);
    }
}

/// <summary>
/// Redis 副本注册表（跨副本诊断）：发布 / 跨读取者可见 / 清理过期索引。
/// 需要本地或容器内可用 Redis（默认 localhost:6390，<c>AGUI_REDIS_TEST_URL</c> 覆盖）；不可达时跳过。
/// </summary>
[Collection("Redis")]
[Trait("Category", "Redis")]
public sealed class RedisReplicaRegistryTests : IDisposable
{
    private static readonly bool RedisAvailable;
    private static readonly string RedisUrl;

    private readonly RedisContext? _ctx;

    static RedisReplicaRegistryTests()
    {
        RedisUrl = Environment.GetEnvironmentVariable("AGUI_REDIS_TEST_URL") ?? "localhost:6390";
        try
        {
            using var probe = ConnectionMultiplexer.Connect(RedisUrl, options => options.AbortOnConnectFail = false);
            RedisAvailable = probe.GetDatabase().Ping().TotalMilliseconds >= 0;
        }
        catch { RedisAvailable = false; }
    }

    public RedisReplicaRegistryTests()
    {
        if (!RedisAvailable) return;
        _ctx = new RedisContext(RedisUrl);
        _ctx.FlushAguiKeys();
    }

    public void Dispose() => _ctx?.Dispose();

    private RedisReplicaRegistry Registry(string selfId)
        => new(_ctx!, new ReplicaIdentity(selfId), NullLogger<RedisReplicaRegistry>.Instance);

    [Fact]
    public void Publish_IsVisibleToOtherReplicaReaders()
    {
        if (!RedisAvailable) return;
        Registry("r1").Publish(new ReplicaSnapshot { ReplicaId = "r1", Host = "h", Invocations = 7 });

        var list = Registry("r2").List(); // 另一个「副本」也能读到
        var snap = Assert.Single(list);
        Assert.Equal("r1", snap.ReplicaId);
        Assert.Equal(7, snap.Invocations);
    }

    [Fact]
    public void List_PrunesIndexEntriesWithNoSnapshot()
    {
        if (!RedisAvailable) return;
        _ctx!.Db.SetAdd(RedisContext.ReplicaIndexKey, "ghost"); // 索引里有、但快照已过期

        var list = Registry("r1").List();

        Assert.Empty(list);
        Assert.False(_ctx.Db.SetContains(RedisContext.ReplicaIndexKey, "ghost")); // 死项被清掉
    }

    [Fact]
    public void Ping_ReportsConnected()
    {
        if (!RedisAvailable) return;
        var (connected, latencyMs) = _ctx!.Ping();
        Assert.True(connected);
        Assert.True(latencyMs >= 0);
    }
}
