using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;

namespace AguiGroupChat.Agents;

/// <summary>文档技能应产出的文件类型（由技能 id / 名称 / 描述推断）。</summary>
public enum SkillArtifactKind
{
    None,
    Docx,
    Xlsx,
    Pptx,
    Pdf,
}

/// <summary>单个候选产物的校验结果。</summary>
public sealed record SkillArtifactFile(
    string Path,
    string FileName,
    long Bytes,
    bool Valid,
    IReadOnlyList<string> Issues);

/// <summary>一次产物契约校验的结果。</summary>
public sealed record SkillArtifactValidation(
    SkillArtifactKind Kind,
    IReadOnlyList<string> Paths,
    IReadOnlyList<SkillArtifactFile> Files,
    IReadOnlyList<string> Issues)
{
    public bool HasExpectedArtifact => Kind != SkillArtifactKind.None;

    /// <summary>契约是否满足：非文档技能视为不适用；文档技能至少要有一个扩展名与内容都合格的产物。</summary>
    public bool IsSatisfied => !HasExpectedArtifact || Files.Any(f => f.Valid);
}

/// <summary>
/// 文档类技能的<b>产物契约</b>（1.0.341）：技能说“生成了文件”不等于用户真的拿到了可用文件。
///
/// <para>
/// 运行后除了看 <c>produce_file</c> 标记，还检查：文件存在 / 扩展名匹配 / 非空 / OOXML 或 PDF
/// 文件签名 / 必需结构（<c>word/document.xml</c>、至少一张幻灯片、至少一个工作表与单元格）/
/// 是否只有标题而没有正文。契约失败时在原结果末尾追加明确告警：文件仍尽量回档，但模型与用户
/// 都能看到“产物不完整”，不再把空壳稿子当成成功。
/// </para>
///
/// <para>
/// 同时供生成期自测使用：自动生成的文档技能会用标准样例跑一次并校验契约，失败则进入既有
/// <see cref="SkillAutoFixer"/> 的自动修复循环。
/// </para>
/// </summary>
public static class SkillArtifactContract
{
    public const string FailurePrefix = "技能产物校验失败";

    private const int MinArchiveBytes = 256;
    private const int MinPdfBytes = 512;
    private const int MinDocxTextChars = 20;
    private const int MinPptxTextChars = 20;

    /// <summary>推断技能应产出的文件类型；非文档生成器返回 <see cref="SkillArtifactKind.None"/>。</summary>
    public static SkillArtifactKind ExpectedKind(AgentSkillDefinition skill)
    {
        if (!AgentGatewayHelpers.IsDocumentGenerator(skill)) return SkillArtifactKind.None;

        // 先看 id / 名称（强信号），再看描述，避免描述里的“可导出 PDF”把 docx 技能判成 PDF。
        var idName = ((skill.SkillId ?? "") + " " + (skill.Name ?? "")).ToLowerInvariant();
        if (Contains(idName, "docx", "word 文档", "word文档", "word doc")) return SkillArtifactKind.Docx;
        if (Contains(idName, "xlsx", "excel", "工作簿", "电子表格")) return SkillArtifactKind.Xlsx;
        if (Contains(idName, "pptx", "ppt", "演示文稿", "幻灯片")) return SkillArtifactKind.Pptx;
        if (Contains(idName, "pdf")) return SkillArtifactKind.Pdf;

        // 刻意不看描述：很多技能描述里会顺带写“可导出 PDF”，按描述匹配会把普通技能误判成 PDF 生成器。
        // 文档技能的 id / 名称是强信号（docx_ / xlsx_ / pptx_ / pdf_ / md_to_docx 等）。
        return SkillArtifactKind.None;
    }

    public static string Extension(SkillArtifactKind kind) => kind switch
    {
        SkillArtifactKind.Docx => ".docx",
        SkillArtifactKind.Xlsx => ".xlsx",
        SkillArtifactKind.Pptx => ".pptx",
        SkillArtifactKind.Pdf => ".pdf",
        _ => "",
    };

    public static string DisplayName(SkillArtifactKind kind) => kind switch
    {
        SkillArtifactKind.Docx => "Word 文档",
        SkillArtifactKind.Xlsx => "Excel 工作簿",
        SkillArtifactKind.Pptx => "演示文稿",
        SkillArtifactKind.Pdf => "PDF 文档",
        _ => "文件",
    };

