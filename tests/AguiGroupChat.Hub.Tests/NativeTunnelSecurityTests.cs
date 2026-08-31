using AguiGroupChat.Web;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>反向隧道安全加固的单元测试：逐 agent 令牌解析 / 校验、限流器行为。</summary>
public sealed class NativeTunnelSecurityTests
{
    // —— NativeTunnelOptions 令牌解析 ——

    [Fact]
    public void IsTokenValid_UsesPerAgentToken_WhenConfigured()
    {
        var opts = new NativeTunnelOptions
        {
            Token = "global",
            AgentTokens = new Dictionary<string, string> { ["agent_a"] = "a-token" },
        };
        Assert.True(opts.HasTokenFor("agent_a"));
        Assert.True(opts.IsTokenValid("agent_a", "a-token"));
        Assert.False(opts.IsTokenValid("agent_a", "global")); // 逐 agent 配置后，全局令牌对它失效
        Assert.False(opts.IsTokenValid("agent_a", "wrong"));
    }

    [Fact]
    public void IsTokenValid_FallsBackToGlobal_WhenNoPerAgentToken()
    {
        var opts = new NativeTunnelOptions { Token = "global" };
        Assert.True(opts.HasTokenFor("agent_b")); // 未配置逐 agent → 用全局
        Assert.True(opts.IsTokenValid("agent_b", "global"));
        Assert.False(opts.IsTokenValid("agent_b", "other"));
        // 完全未配置任何令牌 → HasTokenFor 为 false（拒绝注册，不默认放行）
        Assert.False(new NativeTunnelOptions().HasTokenFor("agent_c"));
        Assert.False(new NativeTunnelOptions().IsTokenValid("agent_c", "anything"));
    }

    [Fact]
    public void HasTokenFor_GlobalOnly_True()
    {
        Assert.True(new NativeTunnelOptions { Token = "g" }.HasTokenFor("x"));
    }

    // —— SlidingRateLimiter ——

    [Fact]
    public void RateLimiter_AllowsWithinBudget_ThenRejects()
    {
        var rl = new SlidingRateLimiter(3);
        var now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.True(rl.Allow("ip-1", now));
        Assert.True(rl.Allow("ip-1", now));
        Assert.True(rl.Allow("ip-1", now));
        Assert.False(rl.Allow("ip-1", now)); // 超限
        Assert.True(rl.Allow("ip-2", now));  // 其它键独立计数
    }

    [Fact]
    public void RateLimiter_ResetsAfterWindowExpires()
    {
        var rl = new SlidingRateLimiter(1);
        var now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.True(rl.Allow("ip-1", now));
        Assert.False(rl.Allow("ip-1", now));
        Assert.True(rl.Allow("ip-1", now + 60_001)); // 窗口过后恢复
    }
}
