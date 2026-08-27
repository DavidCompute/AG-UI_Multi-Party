using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>会话安全增强（4.4）测试：多设备会话列举 / 单独吊销 / 吊销其他全部。</summary>
public sealed class SessionManagementTests
{
    private static AuthService CreateAuth()
        => new(new InMemoryUserStore(), new AuthOptions { SessionTtlHours = 24 }, TimeProvider.System, NullLogger<AuthService>.Instance);

    [Fact]
    public void GetUserSessions_ListsMultipleSessions_WithoutTokens()
    {
        var auth = CreateAuth();
        auth.Register("alice", "secret1", null, null);

        var t1 = auth.Login("alice", "secret1").Token;
        var t2 = auth.Login("alice", "secret1").Token;

        var sessions = auth.GetUserSessions(auth.ListUsers()[0].UserId);
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.StartsWith("ses_", s.SessionId));
        // 会话元信息不含令牌明文
        Assert.All(sessions, s => Assert.DoesNotContain(t1.Substring(0, 8), s.SessionId));
        Assert.DoesNotContain(t2, sessions[0].SessionId);
    }

    [Fact]
    public void RevokeSession_RemovesExactlyThatOne()
    {
        var auth = CreateAuth();
        var user = auth.Register("alice", "secret1", null, null);
        var t1 = auth.Login("alice", "secret1").Token;
        var t2 = auth.Login("alice", "secret1").Token;

        var id1 = auth.GetSessionIdOfToken(t1)!;
        Assert.NotNull(auth.GetSessionIdOfToken(t2));

        Assert.True(auth.RevokeSession(user.UserId, id1));
        Assert.Null(auth.ValidateToken(t1));   // t1 已吊销
        Assert.NotNull(auth.ValidateToken(t2)); // t2 仍有效
    }

    [Fact]
    public void RevokeOtherSessions_KeepsCurrentOnly()
    {
        var auth = CreateAuth();
        var user = auth.Register("alice", "secret1", null, null);
        var t1 = auth.Login("alice", "secret1").Token;
        var t2 = auth.Login("alice", "secret1").Token;
        var t3 = auth.Login("alice", "secret1").Token;

        var current = auth.GetSessionIdOfToken(t3)!;
        var revoked = auth.RevokeOtherSessions(user.UserId, current);

        Assert.Equal(2, revoked);
        Assert.Null(auth.ValidateToken(t1));
        Assert.Null(auth.ValidateToken(t2));
        Assert.NotNull(auth.ValidateToken(t3));
    }

    [Fact]
    public void Sessions_SurviveSnapshotRoundTrip_WithIds()
    {
        // 同一 user store 上两个 AuthService：模拟启动恢复，会话（含 SessionId）应完整还原、令牌继续有效
        var store = new InMemoryUserStore();
        var auth1 = new AuthService(store, new AuthOptions { SessionTtlHours = 24 }, TimeProvider.System, NullLogger<AuthService>.Instance);
        auth1.Register("alice", "secret1", null, null);
        var t = auth1.Login("alice", "secret1").Token;
        var before = auth1.SnapshotSessions();
        Assert.All(before, s => Assert.NotNull(s.SessionId));

        var auth2 = new AuthService(store, new AuthOptions { SessionTtlHours = 24 }, TimeProvider.System, NullLogger<AuthService>.Instance);
        auth2.RestoreSessions(before);

        Assert.NotNull(auth2.ValidateToken(t));
        Assert.StartsWith("ses_", auth2.GetSessionIdOfToken(t)!);
        Assert.Single(auth2.GetUserSessions(auth1.ListUsers()[0].UserId));
    }
}
