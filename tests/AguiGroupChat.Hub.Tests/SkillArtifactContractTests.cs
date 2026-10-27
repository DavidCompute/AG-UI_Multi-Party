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
        Assert.Equal(SkillArtifactKind.Image, SkillArtifactContract.ExpectedKind(Skill("image_generate")));
        Assert.Equal(SkillArtifactKind.Archive, SkillArtifactContract.ExpectedKind(Skill("zip_pack")));
        Assert.Equal(SkillArtifactKind.Code, SkillArtifactContract.ExpectedKind(Skill("code_gen")));
        Assert.Equal(SkillArtifactKind.None, SkillArtifactContract.ExpectedKind(Skill("image_reader")));
        Assert.Equal(SkillArtifactKind.None, SkillArtifactContract.ExpectedKind(Skill("zip_reader")));
        Assert.Equal(SkillArtifactKind.None, SkillArtifactContract.ExpectedKind(Skill("code_review")));
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
    public void Docx_WithoutMarker_FailsContractWithZeroScore()
    {
        var validation = SkillArtifactContract.Validate(Skill("docx_report"), "已完成排版。");

        Assert.True(validation.HasExpectedArtifact);
        Assert.False(validation.IsSatisfied);
        Assert.Equal(0, validation.Score);
        Assert.Contains(validation.MissingItems, i => i.Contains("produce_file", StringComparison.Ordinal));
    }

    [Fact]
    public void Image_ValidPng_Passes_AndTinyPngFails()
    {
        var valid = Path.Combine(_dir, "good.png");
        File.WriteAllBytes(valid, Png(64, 64));
        Assert.True(SkillArtifactContract.Validate(Skill("image_generate"), Output(valid)).IsSatisfied);

        var tiny = Path.Combine(_dir, "tiny.png");
        File.WriteAllBytes(tiny, Png(8, 8));
        var validation = SkillArtifactContract.Validate(Skill("image_generate"), Output(tiny));
        Assert.False(validation.IsSatisfied);
        Assert.Contains(validation.MissingItems, i => i.Contains("尺寸过小", StringComparison.Ordinal));
    }

    [Fact]
    public void Archive_WithFile_Passes_AndEmptyZipFails()
    {
        var good = Path.Combine(_dir, "good.zip");
        using (var fs = File.Create(good))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("readme.txt", CompressionLevel.NoCompression);
            using var w = new StreamWriter(e.Open());
            w.Write("压缩包内的实际内容");
        }
        Assert.True(SkillArtifactContract.Validate(Skill("zip_pack"), Output(good)).IsSatisfied);

        var empty = Path.Combine(_dir, "empty.zip");
        using (var fs = File.Create(empty))
        using (var _ = new ZipArchive(fs, ZipArchiveMode.Create)) { }
        var validation = SkillArtifactContract.Validate(Skill("zip_pack"), Output(empty));
        Assert.False(validation.IsSatisfied);
        Assert.Contains(validation.MissingItems, i => i.Contains("没有任何文件", StringComparison.Ordinal));
    }

    [Fact]
    public void Code_WithSource_Passes_AndShortOrBinaryFails()
    {
        var good = Path.Combine(_dir, "good.cs");
        File.WriteAllText(good, "public static class Skill { public static string Run(string input) => input; }");
        Assert.True(SkillArtifactContract.Validate(Skill("code_gen"), Output(good)).IsSatisfied);

        var shortFile = Path.Combine(_dir, "short.py");
        File.WriteAllText(shortFile, "print(1)");
        var shortValidation = SkillArtifactContract.Validate(Skill("code_gen"), Output(shortFile));
        Assert.False(shortValidation.IsSatisfied);
        Assert.Contains(shortValidation.MissingItems, i => i.Contains("内容过短", StringComparison.Ordinal));

        var binary = Path.Combine(_dir, "binary.ts");
        File.WriteAllBytes(binary, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F]);
        var binaryValidation = SkillArtifactContract.Validate(Skill("code_gen"), Output(binary));
        Assert.False(binaryValidation.IsSatisfied);
        Assert.Contains(binaryValidation.MissingItems, i => i.Contains("二进制", StringComparison.Ordinal));
    }

    private static byte[] Png(int width, int height)
    {
        var bytes = new List<byte>();
        bytes.AddRange([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        bytes.AddRange([0, 0, 0, 13]);
        bytes.AddRange("IHDR"u8.ToArray());
        bytes.AddRange([(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width]);
        bytes.AddRange([(byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height]);
        bytes.AddRange([8, 6, 0, 0, 0]);
        bytes.AddRange([0, 0, 0, 0]);
        bytes.AddRange(new byte[600]);
        return bytes.ToArray();
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
        Assert.Contains("质量评分", text);
        Assert.Contains("缺失项", text);
        Assert.StartsWith(missing, text);
        // 重复调用不叠加告警
        Assert.Equal(text, SkillArtifactContract.AppendWarningIfViolated(Skill("docx_report"), text));
    }
}
