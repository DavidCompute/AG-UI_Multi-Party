using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// BM25 分数与余弦相似度的<b>量纲换算</b>。
///
/// <para>
/// 为何要单独立一组回归：向量分与 BM25 分最终会一起排序、一起过门槛，但两者不同尺 ——
/// BM25 是 sigmoid 归一化，零词面重叠就给 <b>0.5</b>。直接拿它当相似度，会让
/// “只共用一个常用词”看起来像 0.54 的相似度，比向量路给无关内容的分还高，
/// 于是它能盖过任何阈值（实测：知识库问“公司食堂今天中午吃什么”，文档里恰好有“公司”二字 →
/// 0.537，连“严格”档都拦不住）。
/// </para>
///
/// <para>
/// 换算后：零重叠 → 0；罕见词 / 专有号 → 0.4 上下 → 仍能过 <see cref="Bm25Ranker.KeywordSimilarityFloor"/>（兜底不丢）；
/// 只共用一个常用词 → 0.07 → 任何档位都不算命中。
/// </para>
/// </summary>
public sealed class Bm25SimilarityScaleTests
{
    [Fact]
    public void ToSimilarity_ZeroOverlap_IsZero_AndPerfectIsOne()
    {
        Assert.Equal(0, Bm25Ranker.ToSimilarity(Bm25Ranker.ZeroOverlapScore), 3);
        Assert.Equal(1, Bm25Ranker.ToSimilarity(1.0), 3);
        // 低于基准（理论上不该出现）也不返回负数
        Assert.Equal(0, Bm25Ranker.ToSimilarity(0.2), 3);
        Assert.Equal(0, Bm25Ranker.ToSimilarity(0), 3);
    }

    [Fact]
    public void SharedCommonWordOnly_FallsBelowTheKeywordFloor()
    {
        // 实测来源：文档里有“把公司组织架构搬进聊天”，提问是“公司食堂今天中午吃什么”，
        // 只共用“公司”这一个常用词二元组。换算前是 0.537（看着像“还挺像”），换算后应低于底线。
        var raw = Bm25Ranker.Score("公司食堂今天中午吃什么", "首创把公司组织架构搬进聊天：用户打造一个团队，平台自动生成对应的数字员工并建群。");
        var sim = Bm25Ranker.ToSimilarity(raw);
        Assert.True(raw > Bm25Ranker.ZeroOverlapScore, $"该查询确实有词面命中（raw={raw:0.###}）");
        Assert.True(sim < Bm25Ranker.KeywordSimilarityFloor,
            $"只共用一个常用词不该算命中：raw={raw:0.###} → sim={sim:0.###}");
    }

    [Fact]
    public void DistinctiveToken_StaysAboveTheKeywordFloor()
    {
        // 罕见词 / 专有号是“词面兜底”存在的意义：换算后仍要过底线（否则等于把功能修没了）
        var raw = Bm25Ranker.Score("ORION-7788 是什么", "本平台内部项目代号 ORION-7788，仅供内部文档引用。");
        var sim = Bm25Ranker.ToSimilarity(raw);
        Assert.True(sim >= Bm25Ranker.KeywordSimilarityFloor,
            $"罕见专有号应仍算命中：raw={raw:0.###} → sim={sim:0.###}");
    }

    [Fact]
    public void NoOverlap_IsZeroAfterConversion()
    {
        var raw = Bm25Ranker.Score("紫罗兰色潜水艇在珊瑚礁间穿行", "本平台内部项目代号 ORION-7788，仅供内部文档引用。");
        Assert.Equal(0, Bm25Ranker.ToSimilarity(raw), 3);
    }
}
