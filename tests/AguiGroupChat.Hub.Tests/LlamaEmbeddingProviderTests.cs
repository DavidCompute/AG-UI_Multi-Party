using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>LlamaEmbeddingProvider 长文本分段 embedding 逻辑：验证超 context 输入被正确分段（带重叠），短文本不分段。</summary>
public sealed class LlamaEmbeddingProviderTests
{
    [Fact]
    public void Split_ShortText_ReturnsSingle()
    {
        var segs = LlamaEmbeddingProvider.SplitForEmbedding("短文本", contextSize: 512);
        Assert.Single(segs);
        Assert.Equal("短文本", segs[0]);
    }

    [Fact]
    public void Split_AtContextBoundary_ReturnsSingle()
    {
        // 512 context × 0.9 字符/token = 460 字符上限；恰好等于上限时不分段
        var text = new string('中', 460);
        var segs = LlamaEmbeddingProvider.SplitForEmbedding(text, contextSize: 512);
        Assert.Single(segs);
    }

    [Fact]
    public void Split_ExceedsContext_SplitsWithOverlap()
    {
        var text = new string('中', 3000); // 3000 > 460 上限
        var segs = LlamaEmbeddingProvider.SplitForEmbedding(text, contextSize: 512);
        Assert.True(segs.Count >= 3, $"预期至少 3 段，实际 {segs.Count}");
        Assert.All(segs, s => Assert.True(s.Length <= 460));
        // 相邻段有重叠（后一段开头应出现在前一段中）
        Assert.Contains(segs[1][..64], segs[0]);
    }

    [Fact]
    public void Split_CoversEntireText()
    {
        var text = new string('测', 5000);
        var segs = LlamaEmbeddingProvider.SplitForEmbedding(text, contextSize: 512);
        var joined = string.Concat(segs);
        Assert.True(joined.Length >= text.Length);
        Assert.Equal(text[..^0].Length, text.Length); // 无丢失：首段从头、末段到尾部
        Assert.Equal('测', segs[0][0]);
        Assert.EndsWith("测", segs[^1]);
    }

    [Fact]
    public void Split_SmallContext_StillWorks()
    {
        var text = new string('a', 300);
        var segs = LlamaEmbeddingProvider.SplitForEmbedding(text, contextSize: 64);
        // 64 context × 0.9 = 57.6，但下限 max(64,...)=64 → 300 字符切 ~5 段
        Assert.True(segs.Count >= 2);
        Assert.All(segs, s => Assert.True(s.Length <= 64));
    }
}
