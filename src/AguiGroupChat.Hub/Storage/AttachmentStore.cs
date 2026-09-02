using System.Text;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Storage;

/// <summary>
/// 附件文件存储（Hub 扩展）：文件按 att_xxx 目录存放于 data/uploads 下，
/// 由 Web 层暴露上传 / 下载端点；智能体网关可经 <see cref="TryReadTextAsync"/>
/// 读取 text 类附件文本注入模型上下文。目录结构与持久化快照同根（data/），
/// Docker 部署时随命名卷一并持久化。
/// </summary>
public sealed class AttachmentStore
{
    /// <summary>单文件大小上限（20 MB）。</summary>
    public const long MaxFileBytes = 20 * 1024 * 1024;

    /// <summary>
    /// 允许上传的扩展名白名单（大小写不敏感）：图片 + 文本 / 办公文档 / 压缩包。
    /// 明确排除可执行 / 脚本 / 内联渲染类（.html/.htm/.js/.mjs/.css/.svg 等），
    /// 防止上传后被同源内联渲染触发存储型 XSS。
    /// </summary>
    public static readonly HashSet<string> AllowedUploadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // 图片
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
        // 音频（语音消息）：无脚本 / 渲染风险，音频可安全内联播放
        ".mp3", ".wav", ".ogg", ".oga", ".m4a", ".aac", ".flac", ".opus", ".webm",
        // 文本 / 文档 / 压缩包
        ".txt", ".md", ".markdown", ".json", ".csv", ".tsv", ".log", ".yaml", ".yml", ".toml",
        ".ini", ".cfg", ".conf", ".properties", ".env", ".xml", ".pdf", ".docx", ".xlsx", ".pptx", ".zip",
    };

    /// <summary>文件扩展名是否在允许上传的白名单内（无扩展名视为不允许）。</summary>
    public static bool IsAllowedUploadExtension(string fileName)
        => AllowedUploadExtensions.Contains(Path.GetExtension(fileName ?? ""));

    /// <summary>附件目录 ID 的合法格式（防目录遍历：只允许 att_ 前缀 + 字母数字下划线连字符）。</summary>
    private static readonly System.Text.RegularExpressions.Regex AttachmentIdPattern = new(
        @"^att_[A-Za-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>文本类附件注入模型上下文的单文件截断长度。</summary>
    public const int MaxTextCharsPerFile = 12_000;

    /// <summary>文本类附件注入模型上下文的总截断长度。</summary>
    public const int MaxTextCharsTotal = 24_000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".csv", ".tsv", ".xml", ".yaml", ".yml", ".toml", ".ini",
        ".cfg", ".conf", ".properties", ".env", ".gitignore", ".log",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".py", ".cs", ".java", ".kt", ".swift", ".c", ".cpp", ".cc",
        ".h", ".hpp", ".go", ".rs", ".rb", ".php", ".sql", ".html", ".htm", ".css", ".scss", ".less",
        ".sh", ".bash", ".bat", ".cmd", ".ps1", ".psm1", ".proto", ".graphql", ".dockerfile",
    };

    /// <summary>常用办公文档扩展名（可提取文本：Office Open XML 与 PDF）。</summary>
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".pptx",
    };

    /// <summary>音频扩展名（语音消息，富媒体 5.2）：仅携带元数据供前端播放，不注入文本上下文。</summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".ogg", ".oga", ".m4a", ".aac", ".flac", ".opus", ".webm",
    };

    private readonly string _root;

    public AttachmentStore(string rootDirectory)
    {
        _root = rootDirectory;
        Directory.CreateDirectory(_root);
    }

    /// <summary>保存上传文件，返回附件元信息。</summary>
    public AttachmentInfo Save(string fileName, string contentType, Stream content, long size)
    {
        if (size > MaxFileBytes)
            throw new InvalidOperationException($"附件大小超过上限（{MaxFileBytes / 1024 / 1024} MB）");
        var safeName = SanitizeFileName(fileName);
        var id = "att_" + IdGenerator.NewId();
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, safeName);
        using (var fs = File.Create(path))
            content.CopyTo(fs);

        return new AttachmentInfo
        {
            AttachmentId = id,
            Name = safeName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            Size = new FileInfo(path).Length,
            Url = $"/ag-ui/files/{id}/{Uri.EscapeDataString(safeName)}",
            Kind = Classify(contentType, safeName),
        };
    }

    /// <summary>按附件 ID 解析存储文件路径；ID 非法或不存在返回 null。</summary>
    public string? ResolvePath(string attachmentId)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) || !AttachmentIdPattern.IsMatch(attachmentId)) return null;
        var dir = Path.Combine(_root, attachmentId);
        if (!Directory.Exists(dir)) return null;
        var file = Directory.EnumerateFiles(dir).FirstOrDefault();
        return file;
    }

    /// <summary>按附件 ID 反查完整附件元信息（用于把已发布产物挂回群消息）；不存在返回 null。</summary>
    public AttachmentInfo? GetAttachmentInfo(string attachmentId)
    {
        var path = ResolvePath(attachmentId);
        if (path is null) return null;
        var safeName = Path.GetFileName(path);
        var size = new FileInfo(path).Length;
        var contentType = GuessContentType(safeName);
        return new AttachmentInfo
        {
            AttachmentId = attachmentId,
            Name = safeName,
            ContentType = contentType,
            Size = size,
            Url = $"/ag-ui/files/{attachmentId}/{Uri.EscapeDataString(safeName)}",
            Kind = Classify(contentType, safeName),
        };
    }

    private static string GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".md" or ".txt" or ".log" or ".csv" or ".json" or ".xml" or ".yml" or ".yaml" => "text/plain; charset=utf-8",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" or ".oga" => "audio/ogg",
        ".m4a" => "audio/mp4",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        ".opus" => "audio/opus",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    /// <summary>导出：枚举全部附件（ID → 磁盘路径），供数据导出打包。</summary>
    public IReadOnlyList<(string AttachmentId, string Path)> ListAllFiles()
    {
        if (!Directory.Exists(_root)) return [];
        var list = new List<(string, string)>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(dir);
            if (!AttachmentIdPattern.IsMatch(id)) continue;
            var file = Directory.EnumerateFiles(dir).FirstOrDefault();
            if (file is not null) list.Add((id, file));
        }
        return list;
    }

    /// <summary>导入还原：按指定附件 ID 写入文件（保留 ID 以保证消息 / 头像引用不变）；ID 已存在则跳过。</summary>
    public bool RestoreFile(string attachmentId, string fileName, byte[] content)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) || !AttachmentIdPattern.IsMatch(attachmentId)) return false;
        if (content.Length > MaxFileBytes) return false;
        var dir = Path.Combine(_root, attachmentId);
        if (Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any()) return true; // 已存在：跳过（保留现有）
        Directory.CreateDirectory(dir);
        var safeName = SanitizeFileName(fileName);
        File.WriteAllBytes(Path.Combine(dir, safeName), content);
        return true;
    }

    /// <summary>清空全部附件文件（系统初始化用）。</summary>
    public void ClearAll()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(dir);
            if (AttachmentIdPattern.IsMatch(id))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* 文件占用忽略 */ }
            }
        }
    }

    /// <summary>读取附件文本（text 类直接读文件；docx/xlsx/pptx/pdf 走办公文档提取；超长截断）。</summary>
    public async Task<string?> TryReadTextAsync(string attachmentId, CancellationToken ct = default)
    {
        var path = ResolvePath(attachmentId);
        if (path is null) return null;
        var ext = Path.GetExtension(path);
        string? text = null;

        if (TextExtensions.Contains(ext))
        {
            try { text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct); }
            catch (Exception) { return null; }
        }
        else if (DocumentExtensions.Contains(ext))
        {
            text = OfficeTextExtractor.Extract(path, ext);
        }

        if (text is null) return null;
        return text.Length > MaxTextCharsPerFile ? text[..MaxTextCharsPerFile] : text;
    }

    /// <summary>读取图片附件为原始字节 + MIME（供视觉模型以 data URI 传入）。id 非法 / 文件不存在 / 非图片返回 (null, null)。</summary>
    public (byte[] Bytes, string ContentType)? TryReadImageBytes(string attachmentId)
    {
        var path = ResolvePath(attachmentId);
        if (path is null) return null;
        var ext = Path.GetExtension(path);
        var mime = ext.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => null,
        };
        if (mime is null) return null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) return null;
            return (File.ReadAllBytes(path), mime);
        }
        catch (Exception) { return null; }
    }

    /// <summary>判断附件是否可提取文本（纯文本或办公文档，供智能体上下文注入）。</summary>
    public static bool IsExtractable(AttachmentInfo attachment)
        => attachment.Kind is "text" or "document"
           || TextExtensions.Contains(Path.GetExtension(attachment.Name))
           || DocumentExtensions.Contains(Path.GetExtension(attachment.Name))
           || (attachment.ContentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>文件名消毒：去目录成分 / 非法字符，兜底默认名，限长。</summary>
    internal static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName?.Trim() ?? "");
        if (string.IsNullOrEmpty(name)) return "file";
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        if (string.IsNullOrEmpty(name)) return "file";
        return name.Length > 120 ? name[..120] : name;
    }

    /// <summary>附件类别：image（图片）/ audio（音频）/ text（可提取文本）/ document（办公文档，可提取文本）/ binary（其余）。</summary>
    internal static string Classify(string? contentType, string fileName)
    {
        if (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return "image";
        if (contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true
            || AudioExtensions.Contains(Path.GetExtension(fileName)))
            return "audio";
        if (contentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
            || TextExtensions.Contains(Path.GetExtension(fileName)))
            return "text";
        if (DocumentExtensions.Contains(Path.GetExtension(fileName)))
            return "document";
        return "binary";
    }
}
