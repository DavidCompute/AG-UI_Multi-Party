using AguiGroupChat.Hub.Users;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>登录二次验证（TOTP，RFC 6238，4.4）测试。</summary>
public sealed class TotpTests
{
    [Fact]
    public void Rfc6238_TestVectors()
    {
        // RFC 6238 附录 B：secret = "12345678901234567890"（base32 = GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ）
        const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.Equal("755224", TotpService.TOTP(secret, 0));
        Assert.Equal("287082", TotpService.TOTP(secret, 1));
        Assert.Equal("359152", TotpService.TOTP(secret, 2));
        Assert.Equal("399871", TotpService.TOTP(secret, 8));
    }

    [Fact]
    public void Enroll_Confirm_Verify_EnabledFlow()
    {
        var svc = new TotpService();
        var secret = svc.Enroll("user_1");
        Assert.False(svc.IsEnabled("user_1")); // 未 confirm 不生效

        // 计算当前动态码（用同一算法，30s 窗口）
        var counter = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var code = TotpService.TOTP(secret, counter);

        Assert.True(svc.Confirm("user_1", code));
        Assert.True(svc.IsEnabled("user_1"));
        Assert.True(svc.Verify("user_1", TotpService.TOTP(secret, counter + 1))); // 窗口容忍
    }

    [Fact]
    public void Disable_RequiresCurrentCode_ThenAllowsLogin()
    {
        var svc = new TotpService();
        var secret = svc.Enroll("user_z");
        var counter = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        Assert.True(svc.Confirm("user_z", TotpService.TOTP(secret, counter)));
        Assert.True(svc.IsEnabled("user_z"));

        Assert.False(svc.Disable("user_z", "000000")); // 错码不能停用
        var nowStep = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        Assert.True(svc.Disable("user_z", TotpService.TOTP(secret, nowStep + 1)));
        Assert.False(svc.IsEnabled("user_z"));
    }

    [Fact]
    public void SnapshotRestore_RoundTrips()
    {
        var svc = new TotpService();
        svc.Enroll("user_a");
        var secret = svc.Enroll("user_b");
        var counter = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        svc.Confirm("user_b", TotpService.TOTP(secret, counter));

        var snap = svc.Snapshot();
        var svc2 = new TotpService();
        svc2.Restore(snap);
        Assert.True(svc2.IsEnabled("user_b"));
        Assert.False(svc2.IsEnabled("user_a"));
    }
}
