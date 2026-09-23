using System.Text;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 提示词装配预算（<see cref="PromptBudgetOptions"/>）与总闸门裁剪（<see cref="AgentGateway.TrimSectionsToBudget"/>）单测。
///
/// <para>
/// 背景：此前各项上限散落在两处、各自独立，且截断<b>全部静默</b>——“看到文字被截断却查不出在哪截的”。
/// 这里钉住三件事：① 默认值就是放宽后的那组；② 非法配置被夹紧而不是带病运行；
/// ③ 总闸门严格按降级顺序牺牲（历史先于历史附件先于当前附件），且从尾部裁。
/// </para>
/// </summary>
public sealed class PromptBudgetTests
{
    // ===================== 默认值与夹紧 =====================

    [Fact]
    public void Defaults_AreTheRelaxedSet()
    {
        var d = PromptBudgetOptions.Default;
        Assert.Equal(200_000, d.MaxTotalChars);
        Assert.Equal(12, d.HistoryWindowMessages);
        Assert.Equal(4_000, d.MaxCharsPerHistoryMessage);      // 旧硬编码 500
        Assert.Equal(48_000, d.MaxHistoryChars);
        Assert.Equal(96_000, d.MaxHistoryInlineTextChars);     // 旧硬编码 24_000
        Assert.Equal(40_000, d.AttachmentMaxTextCharsPerFile); // 旧硬编码 12_000
        Assert.Equal(200_000, d.AttachmentMaxTextCharsTotal);  // 旧硬编码 60_000
        Assert.Equal(4, d.MaxContextImages);
        Assert.Equal(4, d.MaxHistoryImages);
        Assert.Equal(100_000, d.WarnPromptTokens);
        Assert.Equal(["history", "history_attachments", "attachments"], d.TruncationOrder);
    }

    [Fact]
    public void Normalize_FallsBackToDefaults_OnIllegalValues()
    {
        var o = new PromptBudgetOptions
        {
            MaxTotalChars = -1,
            HistoryWindowMessages = 0,
            MaxCharsPerHistoryMessage = 5,          // 低于下限 50
            AttachmentMaxTextCharsPerFile = 0,
            WarnPromptTokens = 10,                  // 低于下限 1000
        };
        o.Normalize(NullLogger.Instance);

        Assert.Equal(PromptBudgetOptions.Default.MaxTotalChars, o.MaxTotalChars);
        Assert.Equal(PromptBudgetOptions.Default.HistoryWindowMessages, o.HistoryWindowMessages);
        Assert.Equal(PromptBudgetOptions.Default.MaxCharsPerHistoryMessage, o.MaxCharsPerHistoryMessage);
        Assert.Equal(PromptBudgetOptions.Default.AttachmentMaxTextCharsPerFile, o.AttachmentMaxTextCharsPerFile);
        Assert.Equal(PromptBudgetOptions.Default.WarnPromptTokens, o.WarnPromptTokens);
    }

    [Fact]
    public void Normalize_KeepsValidValues()
    {
        var o = new PromptBudgetOptions { MaxTotalChars = 300_000, MaxCharsPerHistoryMessage = 2_000, AttachmentMaxTextCharsTotal = 150_000 };
        o.Normalize();
        Assert.Equal(300_000, o.MaxTotalChars);
        Assert.Equal(2_000, o.MaxCharsPerHistoryMessage);
        Assert.Equal(150_000, o.AttachmentMaxTextCharsTotal);
    }

    [Fact]
    public void Normalize_TruncationOrder_FiltersUnknownAndDedupes()
    {
        var o = new PromptBudgetOptions { TruncationOrder = ["attachments", "bogus", "attachments", "history"] };
        o.Normalize();
        Assert.Equal(["attachments", "history"], o.TruncationOrder);
    }

    [Fact]
    public void Normalize_TruncationOrder_FallsBack_WhenWholeTableInvalid()
    {
        var o = new PromptBudgetOptions { TruncationOrder = ["bogus", ""] };
        o.Normalize();
        Assert.Equal(PromptBudgetOptions.DefaultTruncationOrder, o.TruncationOrder);
    }

    // ===================== 总闸门裁剪 =====================

    private static List<(string Name, StringBuilder Text)> Sections(string history, string historyAtt, string att)
        => [("history", new StringBuilder(history)), ("history_attachments", new StringBuilder(historyAtt)), ("attachments", new StringBuilder(att))];

    [Fact]
    public void Trim_DoesNothing_WhenNotOverBudget()
    {
        var sections = Sections("HHHHH", "AAAAA", "TTTTT");
        var cut = AgentGateway.TrimSectionsToBudget(sections, PromptBudgetOptions.DefaultTruncationOrder, 0);
        Assert.Empty(cut);
        Assert.Equal("HHHHH", sections[0].Text.ToString());
        Assert.Equal("AAAAA", sections[1].Text.ToString());
        Assert.Equal("TTTTT", sections[2].Text.ToString());
    }

