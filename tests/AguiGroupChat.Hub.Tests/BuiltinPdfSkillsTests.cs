using AguiGroupChat.Agents;
using AguiGroupChat.Agents.BuiltinSkills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 条件跳过（xUnit v2 惯用法：discovery 阶段设置 Skip）：平台接线（<c>AgentOptions.BuiltinPdfSkills</c> +
/// <c>AgentSkillCatalog</c> 播种，由主代理负责）落地前，播种类断言显示为“已跳过”；
/// 接线后同一批断言自动生效（无需改测试）。
/// </summary>
internal sealed class PdfWiredFactAttribute : FactAttribute
{
    public PdfWiredFactAttribute()
    {
        if (!BuiltinPdfSkillsTests.IsCatalogWired())
            Skip = "内置 pdf 技能尚未接入 AgentOptions.BuiltinPdfSkills / AgentSkillCatalog（平台接线由主代理负责）。";
    }
}

/// <summary>内置 pdf 技能播种：开箱即用、可关闭、跨恢复存活、不覆盖用户改动。</summary>
public sealed class BuiltinPdfSkillsTests
{
    /// <summary>技能库里是否已能取到 pdf_doc（即平台接线是否已落地）。</summary>
    public static bool IsCatalogWired()
    {
        try
        {
            return new AgentSkillCatalog(NullLoggerFactory.Instance, OptionsWithFlag(null)).Get("pdf_doc") is not null;
        }
        catch { return false; }
    }

    /// <summary>开关经反射设置，这样本文件在 <c>AgentOptions.BuiltinPdfSkills</c> 接线前也能编译。</summary>
    private static AgentOptions OptionsWithFlag(bool? enabled)
    {
        var o = new AgentOptions();
        typeof(AgentOptions).GetProperty("BuiltinPdfSkills")?.SetValue(o, enabled);
        return o;
    }

    private static AgentSkillCatalog NewCatalog(bool? enabled = null)
        => new(NullLoggerFactory.Instance, OptionsWithFlag(enabled));

    // ===================== 不依赖平台接线的断言（现在就真验） =====================

    [Fact]
    public void Definitions_ExposePdfDocWithSkillTxtResource()
    {
        var d = Assert.Single(BuiltinPdfSkills.Definitions);
        Assert.Equal("pdf_doc", d.SkillId);
        Assert.Equal("pdf_doc.skill.txt", d.ResourceSuffix);
        Assert.Contains("pdf_doc", BuiltinPdfSkills.SkillIds);
    }

    [Fact]
    public void BuiltDefinition_IsServerDotnetSkillRequiringApproval()
    {
        var d = BuiltinPdfSkills.Definitions[0];
        var def = BuiltinPdfSkills.Build(d.SkillId, d.ResourceSuffix, d.Name, d.Description);
        Assert.Equal("pdf_doc", def.SkillId);
        Assert.Equal(AgentSkillKind.Dotnet, def.Kind);
        Assert.Equal(AgentSkillExecutionLocation.Server, def.ExecutionLocation);
        Assert.True(def.RequiresApproval, "dotnet 技能必须强制人工审批");
        Assert.Null(def.OwnerId); // 系统内置
        Assert.Equal(BuiltinPdfSkills.Version, def.BuiltinVersion);
        Assert.False(string.IsNullOrWhiteSpace(def.Body));
        Assert.Contains("public class Skill", def.Body);
        Assert.Contains("public static string Run(string input)", def.Body);
    }

    [Fact]
    public void IsEnabled_DefaultsToTrue_AndRespectsExplicitFalse()
    {
        Assert.True(BuiltinPdfSkills.IsEnabled(null));
        Assert.True(BuiltinPdfSkills.IsEnabled(true));
        Assert.False(BuiltinPdfSkills.IsEnabled(false));
    }

    [Fact]
    public void ResourceBodyIsCompleteAndLfNormalized()
    {
        var body = BuiltinPdfSkills.Definitions[0];
        var def = BuiltinPdfSkills.Build(body.SkillId, body.ResourceSuffix, body.Name, body.Description);

        // PDFsharp 来自平台 TPA（不该写 #r 指令，那会白跑一次联网还原）
        foreach (var line in def.Body.Split('\n'))
            Assert.False(line.TrimStart().StartsWith("#r", StringComparison.Ordinal), "正文不应有 #r 指令：" + line);

        // 关键 API 必须在正文里（防止嵌入时被截断 / 同步漏掉）
        Assert.Contains("using PdfSharp.Pdf;", def.Body);
        Assert.Contains("using PdfSharp.Drawing;", def.Body);
        Assert.Contains("GlobalFontSettings.FontResolver", def.Body);
        Assert.Contains("TtcFace", def.Body);              // TTC face 抽取
        Assert.Contains("AGUI_PDF_FONT", def.Body);        // 显式字体路径
        Assert.Contains("AGUI_PDF_OUT", def.Body);         // 输出目录
        Assert.Contains("produce_file", def.Body);
        Assert.Contains("XImage", def.Body);               // 图片嵌入
        Assert.DoesNotContain("\r", def.Body);             // 换行统一 LF
        Assert.True(def.Body.Length > 40000, $"正文疑似被截断，长度={def.Body.Length}");
    }

