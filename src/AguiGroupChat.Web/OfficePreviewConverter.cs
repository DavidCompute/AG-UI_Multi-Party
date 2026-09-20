using AguiGroupChat.Hub.Infra;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>预览失败的类别（端点据此选 HTTP 状态码，前端据此给不同的提示语）。</summary>
public enum PreviewFailure
{
    None = 0,

    /// <summary>扩展名不支持在线预览（如 .zip / .txt / .bin）。</summary>
    Unsupported,

    /// <summary>源文件不存在或已被清理。</summary>
    MissingSource,

    /// <summary>服务端缺少转换器（LibreOffice 未安装）。</summary>
    Unavailable,

    /// <summary>转换器执行失败 / 超时。</summary>
    ConversionFailed,
}

/// <summary>预览结果：成功给出可内联渲染的 PDF 绝对路径（可能就是源文件本身，如 .pdf）；失败给出类别与可读原因。</summary>
public sealed record PreviewOutcome(string? PdfPath, PreviewFailure Failure, string Message)
{
    public bool Ok => PdfPath is not null;

    public static PreviewOutcome Success(string pdfPath) => new(pdfPath, PreviewFailure.None, "");

    public static PreviewOutcome Fail(PreviewFailure failure, string message) => new(null, failure, message);
}

/// <summary>
/// 办公文档「在线查看」：把浏览器不能直接渲染的文档（docx / xlsx / pptx …）取出可内联渲染的 PDF。
/// 抽成接口是为了让 HTTP 端点能在单测里注入假转换器——真实转换依赖服务端的 LibreOffice，
/// 不该让端点的权限 / 状态码测试跟着它一起挂。
/// </summary>
public interface IDocPreviewConverter
{
    /// <summary>取该附件的可内联 PDF：命中缓存直接返回，否则转换并缓存。</summary>
    Task<PreviewOutcome> GetOrCreateAsync(string attachmentId, string sourcePath, CancellationToken ct = default);
}

