using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Persistence;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>内置组织角色覆盖语义所需的窄映射 OrgTeamStore 的单元测试。</summary>
public sealed class OrgTeamStoreTests
{
    [Fact]
    public void Upsert_ThenGet_AndSnapshot_Restore_RoundTrip()
    {
        var s = new OrgTeamStore();
        s.Upsert("it_support", "IT服务台", ["helpdesk", "sr"], ["triage"], null);
        var r = s.Get("it_support")!;
        Assert.Equal("IT服务台", r.Title);
        Assert.Equal(["helpdesk", "sr"], r.Agents);
        Assert.Equal(["triage"], r.Skills);

        // 序列化 → 新实例恢复（对应 RegisterOrgTeamPersistence 的 snapshot/restore）
        var json = JsonSerializer.Serialize(s.SnapshotAll());
        var fresh = new OrgTeamStore();
        fresh.RestoreAll(JsonSerializer.Deserialize<List<OrgTeamRecord>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        var rr = fresh.Get("it_support");
        Assert.NotNull(rr);
        Assert.Equal(["helpdesk", "sr"], rr!.Agents);
    }

    [Fact]
    public void Overwrite_SameKey_KeepsLatestOnly_AndRemoveClears()
    {
        var s = new OrgTeamStore();
        s.Upsert("it_support", "v1", ["helpdesk", "sr"], ["triage"], null);
        s.Upsert("it_support", "v2", ["helpdesk", "l3"], ["triage"], null); // 覆盖同一 key
        Assert.Equal(["helpdesk", "l3"], s.Get("it_support")!.Agents);      // 只剩最新批
        Assert.Single(s.All());

        Assert.True(s.Remove("it_support", out var gone));
        Assert.NotNull(gone);
        Assert.Null(s.Get("it_support"));
        Assert.Empty(s.All());
    }
}
