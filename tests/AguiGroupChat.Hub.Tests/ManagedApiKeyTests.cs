using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>托管 API 密钥存储与 AuthService 解析测试。</summary>
public sealed class ManagedApiKeyTests
{
    private static ManagedApiKey NewKey(string plaintext, string username = "bob") => new()
    {
        Id = "ak_1",
        Name = "n8n",
        Username = username,
        KeyPrefix = ManagedApiKeyStore.PrefixOf(plaintext),
        KeyHash = ManagedApiKeyStore.Hash(plaintext),
        CreatedAt = 1,
    };

    [Fact]
    public void Generate_HasAkPrefix()
    {
        var s = ManagedApiKeyStore.Generate();
        Assert.StartsWith("ak_", s);
        Assert.True(s.Length > 20);
        Assert.NotEqual(ManagedApiKeyStore.Generate(), s); // 随机
    }

    [Fact]
    public void Hash_IsDeterministicHex()
    {
        var h1 = ManagedApiKeyStore.Hash("ak_secret");
        var h2 = ManagedApiKeyStore.Hash("ak_secret");
        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length); // SHA-256 十六进制
        Assert.DoesNotContain("ak_secret", h1);
    }

    [Fact]
    public void PrefixOf_TruncatesLongSecrets()
    {
        Assert.Equal("ak_abcdefgh…", ManagedApiKeyStore.PrefixOf("ak_abcdefghijklmnop"));
    }

    [Fact]
    public void Match_FindsKey_AndSetsLastUsed()
    {
        var store = new ManagedApiKeyStore();
        var key = NewKey("ak_live_1");
        store.Add(key);

        var hit = store.Match("ak_live_1", 5_000);
        Assert.NotNull(hit);
        Assert.Equal("bob", hit!.Username);
        Assert.Equal(5_000, hit.LastUsedAt);
        Assert.Null(store.Match("ak_wrong", 6_000));
        Assert.Null(store.Match(null, 6_000));
    }

    [Fact]
    public void Remove_DeletesKey()
    {
        var store = new ManagedApiKeyStore();
        store.Add(NewKey("ak_live_2"));
        Assert.True(store.Remove("ak_1"));
        Assert.Null(store.Get("ak_1"));
        Assert.False(store.Remove("ak_1"));
    }

    [Fact]
    public void AuthService_ResolvesManagedKey_ToBoundUser()
    {
        var users = new InMemoryUserStore();
        users.AddUser(new UserAccount { UserId = "user_9", Username = "bob", Nickname = "小博", PasswordHash = "h", PasswordSalt = "s", CreatedAt = 1 });
        var managed = new ManagedApiKeyStore();
        managed.Add(NewKey("ak_bound_ok", "bob"));

        var auth = new AuthService(users, new AuthOptions(), TimeProvider.System,
            NullLogger<AuthService>.Instance, managedKeys: managed);

        var user = auth.ResolveApiKey("ak_bound_ok");
        Assert.NotNull(user);
        Assert.Equal("user_9", user!.UserId);
        Assert.Null(auth.ResolveApiKey("ak_nope"));
    }

    [Fact]
    public void AuthService_ManagedKey_BoundToUnknownUser_ReturnsNull()
    {
        var users = new InMemoryUserStore();
        var managed = new ManagedApiKeyStore();
        managed.Add(NewKey("ak_ghost", "nobody"));
        var auth = new AuthService(users, new AuthOptions(), TimeProvider.System,
            NullLogger<AuthService>.Instance, managedKeys: managed);
        Assert.Null(auth.ResolveApiKey("ak_ghost"));
    }
}