/// <summary>
/// 办公文档「在线查看」转换器：用 LibreOffice（headless）把 docx / xlsx / pptx 转成 PDF 并缓存，
/// 前端在弹窗 iframe 里内联渲染。
///
/// <para>
/// 为什么是「服务端转 PDF」而不是前端 JS 渲染库：PPT 的版式 / 图表 / 中文字体在纯前端方案里
/// 还原度很差（PPTXjs 之类基本看不了），而 LibreOffice 是**另一套成熟渲染器**，
/// docx / xlsx / pptx 三种格式共用同一条通路，前端只需要一个浏览器自带的 PDF 阅读器。
/// </para>
///
/// <para>
/// 关键约束（都是实测踩出来的）：
/// ① <b>串行化</b>：LibreOffice 并发跑会互相踩（实测），所以全程一把 <see cref="SemaphoreSlim"/>；
/// ② <b>每次转换独立 profile</b>：复用 profile 只快约 1 秒（实测 8.8s vs 7.5s，4.3MB/19 页 PPT），
///    但进程被超时杀掉后会留下脏 profile 让**后续所有**转换失败——用一次性 profile 换健壮性；
/// ③ <b>必须缓存</b>：首次转换要好几秒，同一份文件每次预览都重转是不可接受的；
///    缓存键 = 附件 ID + 源文件指纹（长度 + mtime），替换过文件会自动失效。
/// </para>
/// </summary>
public sealed class OfficePreviewConverter : IDocPreviewConverter, IDisposable
{
    /// <summary>可在线预览的扩展名。含 .pdf：它本身就能内联渲染，走同一条前端通路（免转换）。</summary>
    private static readonly HashSet<string> PreviewableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt",
        // ODF / RTF：LibreOffice 原生格式，顺带支持（用户可能上传 WPS / LibreOffice 导出的文件）
        ".odt", ".ods", ".odp", ".rtf",
    };

    /// <summary>缓存默认保留期：超过则在下一次转换时被顺带清理（预览是“看过就算”的派生数据，不留太久）。</summary>
    public static readonly TimeSpan DefaultCacheRetention = TimeSpan.FromDays(7);

    /// <summary>清理扫描的最小间隔（避免每次预览都去遍历缓存目录）。</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    /// <summary>转换产物的缓存目录名（挂 data 卷，容器重建不丢；丢了也只是重转一次）。</summary>
    public const string CacheDirectoryName = "preview";

    private readonly string _cacheRoot;
    private readonly ISofficeRunner _runner;
    private readonly TimeSpan _retention;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _pruneLock = new();
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public OfficePreviewConverter(string cacheRoot, ISofficeRunner runner, ILogger logger,
        TimeSpan? retention = null)
    {
        _cacheRoot = cacheRoot;
        _runner = runner;
        _logger = logger;
        _retention = retention ?? DefaultCacheRetention;
    }

    /// <summary>该文件是否支持在线预览（按扩展名判定）。</summary>
    public static bool IsPreviewable(string fileName)
        => PreviewableExtensions.Contains(Path.GetExtension(fileName ?? ""));

    public async Task<PreviewOutcome> GetOrCreateAsync(string attachmentId, string sourcePath,
        CancellationToken ct = default)
    {
        if (!IsPreviewable(sourcePath))
            return PreviewOutcome.Fail(PreviewFailure.Unsupported,
                $"该文件类型不支持在线预览（{Path.GetExtension(sourcePath)}），请下载后用本地应用打开");

        // PDF 本身就是浏览器能内联渲染的格式：不必转换，直接回源文件
        if (string.Equals(Path.GetExtension(sourcePath), ".pdf", StringComparison.OrdinalIgnoreCase))
            return File.Exists(sourcePath)
                ? PreviewOutcome.Success(sourcePath)
                : PreviewOutcome.Fail(PreviewFailure.MissingSource, "源文件不存在或已删除");

        var source = new FileInfo(sourcePath);
        if (!source.Exists)
            return PreviewOutcome.Fail(PreviewFailure.MissingSource, "源文件不存在或已删除");

        var pdfPath = Path.Combine(_cacheRoot, SafeCacheKey(attachmentId) + ".pdf");
        var stampPath = pdfPath + ".stamp";
        var stamp = $"{source.Length}:{source.LastWriteTimeUtc.Ticks}";

        if (IsFreshCache(pdfPath, stampPath, stamp))
            return PreviewOutcome.Success(pdfPath);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PruneIfDue();

            // 二次确认：等锁期间可能已被并发的另一个请求转好了（同一份文件常被连续点开）
            if (IsFreshCache(pdfPath, stampPath, stamp))
                return PreviewOutcome.Success(pdfPath);

            Directory.CreateDirectory(_cacheRoot);
            // 转换落到独立临时目录再搬进来：转换中途失败 / 被打断时不会留下半截的“缓存”
            var tmpDir = Path.Combine(_cacheRoot, "tmp-" + IdGenerator.NewId());
            Directory.CreateDirectory(tmpDir);
            try
            {
                var result = await _runner.ConvertToPdfAsync(sourcePath, tmpDir, ct).ConfigureAwait(false);
                if (!result.Ok)
                {
                    _logger.LogWarning("在线预览转换失败：{Source}（{Detail}）", sourcePath, result.Detail);
                    return PreviewOutcome.Fail(result.Unavailable ? PreviewFailure.Unavailable
                        : PreviewFailure.ConversionFailed, result.Detail);
                }

                var produced = Directory.EnumerateFiles(tmpDir, "*.pdf").FirstOrDefault();
                if (produced is null)
                    return PreviewOutcome.Fail(PreviewFailure.ConversionFailed,
                        "转换未产出 PDF（文档可能已损坏或含不支持的内容）");

                File.Move(produced, pdfPath, overwrite: true);
                await File.WriteAllTextAsync(stampPath, stamp, ct).ConfigureAwait(false);
                return PreviewOutcome.Success(pdfPath);
            }
            finally
            {
                try { Directory.Delete(tmpDir, recursive: true); } catch { /* 清理失败不影响结果 */ }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>清空全部预览缓存（系统初始化「清空一切」用）。</summary>
    public void ClearAll()
    {
        if (!Directory.Exists(_cacheRoot)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(_cacheRoot))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch { /* 文件占用忽略 */ }
        }
    }

    private static bool IsFreshCache(string pdfPath, string stampPath, string stamp)
        => File.Exists(pdfPath) && File.Exists(stampPath) && ReadStamp(stampPath) == stamp;

    private static string? ReadStamp(string stampPath)
    {
        try { return File.ReadAllText(stampPath).Trim(); }
        catch { return null; }
    }

    /// <summary>缓存文件名：只保留安全字符（附件 ID 另有格式约束，但这里不依赖调用方已校验）。</summary>
    private static string SafeCacheKey(string attachmentId)
    {
        var key = new string((attachmentId ?? "").Where(char.IsLetterOrDigit).ToArray());
        return key.Length == 0 ? "attachment" : key;
    }

    /// <summary>顺带清理过期缓存与崩死留下的临时目录；最多每小时扫一次。</summary>
    private void PruneIfDue()
    {
        lock (_pruneLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastPruneUtc < PruneInterval) return;
            _lastPruneUtc = now;
        }
        try
        {
            var cutoff = DateTime.UtcNow - _retention;
            foreach (var file in Directory.EnumerateFiles(_cacheRoot))
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                // 只碰自己的派生数据：.stamp 是元数据，.pdf 是产物，两者同生命周期
                if (!file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".stamp", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch { /* 忽略 */ }
            }
            foreach (var dir in Directory.EnumerateDirectories(_cacheRoot, "tmp-*"))
            {
                if (Directory.GetLastWriteTimeUtc(dir) >= cutoff) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* 忽略 */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "预览缓存清理跳过");
        }
    }

    public void Dispose() => _gate.Dispose();
}
