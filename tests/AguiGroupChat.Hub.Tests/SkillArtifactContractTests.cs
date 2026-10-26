using System.IO.Compression;
using System.Text.Json;
using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>文档技能产物契约：缺失、空壳、格式不匹配与合格产物的判定。</summary>
public sealed class SkillArtifactContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agui-artifact-" + Guid.NewGuid().ToString("N"));

    public SkillArtifactContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 测试清理失败不影响断言 */ }
    }

    private static AgentSkillDefinition Skill(string id, AgentSkillKind kind = AgentSkillKind.Dotnet)
        => new() { SkillId = id, Name = id, Kind = kind };

    private static string Output(string path)
        => "{\"ok\":true,\"produce_file\":" + JsonSerializer.Serialize(new
        {
            path,
            name = Path.GetFileName(path),
            bytes = new FileInfo(path).Length,
        }) + "}";

    private string Office(string name, params (string Entry, string Content)[] parts)
    {
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (entry, content) in parts)
        {
            var e = zip.CreateEntry(entry, CompressionLevel.NoCompression);
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }
        return path;
    }

    private string Docx(string name, string bodyText)
        => Office(name,
            ("word/document.xml",
                "<w:document><w:body><w:p><w:r><w:t>" + bodyText + "</w:t></w:r></w:p></w:body></w:document>"),
            ("docProps/core.xml", new string('x', 600)));

    [Fact]
    public void ExpectedKind_UsesStrongIdSignals()
    {
        Assert.Equal(SkillArtifactKind.Docx, SkillArtifactContract.ExpectedKind(Skill("docx_report")));
        Assert.Equal(SkillArtifactKind.Docx, SkillArtifactContract.ExpectedKind(Skill("md_to_docx")));
        Assert.Equal(SkillArtifactKind.Xlsx, SkillArtifactContract.ExpectedKind(Skill("xlsx_book")));
        Assert.Equal(SkillArtifactKind.Pptx, SkillArtifactContract.ExpectedKind(Skill("pptx_deck")));
        Assert.Equal(SkillArtifactKind.Pdf, SkillArtifactContract.ExpectedKind(Skill("pdf_doc")));
        Assert.Equal(SkillArtifactKind.None, SkillArtifactContract.ExpectedKind(Skill("query_weather")));
        Assert.Equal(SkillArtifactKind.None, SkillArtifactContract.ExpectedKind(Skill("docx_report", AgentSkillKind.Prompt)));
    }

    [Fact]
    public void SampleInputs_AreValidJsonAndNonEmpty()
    {
        foreach (var kind in new[] { SkillArtifactKind.Docx, SkillArtifactKind.Xlsx, SkillArtifactKind.Pptx, SkillArtifactKind.Pdf })
        {
            var sample = SkillArtifactContract.SampleInput(kind);
            Assert.False(string.IsNullOrWhiteSpace(sample));
            using var _ = JsonDocument.Parse(sample); // 抛异常即失败
        }
    }

    [Fact]
    public void Docx_WithoutMarker_FailsContract()
    {
        var validation = SkillArtifactContract.Validate(Skill("docx_report"), "已完成排版。");

        Assert.True(validation.HasExpectedArtifact);
        Assert.False(validation.IsSatisfied);
        Assert.Contains(validation.Issues, i => i.Contains("produce_file", StringComparison.Ordinal));
    }

    [Fact]
    public void Docx_TitleOnly_FailsWithShortBodyReason()
    {
        var path = Docx("title-only.docx", "标题");
        var validation = SkillArtifactContract.Validate(Skill("docx_report"), Output(path));

        Assert.False(validation.IsSatisfied);
        Assert.Contains(validation.Issues, i => i.Contains("只有标题或正文过短", StringComparison.Ordinal));
    }

    [Fact]
    public void Docx_WithRealBody_PassesAndAddsNoWarning()
    {
        var path = Docx("good.docx", "这是一份包含实际正文的文档，用于验证产物契约不会把合格文件误判为空壳。");
        var skill = Skill("docx_report");
        var text = Output(path) + "\n排版完成。";

        var validation = SkillArtifactContract.Validate(skill, text);
        Assert.True(validation.IsSatisfied);
        Assert.Equal(text, SkillArtifactContract.AppendWarningIfViolated(skill, text));
    }

    [Fact]
    public void Pptx_WithSlidesAndText_Passes()
    {
        var path = Office("good.pptx",
            ("ppt/presentation.xml", "<p:presentation/>"),
            ("ppt/slides/slide1.xml", "<p:sld><a:t>第一页正文内容，包含足够多的实际文字用于通过契约校验。</a:t><a:t>第二段内容。</a:t></p:sld>"),
            ("docProps/core.xml", new string('x', 600)));

        Assert.True(SkillArtifactContract.Validate(Skill("pptx_deck"), Output(path)).IsSatisfied);
    }

    [Fact]
    public void Xlsx_WithCells_Passes()
    {
        var path = Office("good.xlsx",
            ("xl/workbook.xml", "<workbook/>"),
            ("xl/worksheets/sheet1.xml", "<worksheet><row r=\"1\"><c r=\"A1\"><v>甲</v></c></row></worksheet>"),
            ("docProps/core.xml", new string('x', 600)));

        Assert.True(SkillArtifactContract.Validate(Skill("xlsx_book"), Output(path)).IsSatisfied);
    }

    [Fact]
    public void Pdf_WithPage_Passes()
    {
        var path = Path.Combine(_dir, "good.pdf");
        File.WriteAllText(path,
            "%PDF-1.4\n1 0 obj\n<< /Type /Page /MediaBox [0 0 595 842] >>\nendobj\n"
            + new string('x', 700) + "\n%%EOF");

        Assert.True(SkillArtifactContract.Validate(Skill("pdf_doc"), Output(path)).IsSatisfied);
    }

    [Fact]
    public void WrongExtension_IsReported()
    {
        var path = Docx("real.docx", "这是一份包含实际正文的文档，用于验证扩展名不匹配时会被拒绝。");
        var validation = SkillArtifactContract.Validate(Skill("pdf_doc"), Output(path));

        Assert.False(validation.IsSatisfied);
        Assert.Contains(validation.Issues, i => i.Contains(".pdf", StringComparison.Ordinal));
    }

    [Fact]
    public void AppendWarning_IsAddedOnlyWhenContractFails()
    {
        var missing = "文档生成完成。";
        var text = SkillArtifactContract.AppendWarningIfViolated(Skill("docx_report"), missing);

        Assert.Contains(SkillArtifactContract.FailurePrefix, text);
        Assert.StartsWith(missing, text);
        // 重复调用不叠加告警
        Assert.Equal(text, SkillArtifactContract.AppendWarningIfViolated(Skill("docx_report"), text));
    }
}
