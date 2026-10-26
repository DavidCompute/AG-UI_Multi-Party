using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>编排步骤空产出重试参数：默认值、0 合法性与负数回退。</summary>
public sealed class ExecutionOptionsRetryTests
{
    [Fact]
    public void Defaults_EnableRetryWithOneAttempt()
    {
        var options = new ExecutionOptions();

        Assert.True(options.PlanStepRetryOnEmpty);
        Assert.Equal(1, options.PlanStepEmptyRetryMax);
    }

    [Fact]
    public void Normalize_KeepsZeroRetryMax()
    {
        var options = new ExecutionOptions { PlanStepEmptyRetryMax = 0 };

        options.Normalize();

        Assert.Equal(0, options.PlanStepEmptyRetryMax);
    }

    [Fact]
    public void Normalize_ClampsNegativeRetryMaxToDefault()
    {
        var options = new ExecutionOptions { PlanStepEmptyRetryMax = -3 };

        options.Normalize();

        Assert.Equal(1, options.PlanStepEmptyRetryMax);
    }

    [Fact]
    public void Normalize_DoesNotChangeRetrySwitch()
    {
        var options = new ExecutionOptions { PlanStepRetryOnEmpty = false };

        options.Normalize();

        Assert.False(options.PlanStepRetryOnEmpty);
    }
}
