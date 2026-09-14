using AguiGroupChat.Agents;
using AguiGroupChat.Agents.BuiltinSkills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>内置 xlsx 技能播种：开箱即用、可关闭、跨恢复存活、不覆盖用户改动。</summary>
public sealed class BuiltinXlsxSkillsTests
{
    private static AgentSkillCatalog NewCatalog(bool? enabled = null)
        => new(NullLoggerFactory.Instance, new AgentOptions { BuiltinXlsxSkills = enabled });

    [Fact]
    public void SeedsXlsxSkillByDefault()
    {
        var c = NewCatalog();
        var s = c.Get("xlsx_book");
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
        var body = NewCatalog().Get("xlsx_book")!.Body;
        // NuGet 依赖（缺一编译不过）
        Assert.Contains("DocumentFormat.OpenXml", body);
        // 关键 API 必须在正文里（防止嵌入时被截断 / 同步漏掉）
        Assert.Contains("SpreadsheetDocument", body);
        Assert.Contains("WorksheetPart", body);
        Assert.Contains("StyleBook", body);
        Assert.Contains("produce_file", body);
        Assert.Contains("AGUI_XLSX_OUT", body);
        Assert.DoesNotContain("\r", body); // 换行统一 LF
        Assert.True(body.Length > 20000, $"正文疑似被截断，长度={body.Length}");
        // 刻意不做图表 → 不应引入 ImageSharp（宁可少依赖）
        Assert.DoesNotContain("SixLabors.ImageSharp", body);
    }

    [Fact]
    public void CanBeDisabledByConfig()
    {
        Assert.Null(NewCatalog(enabled: false).Get("xlsx_book"));
    }

    [Fact]
    public void SurvivesPersistenceRestore()
    {
        // 关键：恢复会先清空目录；内置技能必须被重放，否则重启后消失
        var c = NewCatalog();
        c.RestoreAll([]);
        Assert.NotNull(c.Get("xlsx_book"));
    }

    [Fact]
    public void UserEditWinsOverBuiltinOnRestore()
    {
        var c = NewCatalog();
        c.RestoreAll([
            new AgentSkillDefinition
            {
                SkillId = "xlsx_book", Name = "被我改过", Description = "自定义",
                Kind = AgentSkillKind.Dotnet, Body = "public class Skill { public static string Run(string i)=>i; }",
                BuiltinVersion = null,
                OwnerId = "user_someone",
            },
        ]);
        Assert.Equal("被我改过", c.Get("xlsx_book")!.Name);
    }

    [Fact]
    public void LegacyBuiltinSnapshot_IsRefreshedOnUpgrade()
    {
        // BuiltinVersion 字段上线前持久化的旧内置技能（无标记、OwnerId=null）升级时必须刷新
        var c = NewCatalog();
        c.RestoreAll([
            new AgentSkillDefinition
            {
                SkillId = "xlsx_book", Name = "旧版", Description = "旧描述",
                Kind = AgentSkillKind.Dotnet, Body = "旧正文",
                BuiltinVersion = null, OwnerId = null,
            },
        ]);
        var s = c.Get("xlsx_book")!;
        Assert.Contains("public class Skill", s.Body);
        Assert.Equal(BuiltinXlsxSkills.Version, s.BuiltinVersion);
    }

    [Fact]
    public void BuiltinSeedCarriesVersionMarker()
    {
        Assert.Equal(BuiltinXlsxSkills.Version, NewCatalog().Get("xlsx_book")!.BuiltinVersion);
    }

    [Fact]
    public void CoexistsWithDocxAndPptxBuiltins()
    {
        // 三类内置技能互不影响：默认都播种
        var c = NewCatalog();
        Assert.NotNull(c.Get("xlsx_book"));
        Assert.NotNull(c.Get("pptx_deck"));
        Assert.NotNull(c.Get("docx_report"));
    }

    [Fact]
    public void Description_DocumentsInputSchemaAndActions()
    {
        // 描述必须把关键参数与两种 action 列全，否则模型会自创字段 / 不会用 analyze
        var desc = NewCatalog().Get("xlsx_book")!.Description;
        foreach (var token in new[] { "action", "create", "analyze", "sheets", "columns", "rows", "totals", "currency", "percent", "aggregate", "produce_file" })
            Assert.Contains(token, desc);
        Assert.Contains("公式优先", desc);
    }
}
