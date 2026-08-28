using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AguiGroupChat.NativeBridge;

/// <summary>
/// 本机 shell 命令执行器：在浏览器所在主机上运行命令，产出标准化输出文本。
/// 跨平台：Windows 用 PowerShell `-EncodedCommand`（base64 UTF-16LE，规避引号/代码页问题）；
/// Unix 用 bash 脚本（无 BOM 写入，避免首行被当作 BOM 前缀导致 command not found）。
/// 命令在隔离沙箱目录运行 + 超时强制终止 + 输出截断。
/// </summary>
public sealed class ShellRunner
{
    private const int DefaultTimeoutSec = 30;
    private const int MaxTimeoutSec = 60;
    private const int MaxOutputChars = 12_000;

    public async Task<string> RunAsync(string command, string? cwd, int? timeoutSec, string? query, CancellationToken ct)
    {
        // 统一在沙箱工作目录内运行（含路径穿越防御），命令落盘为脚本执行：
        // Unix 下 bash 脚本；Windows 下直接 PowerShell -EncodedCommand（无需脚本文件）。
        var dir = ResolveWorkDir(cwd);
        Directory.CreateDirectory(dir);
        string fileName, argsText, scriptPath = "";
        if (OperatingSystem.IsWindows())
        {
            var ps = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8;" + command;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
            fileName = "powershell.exe";
            argsText = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
        }
        else
        {
            scriptPath = Path.Combine(dir, "client_run.sh");
            // UTF8Encoding(false) 无 BOM：带 BOM 的脚本首行会被 bash 当成 \xEF\xBB\xBF 前缀
            await File.WriteAllTextAsync(scriptPath, command, new UTF8Encoding(false), ct);
            fileName = "/bin/bash";
            argsText = "\"" + scriptPath + "\"";
        }
        var argvJson = JsonSerializer.Serialize(new { query = query ?? "" });

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argsText,
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["QUERY"] = query ?? "";
        psi.Environment["ARGV_JSON"] = argvJson;

        var timeoutMs = Math.Clamp(timeoutSec.GetValueOrDefault(DefaultTimeoutSec), 1, MaxTimeoutSec) * 1000;
        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new InvalidOperationException("无法启动命令进程。");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var completed = proc.WaitForExit(timeoutMs);
        if (!completed) proc.Kill(entireProcessTree: true);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!completed) return "（本机命令执行超时，已终止）";

        var sb = new StringBuilder();
        if (stdout.Length > 0) sb.AppendLine(stdout.TrimEnd());
        if (stderr.Length > 0) sb.AppendLine("stderr: " + stderr.TrimEnd());
        sb.AppendLine($"（退出码 {proc.ExitCode}）");
        return Truncate(sb.ToString().TrimEnd());
    }

    /// <summary>工作目录：默认用户临时目录下一个固定沙箱；允许相对子目录，不允许逃逸到沙箱之外（路径穿越防御）。</summary>
    private static string ResolveWorkDir(string? cwd)
    {
        var root = Path.Combine(Path.GetTempPath(), "agui-native-bridge");
        if (string.IsNullOrWhiteSpace(cwd) || cwd == ".")
            return root;
        var candidate = Path.GetFullPath(Path.Combine(root, cwd));
        // 仅在沙箱根内时采纳，否则回落到根目录（本机桥语义：不逃逸用户临时沙箱）
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? candidate
            : root;
    }

    private static string Truncate(string? s)
        => string.IsNullOrWhiteSpace(s) ? "（命令无输出）" : (s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + "\n…（输出已截断）");
}
