using AguiGroupChat.Hub.Users;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 登录 TOTP 校验失败限速测试：验证 <see cref="TotpService.Verify"/>（登录路径）在窗口内
/// 连续错码超限后进入 <see cref="TotpService.IsLockedOut"/> 锁定，防止对 6 位动态码暴力枚举。
/// Confirm / Disable 属已登录操作，不受此限速；限速按用户隔离。
/// </summary>
public sealed class TotpRateLimitTests
{
    private const int MaxAttempts = 5;

    private static long Step() => System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

    /// <summary>在给定服务实例上 Enroll + Confirm 把一个账号启用，返回密钥（测试内可直接算出有效码）。</summary>
    private static (TotpService Svc, string UserId, string Secret) Enable(TotpService svc, string userId)
    {
        var secret = svc.Enroll(userId);
        Assert.True(svc.Confirm(userId, TotpService.TOTP(secret, Step())));
        Assert.True(svc.IsEnabled(userId));
        return (svc, userId, secret);
    }

    [Fact]
    public void ContinuousWrongCodes_EventuallyLockOut()
    {
        var (svc, uid, secret) = Enable(new TotpService(), "user_lock");
        // 前几次乱码只报错、未锁定
        for (var i = 0; i < MaxAttempts - 1; i++)
        {
            Assert.False(svc.Verify(uid, "000000"));
            Assert.False(svc.IsLockedOut(uid));
        }
        // 到达阈值后锁定
        Assert.False(svc.Verify(uid, "000000"));
        Assert.True(svc.IsLockedOut(uid));
        // 锁定期间即使给对码也拒绝（不会因命中而解除）
        Assert.False(svc.Verify(uid, TotpService.TOTP(secret, Step() + 1)));
        Assert.True(svc.IsLockedOut(uid));
    }

    [Fact]
    public void Lockout_IsIsolatedPerUser()
    {
        var svc = new TotpService();
        var (_, a, _) = Enable(svc, "user_a");
        var (_, b, bSecret) = Enable(svc, "user_b");

        for (var i = 0; i < MaxAttempts; i++) Assert.False(svc.Verify(a, "111111"));
        Assert.True(svc.IsLockedOut(a));
        // 另一用户不受影响：未被锁定，且对码仍可校验通过（用 +1 窗口避开 Confirm 已用的重放窗口）
        Assert.False(svc.IsLockedOut(b));
        Assert.True(svc.Verify(b, TotpService.TOTP(bSecret, Step() + 1)));
    }

    [Fact]
    public void ConfirmAndDisable_NotRateLimited_EvenDuringLockout()
    {
        var (svc, uid, secret) = Enable(new TotpService(), "user_c");

        // 触发登录锁定（Verify 连续错码）
        for (var i = 0; i < MaxAttempts; i++) Assert.False(svc.Verify(uid, "222222"));
        Assert.True(svc.IsLockedOut(uid));

        // 已登录操作（停用）不受登录锁影响——对码仍可停用（用 +1 窗口避开 Confirm 已用的重放窗口）
        Assert.True(svc.Disable(uid, TotpService.TOTP(secret, Step() + 1)));
        Assert.False(svc.IsEnabled(uid));
    }
}