    /// <summary>校验技能返回文本里的产物标记。非文档技能返回“不适用”（满足）。</summary>
    public static SkillArtifactValidation Validate(AgentSkillDefinition skill, string? output)
    {
        var kind = ExpectedKind(skill);
        if (kind == SkillArtifactKind.None)
            return new SkillArtifactValidation(kind, [], [], []);

        var paths = ProducedFileMarker.ExtractPaths(output);
        var expectedExt = Extension(kind);
        var matching = paths.Where(p => string.Equals(Path.GetExtension(p), expectedExt, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var issues = new List<string>();

        if (paths.Count == 0)
            issues.Add($"返回里没有 produce_file 标记，用户拿不到可下载的 {DisplayName(kind)}");
        else if (matching.Count == 0)
            issues.Add($"没有找到 {expectedExt} 产物（实际声明：{string.Join(" / ", paths.Select(Path.GetFileName))}）");

        var files = matching.Select(p => ValidateFile(p, kind)).ToList();
        if (matching.Count > 0 && !files.Any(f => f.Valid))
            issues.AddRange(files.SelectMany(f => f.Issues).Distinct(StringComparer.Ordinal));

        return new SkillArtifactValidation(kind, paths, files, issues);
    }

    /// <summary>契约失败时在原结果后追加告警；通过 / 非文档技能原样返回。</summary>
    public static string AppendWarningIfViolated(AgentSkillDefinition skill, string? output)
    {
        var text = output ?? "";
        var validation = Validate(skill, text);
        if (!validation.HasExpectedArtifact || validation.IsSatisfied) return text;
        if (text.Contains(FailurePrefix, StringComparison.Ordinal)) return text;

        var detail = validation.Issues.Count > 0
            ? string.Join("；", validation.Issues)
            : "产物存在但没有通过内容校验";
        return text + "\n\n⚠ " + FailurePrefix + "：" + detail
            + "。文件仍会尽量回档，但请补足正文或修正技能后重试，不要把空壳产物当作成功交付。";
    }

    /// <summary>自动修复自测用的标准样例输入（覆盖标题 + 正文 / 多页 / 多表）。</summary>
    public static string SampleInput(SkillArtifactKind kind) => kind switch
    {
        SkillArtifactKind.Docx =>
            """{"title":"技能产物自测","sections":[{"heading":"一、自测说明","level":1},{"paragraph":"这是一段用于验证文档技能的正文内容，必须真实写入产物，而不是只生成标题。"},{"paragraph":"第二段用于确认正文块不止一个，并让产物具备可检查的文字内容。"}]}""",
        SkillArtifactKind.Xlsx =>
            """{"title":"技能产物自测","sheets":[{"name":"自测","headers":["项目","数值"],"rows":[["甲",1],["乙",2]]}]}""",
        SkillArtifactKind.Pptx =>
            """{"title":"技能产物自测","slides":[{"type":"title","title":"技能产物自测"},{"type":"bullets","title":"自测要点","items":["第一项内容","第二项内容","第三项内容"]}]}""",
        SkillArtifactKind.Pdf =>
            """{"title":"技能产物自测","blocks":[{"type":"heading","text":"一、自测说明"},{"type":"paragraph","text":"这是用于验证 PDF 技能的正文内容，确保产物不是只有封面。"}]}""",
        _ => "",
    };

    private static SkillArtifactFile ValidateFile(string path, SkillArtifactKind kind)
    {
        var fileName = Path.GetFileName(path);
        var issues = new List<string>();
        try
        {
            if (!File.Exists(path))
                return new SkillArtifactFile(path, fileName, 0, false, [$"产物文件不存在：{fileName}"]);
            var fi = new FileInfo(path);
            if (fi.Length <= 0)
                return new SkillArtifactFile(path, fileName, 0, false, [$"产物是空文件：{fileName}"]);

            switch (kind)
            {
                case SkillArtifactKind.Docx:
                case SkillArtifactKind.Xlsx:
                case SkillArtifactKind.Pptx:
                    ValidateOfficeOpenXml(path, fileName, kind, fi.Length, issues);
                    break;
                case SkillArtifactKind.Pdf:
                    ValidatePdf(path, fileName, fi.Length, issues);
                    break;
            }

            return new SkillArtifactFile(path, fileName, fi.Length, issues.Count == 0, issues);
        }
        catch (Exception ex)
        {
            issues.Add($"产物校验异常（{fileName}）：{ex.Message}");
            return new SkillArtifactFile(path, fileName, 0, false, issues);
        }
    }

    private static void ValidateOfficeOpenXml(
        string path, string fileName, SkillArtifactKind kind, long bytes, List<string> issues)
    {
        if (bytes < MinArchiveBytes) issues.Add($"产物过小，疑似空壳：{fileName}（{bytes} 字节）");

        using var fs = File.OpenRead(path);
        Span<byte> sig = stackalloc byte[4];
        if (fs.Read(sig) < 4 || sig[0] != 'P' || sig[1] != 'K')
        {
            issues.Add($"不是有效的 Office 文件（缺少 ZIP 签名）：{fileName}");
            return;
        }
        fs.Position = 0;
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        var entries = zip.Entries
            .GroupBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        switch (kind)
        {
            case SkillArtifactKind.Docx:
            {
                if (!entries.TryGetValue("word/document.xml", out var docEntry))
                {
                    issues.Add($"Word 文档缺少正文部件 word/document.xml：{fileName}");
                    return;
                }
                var textLength = ReadXmlTextLength(docEntry);
                if (textLength < MinDocxTextChars)
                    issues.Add($"Word 文档只有标题或正文过短（非空白字符 {textLength} < {MinDocxTextChars}）：{fileName}");
                break;
            }
            case SkillArtifactKind.Pptx:
            {
                var slideEntries = entries.Where(e =>
                        e.Key.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                        && e.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Value).ToList();
                if (slideEntries.Count == 0)
                {
                    issues.Add($"演示文稿没有任何幻灯片部件：{fileName}");
                    return;
                }
                var textLength = slideEntries.Sum(ReadXmlTextLength);
                if (textLength < MinPptxTextChars)
                    issues.Add($"演示文稿只有标题或正文过短（非空白字符 {textLength} < {MinPptxTextChars}）：{fileName}");
                break;
            }
            case SkillArtifactKind.Xlsx:
            {
                if (!entries.TryGetValue("xl/workbook.xml", out _))
                {
                    issues.Add($"Excel 工作簿缺少 xl/workbook.xml：{fileName}");
                    return;
                }
                var sheetEntries = entries.Where(e =>
                        e.Key.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Value).ToList();
                if (sheetEntries.Count == 0)
                {
                    issues.Add($"Excel 工作簿没有任何工作表部件：{fileName}");
                    return;
                }
                var cells = 0;
                foreach (var sheet in sheetEntries)
                {
                    using var reader = new StreamReader(sheet.Open());
                    var xml = reader.ReadToEnd();
                    cells += Regex.Matches(xml, "<c\\b", RegexOptions.IgnoreCase).Count;
                }
                if (cells == 0)
                    issues.Add($"Excel 工作簿没有任何单元格内容：{fileName}");
                break;
            }
        }
    }

    private static void ValidatePdf(string path, string fileName, long bytes, List<string> issues)
    {
        if (bytes < MinPdfBytes) issues.Add($"PDF 过小，疑似空壳：{fileName}（{bytes} 字节）");
        using var fs = File.OpenRead(path);
        var take = (int)Math.Min(bytes, 2_000_000);
        var buffer = new byte[take];
        var read = fs.Read(buffer, 0, take);
        var head = System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Min(read, 8));
        if (!head.StartsWith("%PDF-", StringComparison.Ordinal))
            issues.Add($"不是有效的 PDF（缺少 %PDF 头）：{fileName}");

        // 扫描前 2MB 找页面对象；没有页面对象通常意味着只有封面 / 空文件。
        var body = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
        var pages = Regex.Matches(body, @"/Type\s*/Page(?!s)", RegexOptions.IgnoreCase).Count;
        if (pages == 0)
            issues.Add($"PDF 没有任何页面对象：{fileName}");
    }

    private static int ReadXmlTextLength(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        var xml = reader.ReadToEnd();
        var text = Regex.Replace(xml, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", "").Length;
    }

    private static bool Contains(string text, params string[] keys)
        => keys.Any(k => text.Contains(k, StringComparison.Ordinal));
}
