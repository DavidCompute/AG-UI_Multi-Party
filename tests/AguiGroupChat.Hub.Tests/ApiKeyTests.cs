using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>对外 API 密钥（6.4）测试：命中返回绑定用户 / 未命中返回空 / 用户名不存在返回空。</summary>
public sealed class ApiKeyTests
{
    private static AuthService CreateAuth(AuthOptions options)
    {
        var users = new InMemoryUserStore();
        var auth = new AuthService(users, options, TimeProvider.System, NullLogger<AuthService>.Instance);
        // 预置用户名（API Key 需绑定到已注册用户）
        users.AddUser(new UserAccount { UserId = "user_1", Username = "alice", Nickname = "小爱", PasswordHash = "h", PasswordSalt = "s", CreatedAt = 1 });
        return auth;
    }

    [Fact]
    public void ResolveApiKey_ReturnsBoundUser_WhenHit()
    {
        var auth = CreateAuth(new AuthOptions { ApiKeys = [new ApiKeyEntry { ApiKey = "ak_secret_123", Username = "alice" }] });
        var user = auth.ResolveApiKey("ak_secret_123");
        Assert.NotNull(user);
        Assert.Equal("user_1", user!.UserId);
        Assert.Equal("alice", user.Username);
    }

    [Fact]
    public void ResolveApiKey_ReturnsNull_WhenNoMatchOrNoKeys()
    {
        var auth = CreateAuth(new AuthOptions { ApiKeys = [new ApiKeyEntry { ApiKey = "ak_secret_123", Username = "alice" }] });
        Assert.Null(auth.ResolveApiKey("wrong-key"));
        Assert.Null(auth.ResolveApiKey(null));
        var authNoKeys = CreateAuth(new AuthOptions());
        Assert.Null(authNoKeys.ResolveApiKey("anything"));
    }

    [Fact]
    public void ResolveApiKey_ReturnsNull_WhenBoundUsernameMissing()
    {
        var auth = CreateAuth(new AuthOptions { ApiKeys = [new ApiKeyEntry { ApiKey = "ak_ghost", Username = "nobody" }] });
        Assert.Null(auth.ResolveApiKey("ak_ghost"));
    }
}