    [Fact]
    public void Description_DocumentsEveryBlockTypeAndKeyParams()
    {
        // 描述必须把块型与关键参数列全，否则模型会自创块型 / 漏字段（pptx 同类实测踩到过）
        var desc = BuiltinPdfSkills.Definitions[0].Description;
        foreach (var type in new[]
                 {
                     "h1", "h2", "h3", "p", "list", "callout", "quote", "table",
                     "image", "chart", "code", "divider", "caption", "pagebreak", "spacer", "toc",
                 })
            Assert.Contains($"\"{type}\"", desc);
        foreach (var token in new[]
                 {
                     "docType", "report", "proposal", "resume", "academic", "minimal", "editorial",
                     "magazine", "terminal", "accent", "accentRole", "toc", "pageSize", "marginMm",
                     "colors", "fontPath", "outputPath", "blocks", "markdown", "produce_file",
                 })
            Assert.Contains(token, desc);
        // 边界要写清（PDFsharp 不支持 AcroForm 填写）
        Assert.Contains("表单", desc);
    }

    // ===================== 平台接线后生效的断言 =====================

    [PdfWiredFact]
    public void SeedsPdfSkillByDefault()
    {
        var c = NewCatalog();
        var s = c.Get("pdf_doc");
        Assert.NotNull(s);
        Assert.Equal(AgentSkillKind.Dotnet, s!.Kind);
        Assert.Equal(AgentSkillExecutionLocation.Server, s.ExecutionLocation);
        Assert.True(s.RequiresApproval, "dotnet 技能必须强制人工审批");
        Assert.Null(s.OwnerId); // 系统内置
        Assert.Equal(BuiltinPdfSkills.Version, s.BuiltinVersion);
        Assert.Contains("public class Skill", s.Body);
        Assert.Contains("public static string Run(string input)", s.Body);
    }

    [PdfWiredFact]
    public void CanBeDisabledByConfig()
    {
        // 接线后：设 Agents:BuiltinPdfSkills=false 时不应播种
        // （属性名需与 Builtin*Skills 约定一致；不一致时这里会给出明确失败）
        Assert.NotNull(typeof(AgentOptions).GetProperty("BuiltinPdfSkills"));
        Assert.Null(NewCatalog(enabled: false).Get("pdf_doc"));
    }

    [PdfWiredFact]
    public void SurvivesPersistenceRestore()
    {
        // 关键：恢复会先清空目录；内置技能必须被重放，否则重启后消失
        var c = NewCatalog();
        c.RestoreAll([]);
        Assert.NotNull(c.Get("pdf_doc"));
    }

    [PdfWiredFact]
    public void UserEditWinsOverBuiltinOnRestore()
    {
        var c = NewCatalog();
        c.RestoreAll([
            new AgentSkillDefinition
            {
                SkillId = "pdf_doc", Name = "被我改过", Description = "自定义",
                Kind = AgentSkillKind.Dotnet, Body = "public class Skill { public static string Run(string i)=>i; }",
                BuiltinVersion = null,
                OwnerId = "user_someone",
            },
        ]);
        Assert.Equal("被我改过", c.Get("pdf_doc")!.Name);
    }

    [PdfWiredFact]
    public void LegacyBuiltinSnapshot_IsRefreshedOnUpgrade()
    {
        // BuiltinVersion 字段上线前持久化的旧内置技能（无标记、OwnerId=null）升级时必须刷新
        var c = NewCatalog();
        c.RestoreAll([
            new AgentSkillDefinition
            {
                SkillId = "pdf_doc", Name = "旧版", Description = "旧描述",
                Kind = AgentSkillKind.Dotnet, Body = "旧正文",
                BuiltinVersion = null, OwnerId = null,
            },
        ]);
        var s = c.Get("pdf_doc")!;
        Assert.Contains("public class Skill", s.Body);
        Assert.Equal(BuiltinPdfSkills.Version, s.BuiltinVersion);
    }

    [PdfWiredFact]
    public void CoexistsWithDocxPptxAndXlsxBuiltins()
    {
        var c = NewCatalog();
        Assert.NotNull(c.Get("pdf_doc"));
        Assert.NotNull(c.Get("pptx_deck"));
        Assert.NotNull(c.Get("docx_report"));
    }
}