    [Fact]
    public void Trim_SacrificesHistoryFirst_AndFromTheTail()
    {
        // 只超 2 个字符 → 只从历史尾部裁 2 个字（尾部 = 更早的消息，头部保留）
        var sections = Sections("H1H2H3H4H5", "AAAAA", "TTTTT");
        var cut = AgentGateway.TrimSectionsToBudget(sections, PromptBudgetOptions.DefaultTruncationOrder, 2);
        Assert.Equal(["history:-2"], cut);
        Assert.Equal("H1H2H3H4", sections[0].Text.ToString());
        Assert.Equal("AAAAA", sections[1].Text.ToString());
        Assert.Equal("TTTTT", sections[2].Text.ToString());
    }

    [Fact]
    public void Trim_MovesToNextStage_WhenFirstIsExhausted()
    {
        // 历史 5 字全裁掉仍不够（还差 3）→ 继续裁历史附件 3 字；当前附件不动
        var sections = Sections("HHHHH", "AAAAA", "TTTTT");
        var cut = AgentGateway.TrimSectionsToBudget(sections, PromptBudgetOptions.DefaultTruncationOrder, 8);
        Assert.Equal(["history:-5", "history_attachments:-3"], cut);
        Assert.Equal("", sections[0].Text.ToString());
        Assert.Equal("AA", sections[1].Text.ToString());
        Assert.Equal("TTTTT", sections[2].Text.ToString());
    }

    [Fact]
    public void Trim_ReachesLastStage_OnlyWhenNeeded()
    {
        var sections = Sections("HHHHH", "AAAAA", "TTTTT");
        var cut = AgentGateway.TrimSectionsToBudget(sections, PromptBudgetOptions.DefaultTruncationOrder, 12);
        Assert.Equal(["history:-5", "history_attachments:-5", "attachments:-2"], cut);
        Assert.Equal("TTT", sections[2].Text.ToString());
    }

    [Fact]
    public void Trim_HonorsCustomOrder()
    {
        // 自定义：先牺牲当前附件
        var sections = Sections("HHHHH", "AAAAA", "TTTTT");
        var cut = AgentGateway.TrimSectionsToBudget(sections, ["attachments", "history", "history_attachments"], 3);
        Assert.Equal(["attachments:-3"], cut);
        Assert.Equal("HHHHH", sections[0].Text.ToString());
        Assert.Equal("TT", sections[2].Text.ToString());
    }

    [Fact]
    public void Trim_SkipsEmptySections()
    {
        var sections = Sections("", "AAAAA", "");
        var cut = AgentGateway.TrimSectionsToBudget(sections, PromptBudgetOptions.DefaultTruncationOrder, 2);
        Assert.Equal(["history_attachments:-2"], cut);
    }

    // ===================== 附件存储从预算取上限 =====================

    [Fact]
    public void AttachmentStore_UsesConfiguredCharLimits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agui-budget-" + Guid.NewGuid().ToString("N"));
        var store = new AttachmentStore(dir, textCharsPerFile: 1_234, textCharsTotal: 5_678);
        Assert.Equal(1_234, store.TextCharsPerFile);
        Assert.Equal(5_678, store.TextCharsTotal);
    }

    [Fact]
    public void AttachmentStore_FallsBackToDefaults_WhenNotConfigured()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agui-budget-" + Guid.NewGuid().ToString("N"));
        var store = new AttachmentStore(dir);
        Assert.Equal(AttachmentStore.DefaultTextCharsPerFile, store.TextCharsPerFile);
        Assert.Equal(AttachmentStore.DefaultTextCharsTotal, store.TextCharsTotal);
    }

    [Fact]
    public void AttachmentStore_FallsBackToDefaults_OnIllegalValues()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agui-budget-" + Guid.NewGuid().ToString("N"));
        var store = new AttachmentStore(dir, textCharsPerFile: 0, textCharsTotal: -5);
        Assert.Equal(AttachmentStore.DefaultTextCharsPerFile, store.TextCharsPerFile);
        Assert.Equal(AttachmentStore.DefaultTextCharsTotal, store.TextCharsTotal);
    }

    // ===================== 推理力度映射 =====================

    [Fact]
    public void BuildReasoningOptions_MapsKnownValues_AndIgnoresUnknown()
    {
        Assert.Null(AgentCatalog.BuildReasoningOptions(null));
        Assert.Null(AgentCatalog.BuildReasoningOptions("   "));
        Assert.Null(AgentCatalog.BuildReasoningOptions("bogus"));
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.None, AgentCatalog.BuildReasoningOptions("none")!.Effort);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.Low, AgentCatalog.BuildReasoningOptions("LOW")!.Effort);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.Medium, AgentCatalog.BuildReasoningOptions("medium")!.Effort);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.High, AgentCatalog.BuildReasoningOptions(" high ")!.Effort);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.ExtraHigh, AgentCatalog.BuildReasoningOptions("xhigh")!.Effort);
    }

    [Fact]
    public void Defaults_OutputBudgetIsExplicit_AndReasoningIsOptIn()
    {
        var options = new AgentOptions();
        Assert.True(options.MaxOutputTokens > 0, "正式回复应显式给出输出预算，不依赖提供方默认值");
        Assert.Null(options.ReasoningEffort); // 推理力度取值需实测后固定，默认不启用
    }

    [Fact]
    public void Defaults_MemorySlicesAreRelaxed()
    {
        var m = new AgentOptions().Memory;
        Assert.Equal(1_500, m.MaxCharsPerMemory); // 旧默认 600
        Assert.Equal(6, m.TopK);                  // 旧默认 5
    }
}
