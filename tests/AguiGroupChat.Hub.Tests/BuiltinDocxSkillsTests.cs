using AguiGroupChat.Agents;
using AguiGroupChat.Agents.BuiltinSkills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>内置 docx 技能播种：开箱即用、可关闭、跨恢复存活、不覆盖用户改动。</summary>
public sealed class BuiltinDocxSkillsTests
{
    private static AgentSkillCatalog NewCatalog(bool? enabled = null)
        => new(NullLoggerFactory.Instance, new AgentOptions { BuiltinDocxSkills = enabled });

    [Fact]
    public void SeedsThreeSkillsByDefault()
    {
        // 默认（未配置）= 开启：新部署开箱即用
        var c = NewCatalog();
        foreach (var id in new[] { "docx_gongwen", "docx_notice", "docx_report" })
        {
            var s = c.Get(id);
            Assert.NotNull(s);
            Assert.Equal(AgentSkillKind.Dotnet, s!.Kind);
            Assert.Equal(AgentSkillExecutionLocation.Server, s.ExecutionLocation);
            Assert.True(s.RequiresApproval, "dotnet 技能必须强制人工审批");
            Assert.Null(s.OwnerId); // 系统内置
            Assert.False(string.IsNullOrWhiteSpace(s.Body));
            Assert.Contains("public class Skill", s.Body);
            Assert.Contains("public static string Run(string input)", s.Body);
        }
    }

    [Fact]
    public void ResourceBodyIsCompleteAndLfNormalized()
    {
        var c = NewCatalog();
        var body = c.Get("docx_gongwen")!.Body;
        // 三个 NuGet 依赖都在（缺一编译不过）
        Assert.Contains("DocumentFormat.OpenXml", body);
        Assert.Contains("SixLabors.ImageSharp", body);
        Assert.Contains("SixLabors.ImageSharp.Drawing", body);
        // 换行统一 LF（避免 CRLF 混入技能正文）
        Assert.DoesNotContain("\r", body);
        // 体积与生成物一致（防止嵌入时被截断）
        Assert.True(body.Length > 40000, $"正文疑似被截断，长度={body.Length}");
    }

    [Fact]
    public void CanBeDisabledByConfig()
    {
        var c = NewCatalog(enabled: false);
        Assert.Null(c.Get("docx_gongwen"));
        Assert.Null(c.Get("docx_notice"));
        Assert.Null(c.Get("docx_report"));
    }

    [Fact]
    public void SurvivesPersistenceRestore()
    {
        // 关键：恢复会先清空目录；内置技能必须被重放，否则重启后消失
        var c = NewCatalog();
        c.RestoreAll([]); // 模拟“快照为空”（如新装/被清库）
        foreach (var id in new[] { "docx_gongwen", "docx_notice", "docx_report" })
            Assert.NotNull(c.Get(id));
    }

    [Fact]
    public void UserEditWinsOverBuiltinOnRestore()
    {
        // 用户在界面上改过内置技能 → 快照里的版本优先，不被升级覆盖。
        // 编辑过的特征：OwnerId 记录编辑器（非 null），BuiltinVersion 被置空。
        var c = NewCatalog();
        c.RestoreAll([
            new AgentSkillDefinition
            {
                SkillId = "docx_gongwen", Name = "被我改过", Description = "自定义",
                Kind = AgentSkillKind.Dotnet, Body = "public class Skill { public static string Run(string i)=>i; }",
                BuiltinVersion = null,
                OwnerId = "user_someone", // 用户编辑后记录的归属
            },
        ]);
        var s = c.Get("docx_gongwen")!;
        Assert.Equal("被我改过", s.Name);
        Assert.Equal("自定义", s.Description);
    }

    [Fact]
    public void LegacyBuiltinSnapshot_IsRefreshedOnUpgrade()
    {
        // 关键回归：BuiltinVersion 字段上线前持久化的旧内置技能（无标记、OwnerId=null），
        // 升级时必须用新正文刷新 —— 否则平台修好的实现（落盘目录/字体/命名）永远上不去。
        // 本仓库真实踩到过：库里 docx_report 正文停在 44348 字符的旧版。
        var c = NewCatalog();
        c.RestoreAll([
            new AgentSkillDefinition
            {
                SkillId = "docx_report", Name = "旧版", Description = "旧描述",
                Kind = AgentSkillKind.Dotnet, Body = "旧正文",
                BuiltinVersion = null, OwnerId = null, // 旧格式内置技能的特征
            },
        ]);
        var s = c.Get("docx_report")!;
        Assert.Contains("public class Skill", s.Body);            // 已刷新为新正文
        Assert.NotEqual("旧正文", s.Body);
        Assert.Equal(BuiltinDocxSkills.Version, s.BuiltinVersion); // 并补上新标记
    }

    [Fact]
    public void SnapshotMatchingShippedBuiltin_IsKeptButReStamped()
    {
        // 正文未改但版本号是旧的 → 刷新为当前版本标记
        var fresh = NewCatalog().Get("docx_report")!;
        var c = NewCatalog();
        c.RestoreAll([
            new AgentSkillDefinition
            {
                SkillId = "docx_report", Name = fresh.Name, Description = fresh.Description,
                Kind = fresh.Kind, Body = fresh.Body,
                BuiltinVersion = "2026-01-01.0",
            },
        ]);
        Assert.Equal(BuiltinDocxSkills.Version, c.Get("docx_report")!.BuiltinVersion);
    }

    [Fact]
    public void BuiltinSeedCarriesVersionMarker()
    {
        // 播种时就该带版本标记，否则升级刷新逻辑永远不生效
        var c = NewCatalog();
        Assert.Equal(BuiltinDocxSkills.Version, c.Get("docx_gongwen")!.BuiltinVersion);
    }

    [Fact]
    public void DoesNotOverrideConfiguredSeedWithSameId()
    {
        // appsettings 种子先播种；同 id 的内置技能不应覆盖它
        var c = new AgentSkillCatalog(NullLoggerFactory.Instance, new AgentOptions
        {
            Skills = [new AgentSkillDefinition { SkillId = "docx_gongwen", Name = "配置版", Kind = AgentSkillKind.Prompt }],
        });
        Assert.Equal("配置版", c.Get("docx_gongwen")!.Name);
    }

    [Fact]
    public void CatalogAcceptsExternalAppend()
    {
        // 与既有技能共存：播种不应影响别的技能
        var c = NewCatalog();
        c.Upsert(new AgentSkillDefinition { SkillId = "skill_x", Name = "别的", Kind = AgentSkillKind.Prompt });
        Assert.NotNull(c.Get("skill_x"));
        Assert.NotNull(c.Get("docx_report"));
        Assert.Equal(4, c.ListAll().Count);
    }
}
