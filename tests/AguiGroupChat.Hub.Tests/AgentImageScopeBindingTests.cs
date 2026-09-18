using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 「数字员工绑定图库」→ 文档技能实际可检索的范围。
///
/// <para>
/// 这是图库权限的<b>收口点</b>：绑定不是界面上的一个装饰，而是“这个岗位出稿时只能用哪些图”的硬约束
/// （典型诉求：对外宣讲岗只准用已审核的品牌图库）。所以这里不测界面，只测范围句柄里到底登记了哪些图库。
/// </para>
///
/// <para>
/// 为什么直接看句柄而不是走回调接口：句柄只有自令牌能解析，测试里用
/// <see cref="ImageLibraryCatalog.ResolveSearchScope"/> 就能看到平台登记的真相，比绕过 HTTP 更直接。
/// </para>
/// </summary>
public sealed class AgentImageScopeBindingTests
{
    private sealed class FakeStore : IMessageMemoryStore
    {
        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record) { }
        public void Remove(string groupId, string messageId) { }
        public void RemoveGroup(string groupId) { }
        public void ClearAll() { }
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset) => [];
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs) => 0;
        public int PruneExpired(long nowMs) => 0;
        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope) => [];
        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore) => [];
    }

    private sealed class FakeEmbedding : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(string.IsNullOrWhiteSpace(text) ? null : [0.1f, 0.2f]);
        public void Dispose() { }
    }

    private const string User = "user_owner";
    private const string Other = "user_other";
    private const string AgentId = "agent_doc";

    private static readonly AgentSkillDefinition DocxSkill = new()
    {
        SkillId = "docx_report",
        Name = "工作报告生成（Word）",
        Description = "生成工作报告 Word 文档",
        Kind = AgentSkillKind.Dotnet,
        ExecutionLocation = AgentSkillExecutionLocation.Server,
        Body = "",
    };

    private static readonly AgentSkillDefinition ShellSkill = new()
    {
        SkillId = "list_files",
        Name = "列目录",
        Description = "列出目录文件",
        Kind = AgentSkillKind.Shell,
        ExecutionLocation = AgentSkillExecutionLocation.Server,
        Body = "ls",
    };

    private static (AgentCatalog Catalog, ImageLibraryCatalog Libs) NewCatalog()
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            Agents = [new AgentDefinition { AgentId = AgentId, Nickname = "文档岗", Instructions = "x" }],
        };
        var root = Path.Combine(Path.GetTempPath(), "agui-imgscope-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // 图库目录自己要能拿到 store / embedding；AgentCatalog 也要能拿到同一个图库目录（注入时查它）
        var libsServices = new ServiceCollection();
        libsServices.AddSingleton<IMessageMemoryStore>(new FakeStore());
        libsServices.AddSingleton<IEmbeddingProvider>(new FakeEmbedding());
        var libs = new ImageLibraryCatalog(options, libsServices.BuildServiceProvider(),
            NullLoggerFactory.Instance, root);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageMemoryStore>(new FakeStore());
        services.AddSingleton<IEmbeddingProvider>(new FakeEmbedding());
        services.AddSingleton(libs);
        return (new AgentCatalog(options, NullLoggerFactory.Instance, services.BuildServiceProvider()), libs);
    }

    /// <summary>把岗位改成“绑定这些图库”（空 = 不绑定，用触发者全部可读图库）。</summary>
    private static void Bind(AgentCatalog catalog, params string[] libraryIds)
        => catalog.Upsert(new AgentDefinition
        {
            AgentId = AgentId,
            Nickname = "文档岗",
            Instructions = "x",
            ImageLibraryIds = libraryIds.ToList(),
        });

    private static IReadOnlyList<string>? ScopeOf(string injected, ImageLibraryCatalog libs)
    {
        using var doc = JsonDocument.Parse(injected);
        Assert.True(doc.RootElement.TryGetProperty("imageScopeId", out var h), "应注入 imageScopeId：" + injected);
        return libs.ResolveSearchScope(h.GetString());
    }

    [Fact]
    public void BoundLibrary_RestrictsScopeToThatLibraryOnly()
    {
        var (catalog, libs) = NewCatalog();
        var brand = libs.CreateLibrary("品牌图库", "", User);
        var otherMine = libs.CreateLibrary("我的别的图库", "", User);
        libs.CreateLibrary("别人建的图库", "", Other);
        Bind(catalog, brand.LibId);

        var scope = ScopeOf(catalog.WithImageScope("{\"title\":\"x\"}", DocxSkill, User, AgentId), libs);
        Assert.NotNull(scope);
        Assert.Equal([brand.LibId], scope);
        Assert.DoesNotContain(otherMine.LibId, scope!);
    }

    [Fact]
    public void NoBinding_UsesAllLibrariesReadableByTriggerUser()
    {
        var (catalog, libs) = NewCatalog();
        var a = libs.CreateLibrary("甲", "", User);
        var b = libs.CreateLibrary("乙", "", User);
        libs.CreateLibrary("别人的", "", Other);

        var scope = ScopeOf(catalog.WithImageScope("{\"title\":\"x\"}", DocxSkill, User, AgentId), libs);
        Assert.NotNull(scope);
        Assert.Equal(new[] { a.LibId, b.LibId }.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            scope!.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void BoundLibraryMissing_FallsBackToNothingRatherThanEverything()
    {
        var (catalog, libs) = NewCatalog();
        libs.CreateLibrary("本人可读的库", "", User);   // 未被绑定 → 不该被用上
        Bind(catalog, "img_已删除");

        // 绑定的库都没了 → 不注入句柄（技能按“无图库”降级），而**不是**放宽成本人全部图库
        Assert.Equal("{\"title\":\"x\"}", catalog.WithImageScope("{\"title\":\"x\"}", DocxSkill, User, AgentId));
    }

    [Fact]
    public void BoundLibraryMissing_KeepsTheRemainingOnes()
    {
        var (catalog, libs) = NewCatalog();
        var alive = libs.CreateLibrary("还在的库", "", User);
        libs.CreateLibrary("不该被用上的库", "", User);
        Bind(catalog, alive.LibId, "img_已删除");

        var scope = ScopeOf(catalog.WithImageScope("{\"title\":\"x\"}", DocxSkill, User, AgentId), libs);
        Assert.Equal([alive.LibId], scope);
    }

    [Fact]
    public void NonDocumentSkill_IsLeftUntouched()
    {
        var (catalog, libs) = NewCatalog();
        libs.CreateLibrary("任意库", "", User);
        Assert.Equal("ls -la", catalog.WithImageScope("ls -la", ShellSkill, User, AgentId));
    }

    [Fact]
    public void NoLibraryAtAll_LeavesInputUntouched()
    {
        var (catalog, _) = NewCatalog();
        Assert.Equal("{\"title\":\"x\"}", catalog.WithImageScope("{\"title\":\"x\"}", DocxSkill, User, AgentId));
    }

    [Fact]
    public void NonJsonInput_IsLeftUntouched()
    {
        var (catalog, libs) = NewCatalog();
        libs.CreateLibrary("任意库", "", User);
        // 模型偶尔会把正文而不是 JSON 交进来：注入不能把这类入参改坏
        Assert.Equal("写一份年度总结", catalog.WithImageScope("写一份年度总结", DocxSkill, User, AgentId));
    }
}
