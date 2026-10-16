using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>本次回答用量累加器（<see cref="UsageAccumulator"/>）：累加与非正数忽略、AsyncLocal 作用域。</summary>
public sealed class UsageAccumulatorTests
{
    [Fact]
    public void Add_AccumulatesAndIgnoresNonPositive()
    {
        var acc = new UsageAccumulator();
        Assert.True(acc.IsEmpty);

        acc.Add(100, 50, 0);
        acc.Add(10, 5, 3);
        acc.Add(0, 0, 0);     // 全零：不计
        acc.Add(-5, -1, -1);  // 负数：不计

        Assert.Equal(110, acc.InputTokens);
        Assert.Equal(55, acc.OutputTokens);
        Assert.Equal(3, acc.ReasoningTokens);
        Assert.False(acc.IsEmpty);
    }

    [Fact]
    public void Ambient_IsNullByDefault_AndRestorable()
    {
        Assert.Null(UsageAccumulator.Ambient.Value);
        var outer = new UsageAccumulator();
        UsageAccumulator.Ambient.Value = outer;
        try
        {
            // 嵌套作用域沿用同一实例（网关「最外层创建、嵌套沿用」语义的基础）
            Assert.Same(outer, UsageAccumulator.Ambient.Value);
            UsageAccumulator.Ambient.Value!.Add(1, 2, 3);
            Assert.Equal(1, outer.InputTokens);
        }
        finally { UsageAccumulator.Ambient.Value = null; }
        Assert.Null(UsageAccumulator.Ambient.Value);
    }
}
