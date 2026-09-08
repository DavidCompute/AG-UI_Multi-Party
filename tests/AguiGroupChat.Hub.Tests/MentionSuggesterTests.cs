using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>输入时「建议 @ 谁」评分器（本地规则、无模型调用）单元测试。</summary>
public sealed class MentionSuggesterTests
{
    private static MentionCandidateProfile Agent(string id, string nick, string desc, params string[] keywords)
        => new(id, nick, desc, keywords);

    [Fact]
    public void Suggest_TooShortOrEmpty_ReturnsNone()
    {
        Assert.Empty(MentionSuggester.Suggest("", [Agent("a", "助手", "负责测试")]));
        Assert.Empty(MentionSuggester.Suggest("   ", [Agent("a", "助手", "负责测试")]));
        Assert.Empty(MentionSuggester.Suggest("短", [Agent("a", "助手", "负责测试")]));
        Assert.Empty(MentionSuggester.Suggest("帮我看看", []));
    }

    [Fact]
    public void Suggest_KeywordHit_RanksTop()
    {
        var profiles = new[]
        {
            Agent("agent_disk", "磁盘专员", "负责服务器存储", "磁盘"),
            Agent("agent_net", "网络专员", "负责交换机与路由", "网络"),
        };
        var sug = MentionSuggester.Suggest("帮我看看磁盘空间是不是满了", profiles);
        Assert.Equal("agent_disk", Assert.Single(sug).AgentId);
    }

    [Fact]
    public void Suggest_NicknameInText_Counts()
    {
        var profiles = new[]
        {
            Agent("agent_ops", "运维小助手", "负责机房日常巡检与告警处理"),
            Agent("agent_hr", "人事小助手", "负责入职办理与考勤管理"),
        };
        var sug = MentionSuggester.Suggest("运维小助手在吗？帮我查一下值班表", profiles);
        Assert.Equal("agent_ops", Assert.Single(sug).AgentId);
    }

    [Fact]
    public void Suggest_DescriptionBigramOverlap_MatchRelevantRole()
    {
        var profiles = new[]
        {
            Agent("agent_disk", "存储助手", "负责磁盘空间清理与性能优化"),
            Agent("agent_mkt", "营销助手", "负责广告投放与活动策划文案"),
        };
        var sug = MentionSuggester.Suggest("磁盘空间不足了，怎么清理一下？", profiles);
        Assert.Equal("agent_disk", Assert.Single(sug).AgentId);
    }

    [Fact]
    public void Suggest_IrrelevantText_ReturnsNone()
    {
        var profiles = new[]
        {
            Agent("agent_disk", "存储助手", "负责磁盘空间清理与性能优化"),
            Agent("agent_net", "网络助手", "负责网络故障排查与专线维护"),
        };
        Assert.Empty(MentionSuggester.Suggest("中午一起吃什么好呢", profiles));
    }

    [Fact]
    public void Suggest_CapsAtThree_AndSortsByScore()
    {
        var profiles = new[]
        {
            Agent("a1", "一号", "负责磁盘清理", "磁盘"),
            Agent("a2", "二号", "负责数据库备份", "数据库"),
            Agent("a3", "三号", "负责网络安全", "网络"),
            Agent("a4", "四号", "负责界面设计", "界面"),
        };
        var sug = MentionSuggester.Suggest("帮我做磁盘清理、数据库备份和网络安全检查", profiles);
        // 三个命中关键词的数字员工都应返回；不相关者不返回；总数不超过 3
        Assert.Equal(3, sug.Count);
        Assert.Contains(sug, s => s.AgentId == "a1");
        Assert.Contains(sug, s => s.AgentId == "a2");
        Assert.Contains(sug, s => s.AgentId == "a3");
        Assert.DoesNotContain(sug, s => s.AgentId == "a4");
    }

    [Fact]
    public void CjkBigrams_ExtractsAdjacentHanPairs_Only()
    {
        var set = MentionSuggester.CjkBigrams("中文分词");
        Assert.Equal(3, set.Count);
        Assert.Contains("中文", set);
        Assert.Contains("文分", set);
        Assert.Contains("分词", set);
        // 汉字被 ASCII 隔断 → 不构成相邻二元组
        Assert.Empty(MentionSuggester.CjkBigrams("a中b文1"));
        Assert.Empty(MentionSuggester.CjkBigrams("abc123"));
        Assert.Empty(MentionSuggester.CjkBigrams(""));
    }
}
