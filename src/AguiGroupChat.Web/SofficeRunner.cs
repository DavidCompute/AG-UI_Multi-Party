using System.Diagnostics;
using System.Text;
using AguiGroupChat.Hub.Infra;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>一次转换的结果：成功 / 失败 + 诊断信息；<see cref="Unavailable"/> 表示“服务端根本没装转换器”（区别于“这份文档转不出来”）。</summary>
public sealed record SofficeResult(bool Ok, string Detail, bool Unavailable = false);

/// <summary>把办公文档转成 PDF 的执行器（抽成接口：单测注入假实现，不依赖真机是否装了 LibreOffice）。</summary>
public interface ISofficeRunner
{
    Task<SofficeResult> ConvertToPdfAsync(string sourcePath, string outDir, CancellationToken ct = default);
}

/// <summary>
/// LibreOffice（headless）转换执行器：<c>soffice --headless --convert-to pdf --outdir &lt;dir&gt; &lt;src&gt;</c>。
///
/// <para>
/// 为什么每次调用都用一次性 profile（<c>-env:UserInstallation</c>）：LibreOffice 会在 profile 里写锁，
/// 进程被超时强杀后锁会残留，导致**之后所有**转换都失败。实测复用 profile 只快约 1 秒
/// （8.8s → 7.5s，4.3MB / 19 页 PPT），不值得拿稳定性去换。
/// </para>
///
/// <para>
/// 为什么必须杀进程树：soffice 会 fork 出 <c>soffice.bin</c> 常驻进程，只杀父进程会留下孤儿占着 CPU；
/// 复杂文档 + 缺字体时它确实可能长时间不返回（本项目的容器就装了中文/PDF 字体，见 Dockerfile）。
/// </para>
/// </summary>
public sealed class ProcessSofficeRunner : ISofficeRunner
{
    /// <summary>单次转换超时（实测一份 19 页带图 PPT 约 9 秒，留足余量给大文档/冷启动）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly string? _sofficePath;
    private readonly string _profileRoot;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    /// <param name="sofficePath">soffice 可执行文件路径；null / 空则自动探测。</param>
    /// <param name="profileRoot">一次性 profile 的落盘父目录（随缓存目录走）。</param>
    public ProcessSofficeRunner(string? sofficePath, string profileRoot, ILogger logger,
        TimeSpan? timeout = null)
    {
        _sofficePath = string.IsNullOrWhiteSpace(sofficePath) ? DetectSoffice() : sofficePath;
        _profileRoot = profileRoot;
        _timeout = timeout ?? DefaultTimeout;
        _logger = logger;
    }

    /// <summary>是否探测到了 LibreOffice（供健康检查 / 诊断用，不触发转换）。</summary>
    public bool Available => _sofficePath is not null;

    /// <summary>转换器位置（探测顺序：环境变量 AGUI_SOFFICE → 常见安装路径）。找不到返回 null。</summary>
    public static string? DetectSoffice()
    {
        var configured = Environment.GetEnvironmentVariable("AGUI_SOFFICE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        string[] candidates =
        [
            // Debian / Ubuntu 容器（本项目 Dockerfile 即此形态）
            "/usr/bin/soffice",
            "/usr/local/bin/soffice",
            "/usr/lib/libreoffice/program/soffice",
            "/opt/libreoffice/program/soffice",
            "/snap/bin/libreoffice",
            // Windows（桌面版 / 开发者本机装了 LibreOffice）
            @"C:\Program Files\LibreOffice\program\soffice.exe",
            @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        ];
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    public async Task<SofficeResult> ConvertToPdfAsync(string sourcePath, string outDir,
        CancellationToken ct = default)
    {
        if (_sofficePath is null)
            return new SofficeResult(false,
                "服务端未安装文档转换组件（LibreOffice），无法在线转换；请下载后用本地应用打开",
                Unavailable: true);

        // 一次性 profile：转换结束（含被超时杀掉）后整目录删除，不给后续调用留锁
        var profileDir = Path.Combine(_profileRoot, "profile-" + IdGenerator.NewId());
        Directory.CreateDirectory(profileDir);
        try
        {
            var psi = new ProcessStartInfo(_sofficePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--nologo");
            psi.ArgumentList.Add("--nodefault");
            psi.ArgumentList.Add("--norestore");
            psi.ArgumentList.Add("--nolockcheck");
            // 注意：必须是单个参数（含 = 与 file:// URI），拼成两个参数会失效
            psi.ArgumentList.Add("-env:UserInstallation=" + new Uri(profileDir).AbsoluteUri);
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add("pdf");
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(outDir);
            psi.ArgumentList.Add(sourcePath);

            using var proc = Process.Start(psi);
            if (proc is null)
                return new SofficeResult(false, "无法启动文档转换组件");

            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(proc);
                if (ct.IsCancellationRequested) throw;
                return new SofficeResult(false,
                    $"文档转换超时（超过 {(int)_timeout.TotalSeconds} 秒），请下载后用本地应用打开");
            }

            var output = await SafeTextAsync(stdout).ConfigureAwait(false);
            var error = await SafeTextAsync(stderr).ConfigureAwait(false);
            if (proc.ExitCode == 0)
                return new SofficeResult(true, output.Trim());

            _logger.LogWarning("soffice 转换失败（退出码 {Code}）：{Error}", proc.ExitCode, error.Trim());
            var detail = LastMeaningfulLine(error) ?? LastMeaningfulLine(output) ?? "未知错误";
            return new SofficeResult(false, $"文档转换失败：{detail}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "soffice 转换异常：{Source}", sourcePath);
            return new SofficeResult(false, $"文档转换异常：{ex.Message}");
        }
        finally
        {
            try { Directory.Delete(profileDir, recursive: true); } catch { /* 清理失败忽略 */ }
        }
    }

    private static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* 已退出 / 无权限：忽略 */ }
    }

    private static async Task<string> SafeTextAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return ""; }
    }

    /// <summary>取最后一行有信息量的输出（soffice 的报错混在 javaldx 之类的告警后面，取首行会拿到无用噪音）。</summary>
    private static string? LastMeaningfulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("javaldx", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (lines.Count == 0) return null;
        var line = lines[^1];
        var sb = new StringBuilder(line.Length);
        foreach (var ch in line)
            if (!char.IsControl(ch)) sb.Append(ch);   // 去掉控制字符，避免把乱码塞进前端提示
        var clean = sb.ToString().Trim();
        return clean.Length == 0 ? null : (clean.Length > 300 ? clean[..300] : clean);
    }
}
