using AguiGroupChat.Agents;
using AguiGroupChat.Agents.BuiltinSkills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>内置 pptx 技能播种：开箱即用、可关闭、跨恢复存活、不覆盖用户改动。</summary>
public sealed class BuiltinPptxSkillsTests
{
    private static AgentSkillCatalog NewCatalog(bool? enabled = null)
        => new(NullLoggerFactory.Instance, new AgentOptions { BuiltinPptxSkills = enabled });

    [Fact]
    public void SeedsDeckSkillByDefault()
    {
        var c = NewCatalog();
        var s = c.Get("pptx_deck");
        Assert.NotNull(s);
        Assert.Equal(AgentSkillKind.Dotnet, s!.Kind);
        Assert.Equal(AgentSkillExecutionLocation.Server, s.ExecutionLocation);
        Assert.True(s.RequiresApproval, "dotnet 技能必须强制人工审批");
        Assert.Null(s.OwnerId); // 系统内置
        Assert.False(string.IsNullOrWhiteSpace(s.Body));
        Assert.Contains("public class Skill", s.Body);
        Assert.Contains("public static string Run(string input)", s.Body);
    }

    [Fact]
    public void ResourceBodyIsCompleteAndLfNormalized()
    {
        var body = NewCatalog().Get("pptx_deck")!.Body;
        // 三个 NuGet 依赖都在（缺一编译不过）
        Assert.Contains("DocumentFormat.OpenXml", body);
        Assert.Contains("SixLabors.ImageSharp", body);
        Assert.Contains("SixLabors.ImageSharp.Drawing", body);
        // 关键 API 必须在正文里（防止嵌入时被截断 / 同步漏掉）
        Assert.Contains("PresentationDocument", body);
        Assert.Contains("SlideMasterPart", body);
        Assert.Contains("produce_file", body);
        Assert.DoesNotContain("\r", body); // 换行统一 LF
        Assert.True(body.Length > 40000, $"正文疑似被截断，长度={body.Length}");
    }

    [Fact]
    public void CanBeDisabledByConfig()
    {
        Assert.Null(NewCatalog(enabled: false).Get("pptx_deck"));
    }

    [Fact]
    public void SurvivesPersistenceRestore()
    {
        // 关键：恢复会先清空目录；内置技能必须被重放，否则重启后消失
        var c = NewCatalog();
        c.RestoreAll([]);
        Assert.NotNull(c.Get("pptx_deck"));
    }

    [Fact]
    public void UserEditWinsOverBuiltinOnRestore()
    {
        var c = NewCatalog();
        c.RestoreAll([
            new AgentSkillDefinition
            {
                SkillId = "pptx_deck", Name = "被我改过", Description = "自定义",
                Kind = AgentSkillKind.Dotnet, Body = "public class Skill { public static string Run(string i)=>i; }",
                BuiltinVersion = null,
                OwnerId = "user_someone",
            },
        ]);
        Assert.Equal("被我改过", c.Get("pptx_deck")!.Name);
    }

    [Fact]
    public void LegacyBuiltinSnapshot_IsRefreshedOnUpgrade()
    {
        // BuiltinVersion 字段上线前持久化的旧内置技能（无标记、OwnerId=null）升级时必须刷新
        var c = NewCatalog();
        c.RestoreAll([
            new AgentSkillDefinition
            {
                SkillId = "pptx_deck", Name = "旧版", Description = "旧描述",
                Kind = AgentSkillKind.Dotnet, Body = "旧正文",
                BuiltinVersion = null, OwnerId = null,
            },
        ]);
        var s = c.Get("pptx_deck")!;
        Assert.Contains("public class Skill", s.Body);
        Assert.Equal(BuiltinPptxSkills.Version, s.BuiltinVersion);
    }

    [Fact]
    public void BuiltinSeedCarriesVersionMarker()
    {
        Assert.Equal(BuiltinPptxSkills.Version, NewCatalog().Get("pptx_deck")!.BuiltinVersion);
    }

    [Fact]
    public void CoexistsWithDocxBuiltins()
    {
        // 两类内置技能互不影响：默认都播种
        var c = NewCatalog();
        Assert.NotNull(c.Get("pptx_deck"));
        Assert.NotNull(c.Get("docx_report"));
    }

    [Fact]
    public void Description_DocumentsEverySlideType()
    {
        // 描述必须把页型列全，否则模型会自创页型 / 漏页型（实测踩到过同类问题）
        var desc = NewCatalog().Get("pptx_deck")!.Description;
        foreach (var type in new[] { "cover", "toc", "section", "content", "twoCol", "table", "kpi", "quote", "image", "chart", "summary", "end" })
            Assert.Contains($"\"{type}\"", desc);
        Assert.Contains("themeColors", desc);
        Assert.Contains("notes", desc);
    }
}
