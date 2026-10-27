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
    Image,
    Archive,
    Code,
}

/// <summary>单个候选产物的校验结果。</summary>
public sealed record SkillArtifactFile(
    string Path,
    string FileName,
    long Bytes,
    bool Valid,
    IReadOnlyList<string> Issues)
{
    /// <summary>单个产物的质量分（0–100）；合格 100，缺失 / 空壳 / 结构错误按严重程度扣分。</summary>
    public int Score
    {
        get
        {
            if (Valid) return 100;
            var score = 100;
            foreach (var issue in Issues)
            {
                if (issue.Contains("不存在", StringComparison.Ordinal) || issue.Contains("空文件", StringComparison.Ordinal)) score -= 60;
                else if (issue.Contains("不是有效", StringComparison.Ordinal) || issue.Contains("缺少", StringComparison.Ordinal)) score -= 40;
                else if (issue.Contains("过小", StringComparison.Ordinal)
                         || issue.Contains("过短", StringComparison.Ordinal)
                         || issue.Contains("只有标题", StringComparison.Ordinal)
                         || issue.Contains("没有任何", StringComparison.Ordinal)) score -= 30;
                else score -= 20;
            }
            return Math.Clamp(score, 0, 100);
        }
    }
}

/// <summary>一次产物契约校验的结果。</summary>
public sealed record SkillArtifactValidation(
    SkillArtifactKind Kind,
    IReadOnlyList<string> Paths,
    IReadOnlyList<SkillArtifactFile> Files,
    IReadOnlyList<string> Issues)
{
    public bool HasExpectedArtifact => Kind != SkillArtifactKind.None;

    /// <summary>契约是否满足：非产物技能视为不适用；产物技能至少要有一个扩展名与内容都合格的产物。</summary>
    public bool IsSatisfied => !HasExpectedArtifact || Files.Any(f => f.Valid);

    /// <summary>整体质量分（0–100）：取合格候选中的最高分；没有任何合格候选时取最好候选分。</summary>
    public int Score
    {
        get
        {
            if (!HasExpectedArtifact) return 100;
            if (Paths.Count == 0) return 0;
            if (Files.Count == 0) return 20;
            return Files.Max(f => f.Score);
        }
    }

    /// <summary>缺失项清单（与 Issues 同源，便于前端 / 日志按“缺什么”展示）。</summary>
    public IReadOnlyList<string> MissingItems => Issues;
}

