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

    /// <summary>
    /// <b>长描述里蹭到一个常用词不算词面命中</b>：BM25 是分词位置的，描述越长、
    /// 普通词越多，累加越容易把分拉过底线。实测（图库）：查询“颁奖 团队 合影”会命中一张
    /// “AI 协作插画”（描述里恰好有“团队”二字），分数 0.38 —— 已经过了 0.35 的底线，
    /// 于是被当成命中嵌进幻灯片（用户报的“配图不正确”）。
    /// </summary>
    [Fact]
    public void ScatteredCommonWord_InLongDescription_IsNotPhraseEvidence()
    {
        const string illustration =
            "三个标有AI的屏幕设备与左侧三人团队图标围绕中心连接环，由虚线节点相连，构成人工智能协作主题的白色背景青绿扁平插画。"
            + "检索词：AI设备、显示器、三人团队、用户群、连接环、数据节点、虚线弧、电路线条；人工智能、人机协作、团队协作、"
            + "科技互联网；连接、同步、交互；青绿、蓝绿、白色；3个AI设备、3人；扁平矢量插画、图标、信息图。";
        var sim = Bm25Ranker.ToSimilarity(Bm25Ranker.Score("颁奖 团队 合影", illustration));
        Assert.True(sim >= Bm25Ranker.KeywordSimilarityFloor,
            $"前提：该查询确实越过了底线（sim={sim:0.###}）—— 所以光靠底线拦不住");
        Assert.False(Bm25Ranker.HasPhraseEvidence("颁奖 团队 合影", illustration),
            "只共享“团队”一个词、且它在查询里是孤立词项：不算词组命中");
    }

    /// <summary>连续词组命中算词面命中：人名、型号这些正是这条路要救的目标。</summary>
    [Fact]
    public void ContiguousPhrase_IsPhraseEvidence()
    {
        // 人名（图库描述就是人名）：整串连续命中
        Assert.True(Bm25Ranker.HasPhraseEvidence("刘佳俊", "刘佳俊"));
        // 型号：ASCII 词项连续命中
        Assert.True(Bm25Ranker.HasPhraseEvidence("SKU-2026", "产品包装盒正面照，蓝色，含 SKU-2026 标签"));
        // 词组（与库里的描述用词一致）
        Assert.True(Bm25Ranker.HasPhraseEvidence("颁奖典礼合影", "年度优秀员工颁奖典礼合影，舞台红毯"));
        // 同库里的无关描述不算
        Assert.False(Bm25Ranker.HasPhraseEvidence("颁奖典礼合影", "会场背景板"));
    }

    /// <summary>
    /// 多词项查询里，**只有孤立词命中**也不算词组证据（这是“挨个蹭词”与“命中一个词组”的区别）。
    /// 例：“MS 商务团队”与“MS技术支持团队”——共享 ms / 团队，但它们在查询里不连续。
    /// 这种弱命中原先就过不了底线（实测 sim≈0.32），这里只把契约写清楚。
    /// </summary>
    [Fact]
    public void IsolatedTokens_WithoutAContiguousRun_AreNotPhraseEvidence()
    {
        Assert.False(Bm25Ranker.HasPhraseEvidence("MS 商务团队", "MS技术支持团队"));
    }
}
