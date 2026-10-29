using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 计时类断言专用集合（1.0.369）：xUnit 默认让不同测试类并行跑，而本机 4 核在全量套件里
/// 会被其它类吃满 CPU —— 同一段扫描隔离跑 ~180ms、并发跑能到 3.5s，绝对阈值就会随机红灯。
/// 把「有耗时断言」的类都放进这个禁并行集合：它们彼此串行，也不与其它集合并发。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "performance-timing";
}