/// <summary>
/// 技能产物契约（1.0.341 文档；1.0.342 图片 / 压缩包 / 代码）：技能说“生成了文件”不等于用户真的拿到了可用文件。
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

    private const int MinArchiveBytes = 128; // 单文件小压缩包也可能只有 ~150 字节；空包（无条目）另有结构校验
    private const int MinPdfBytes = 512;
    private const int MinImageBytes = 512;
    private const int MinCodeBytes = 40;
    private const int MinCodeChars = 40;
    private const int MinImageEdge = 32;
    private const int MinDocxTextChars = 20;
    private const int MinPptxTextChars = 20;

    private static readonly string[] GenerationVerbs =
        ["gen", "generate", "create", "export", "make", "build", "pack", "compress", "write", "writer", "生成", "创建", "导出", "制作", "打包", "压缩", "写出"];

    /// <summary>推断技能应产出的文件类型；没有强信号或没有生成动词时返回 None。</summary>
    public static SkillArtifactKind ExpectedKind(AgentSkillDefinition skill)
    {
        if (skill.Kind == AgentSkillKind.Prompt) return SkillArtifactKind.None;

        // 只看 id / 名称：描述里经常顺带写“可导出 PDF / 可生成图片”，按描述匹配会误判普通读写技能。
        var idName = ((skill.SkillId ?? "") + " " + (skill.Name ?? "")).ToLowerInvariant();
        if (Contains(idName, "docx", "word 文档", "word文档", "word doc")) return SkillArtifactKind.Docx;
        if (Contains(idName, "xlsx", "excel", "工作簿", "电子表格")) return SkillArtifactKind.Xlsx;
        if (Contains(idName, "pptx", "ppt", "演示文稿", "幻灯片")) return SkillArtifactKind.Pptx;
        if (Contains(idName, "pdf")) return SkillArtifactKind.Pdf;

        var hasGen = Contains(idName, GenerationVerbs);
        if (hasGen && Contains(idName, "image", "img", "图片", "图像", "海报", "插图", "照片", "封面图"))
            return SkillArtifactKind.Image;
        if (hasGen && Contains(idName, "zip", "archive", "压缩包", "打包"))
            return SkillArtifactKind.Archive;
        if (hasGen && Contains(idName, "code", "source", "源码", "代码"))
            return SkillArtifactKind.Code;
        return SkillArtifactKind.None;
    }

    /// <summary>是否支持生成期“标准样例真实跑一次”的产物自测（目前仅文档四类；其余类型保持运行期校验）。</summary>
    public static bool SupportsGenerationProbe(SkillArtifactKind kind)
        => kind is SkillArtifactKind.Docx or SkillArtifactKind.Xlsx or SkillArtifactKind.Pptx or SkillArtifactKind.Pdf;

    public static IReadOnlyList<string> Extensions(SkillArtifactKind kind) => kind switch
    {
        SkillArtifactKind.Docx => [".docx"],
        SkillArtifactKind.Xlsx => [".xlsx"],
        SkillArtifactKind.Pptx => [".pptx"],
        SkillArtifactKind.Pdf => [".pdf"],
        SkillArtifactKind.Image => [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"],
        SkillArtifactKind.Archive => [".zip"],
        SkillArtifactKind.Code => [".cs", ".py", ".java", ".go", ".rs", ".ts", ".sql", ".c", ".h", ".cpp", ".hpp", ".vb"],
        _ => [],
    };

    public static string Extension(SkillArtifactKind kind)
    {
        var extensions = Extensions(kind);
        return extensions.Count > 0 ? extensions[0] : "";
    }

    public static string DisplayName(SkillArtifactKind kind) => kind switch
    {
        SkillArtifactKind.Docx => "Word 文档",
        SkillArtifactKind.Xlsx => "Excel 工作簿",
        SkillArtifactKind.Pptx => "演示文稿",
        SkillArtifactKind.Pdf => "PDF 文档",
        SkillArtifactKind.Image => "图片",
        SkillArtifactKind.Archive => "压缩包",
        SkillArtifactKind.Code => "代码文件",
        _ => "文件",
    };

    /// <summary>校验技能返回文本里的产物标记。非文档技能返回“不适用”（满足）。</summary>
    public static SkillArtifactValidation Validate(AgentSkillDefinition skill, string? output)
    {
        var kind = ExpectedKind(skill);
        if (kind == SkillArtifactKind.None)
            return new SkillArtifactValidation(kind, [], [], []);

        var paths = ProducedFileMarker.ExtractPaths(output);
        var expectedExts = Extensions(kind);
        var matching = paths.Where(p => expectedExts.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToList();
        var issues = new List<string>();

        if (paths.Count == 0)
            issues.Add($"返回里没有 produce_file 标记，用户拿不到可下载的 {DisplayName(kind)}");
        else if (matching.Count == 0)
            issues.Add($"没有找到 {string.Join("/", expectedExts)} 产物（实际声明：{string.Join(" / ", paths.Select(Path.GetFileName))}）");

        var files = matching.Select(p => ValidateFile(p, kind)).ToList();
        if (matching.Count > 0 && !files.Any(f => f.Valid))
            issues.AddRange(files.SelectMany(f => f.Issues).Distinct(StringComparer.Ordinal));

        return new SkillArtifactValidation(kind, paths, files, issues);
    }

    /// <summary>契约失败时在原结果后追加告警；通过 / 非文档技能原样返回。</summary>
    public static string AppendWarningIfViolated(AgentSkillDefinition skill, string? output)
        => AppendWarning((output ?? ""), Validate(skill, output));

    /// <summary>用已算好的校验结果追加告警（避免调用方重复解析文件）。</summary>
    public static string AppendWarning(string? output, SkillArtifactValidation validation)
    {
        var text = output ?? "";
        if (!validation.HasExpectedArtifact || validation.IsSatisfied) return text;
        if (text.Contains(FailurePrefix, StringComparison.Ordinal)) return text;

        var detail = validation.MissingItems.Count > 0
            ? string.Join("；", validation.MissingItems)
            : "产物存在但没有通过内容校验";
        return text + "\n\n⚠ " + FailurePrefix + $"（质量评分 {validation.Score}/100）：缺失项：" + detail
            + "。文件仍会尽量回档，但请补足内容或修正技能后重试，不要把空壳产物当作成功交付。";
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
        _ => "", // 图片 / 压缩包 / 代码没有统一输入契约，不做生成期真实跑测
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
                case SkillArtifactKind.Image:
                    ValidateImage(path, fileName, fi.Length, issues);
                    break;
                case SkillArtifactKind.Archive:
                    ValidateArchive(path, fileName, fi.Length, issues);
                    break;
                case SkillArtifactKind.Code:
                    ValidateCode(path, fileName, fi.Length, issues);
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

    private static void ValidateImage(string path, string fileName, long bytes, List<string> issues)
    {
        if (bytes < MinImageBytes) issues.Add($"图片过小，疑似空壳：{fileName}（{bytes} 字节）");
        using var fs = File.OpenRead(path);
        var take = (int)Math.Min(bytes, 256 * 1024);
        var buffer = new byte[take];
        var read = fs.Read(buffer, 0, take);
        if (read < 12)
        {
            issues.Add($"图片文件头不完整：{fileName}");
            return;
        }

        var isPng = buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47;
        var isJpeg = buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF;
        var isGif = buffer[0] == 'G' && buffer[1] == 'I' && buffer[2] == 'F';
        var isBmp = buffer[0] == 'B' && buffer[1] == 'M';
        var isWebp = buffer[0] == 'R' && buffer[1] == 'I' && buffer[2] == 'F' && buffer[3] == 'F'
            && read >= 12 && buffer[8] == 'W' && buffer[9] == 'E' && buffer[10] == 'B' && buffer[11] == 'P';
        if (!isPng && !isJpeg && !isGif && !isBmp && !isWebp)
        {
            issues.Add($"不是有效图片（缺少 PNG / JPEG / GIF / WEBP / BMP 签名）：{fileName}");
            return;
        }

        // WebP 尺寸解析不在无依赖探针覆盖范围内：只做签名与大小检查，不误报。
        if (!isWebp)
        {
            if (!ImageSizeProbe.TryRead(buffer, out var width, out var height))
                issues.Add($"无法读取图片尺寸：{fileName}");
            else if (width < MinImageEdge || height < MinImageEdge)
                issues.Add($"图片尺寸过小（{width}×{height}）：{fileName}");
        }
    }

    private static void ValidateArchive(string path, string fileName, long bytes, List<string> issues)
    {
        if (bytes < MinArchiveBytes) issues.Add($"压缩包过小，疑似空壳：{fileName}（{bytes} 字节）");
        using var fs = File.OpenRead(path);
        Span<byte> sig = stackalloc byte[4];
        if (fs.Read(sig) < 4 || sig[0] != 'P' || sig[1] != 'K')
        {
            issues.Add($"不是有效的 ZIP 压缩包：{fileName}");
            return;
        }
        fs.Position = 0;
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
        if (zip.Entries.Count == 0)
        {
            issues.Add($"压缩包内没有任何文件：{fileName}");
            return;
        }
        if (zip.Entries.All(e => e.Length == 0))
            issues.Add($"压缩包内全部是空文件：{fileName}");
    }

    private static void ValidateCode(string path, string fileName, long bytes, List<string> issues)
    {
        if (bytes < MinCodeBytes) issues.Add($"代码文件过小，疑似空壳：{fileName}（{bytes} 字节）");
        using var fs = File.OpenRead(path);
        var take = (int)Math.Min(bytes, 256 * 1024);
        var buffer = new byte[take];
        var read = fs.Read(buffer, 0, take);
        // 代码应是文本源码；出现 NUL 说明其实是二进制（或扩展名伪装）。
        if (buffer.AsSpan(0, read).Contains((byte)0))
        {
            issues.Add($"代码产物含二进制内容：{fileName}");
            return;
        }
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        var meaningful = Regex.Replace(text, @"\s+", "").Length;
        if (meaningful < MinCodeChars)
            issues.Add($"代码内容过短（非空白字符 {meaningful} < {MinCodeChars}）：{fileName}");
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
