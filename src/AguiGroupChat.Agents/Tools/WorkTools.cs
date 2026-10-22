using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AguiGroupChat.Agents;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// 工作型数字员工工具（<see cref="AgentOptions.WorkToolsEnabled"/> + 智能体 <c>EnableWorkTools</c> 同时开启时挂载）：
///   - work_list_dir / work_read_file：只读，限定在该智能体的 <c>{WorkSpaceRoot}/{agentId}</c> 工作区；
///   - work_write_file：写文件（强制人工审批）；
///   - work_run_command：执行 <see cref="AgentOptions.WorkToolCommands"/> 白名单中的可执行文件（强制人工审批）。
///
/// <para>安全边界：相对路径 + 全路径归一化 + 拒绝越界 / 符号链接 / 联接点；命令不经 shell、拒绝元字符、
/// 只解析 PATH 中的可执行文件（不接受路径），并剔除含 KEY / TOKEN / SECRET / PASSWORD / AUTH 的环境变量。</para>
/// </summary>
public sealed class WorkTools
{
    private const int MaxListEntries = 200;
    private const long MaxReadFileBytes = 10 * 1024 * 1024;
    private const int MaxOutputChars = 20_000;
    private const int MaxCommandLength = 500;

    private readonly AgentOptions _options;
    private readonly string _workspace;
    private readonly ILogger _logger;
    private readonly string[] _allowedCommands;

    public WorkTools(AgentOptions options, string workspaceRoot, string agentId, ILoggerFactory loggerFactory)
    {
        _options = options;
        _workspace = Path.GetFullPath(Path.Combine(workspaceRoot, agentId));
        Directory.CreateDirectory(_workspace);
        _logger = loggerFactory.CreateLogger<WorkTools>();
        _allowedCommands = (options.WorkToolCommands ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>本智能体的工作区绝对路径（测试 / 诊断用）。</summary>
    public string WorkspaceDir => _workspace;

    [Description("列出工作区目录内容。只接受工作区内的相对路径（默认 .）。返回目录 / 文件、大小与修改时间。")]
    public string ListDir([Description("相对工作区的目录，默认 .")] string? path = null)
    {
        try
        {
            var full = Resolve(path);
            if (File.Exists(full))
            {
                var file = new FileInfo(full);
                return $"文件：{Rel(full)}（{file.Length} 字节，修改于 {file.LastWriteTimeUtc:yyyy-MM-dd HH:mm}Z）";
            }
            if (!Directory.Exists(full)) return $"目录不存在：{Rel(full)}";

            var all = new DirectoryInfo(full).EnumerateFileSystemInfos()
                .OrderBy((e) => e is DirectoryInfo ? 0 : 1)
                .ThenBy((e) => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxListEntries + 1)
                .ToList();
            if (all.Count == 0) return $"目录 {Rel(full)} 为空。";

            var sb = new StringBuilder();
            sb.AppendLine($"目录：{Rel(full)}");
            foreach (var e in all.Take(MaxListEntries))
            {
                if (e is DirectoryInfo)
                    sb.AppendLine($"[DIR]  {Rel(e.FullName)}/");
                else if (e is FileInfo f)
                    sb.AppendLine($"[FILE] {Rel(e.FullName)}  {f.Length} 字节  {f.LastWriteTimeUtc:yyyy-MM-dd HH:mm}Z");
            }
            if (all.Count > MaxListEntries) sb.AppendLine($"……（仅显示前 {MaxListEntries} 项）");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [Description("读取工作区内文本文件的片段。只接受相对路径；二进制文件会被拒绝。返回内容带“不可信资料”边界，仅作参考。")]
    public string ReadFile(
        [Description("相对工作区的文件路径")] string path,
        [Description("起始字符偏移，默认 0")] int startIndex = 0,
        [Description("本次最多返回的字符数，默认 12000，最大 20000")] int maxChars = 12000)
    {
        try
        {
            var full = Resolve(path);
            if (!File.Exists(full)) return $"文件不存在：{Rel(full)}";
            var info = new FileInfo(full);
            if (info.Length > MaxReadFileBytes)
                return $"文件过大（{info.Length} 字节，上限 {MaxReadFileBytes} 字节）。";

            var bytes = File.ReadAllBytes(full);
            if (Array.IndexOf(bytes, (byte)0) >= 0) return "二进制文件不支持读取。";
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            if (text.Length == 0) return $"文件 {Rel(full)} 为空。";

            var start = Math.Clamp(startIndex, 0, text.Length);
            var limit = Math.Clamp(maxChars <= 0 ? _options.WorkToolMaxReadChars : maxChars, 1, 20_000);
            var chunk = text.Substring(start, Math.Min(limit, text.Length - start));
            if (chunk.Length == 0) return $"已到文件末尾（共 {text.Length} 字符）。";

            var header = $"文件：{Rel(full)}（{start}–{start + chunk.Length} / {text.Length} 字符）\n\n";
            return header + UntrustedBoundary.Wrap(chunk);
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [Description("在工作区内写入文本文件（覆盖同名文件，自动创建父目录）。仅接受相对路径；此操作需要用户在知聚内批准。")]
    public async Task<string> WriteFile(
        [Description("相对工作区的文件路径")] string path,
        [Description("要写入的文本内容（UTF-8）")] string content)
    {
        try
        {
            if (content is null) return "content 不能为空。";
            var limit = Math.Clamp(_options.WorkToolMaxWriteChars, 1, 8 * 1024 * 1024);
            if (content.Length > limit) return $"内容过长（{content.Length} 字符，上限 {limit}）。";

            var full = Resolve(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var bytes = Encoding.UTF8.GetByteCount(content);
            return $"已写入 {Rel(full)}（{bytes} 字节）。";
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [Description("在工作区内执行白名单命令（不经 shell，拒绝重定向 / 管道 / 变量等元字符）。此操作需要用户在知聚内批准。")]
    public async Task<string> RunCommand([Description("命令行，例如 whoami / git status")] string command)
    {
        try
        {
            if (_allowedCommands.Length == 0)
                return "未配置允许执行的命令（Agents:WorkToolCommands），已拒绝。";
            if (string.IsNullOrWhiteSpace(command)) return "command 为空。";
            if (command.Length > MaxCommandLength) return $"命令过长（上限 {MaxCommandLength} 字符）。";
            if (HasShellMeta(command)) return "命令包含不允许的 shell 元字符（& | < > ^ % ! ; ` $ 换行等）。";

            var tokens = Tokenize(command);
            if (tokens.Count == 0) return "command 为空。";
            var exeName = tokens[0];
            if (exeName.Contains('/') || exeName.Contains('\\') || exeName.Contains(':'))
                return "只允许 PATH 中的可执行文件名，不允许路径。";

            var exeBase = Path.GetFileNameWithoutExtension(exeName);
            if (!_allowedCommands.Any((c) => string.Equals(Path.GetFileNameWithoutExtension(c), exeBase, StringComparison.OrdinalIgnoreCase)))
                return $"命令 {exeBase} 不在白名单中（允许：{string.Join(", ", _allowedCommands)}）。";

            var resolved = ResolveExecutable(exeName);
            if (resolved is null) return $"未找到可执行文件：{exeName}";
            if (IsUnderWorkspace(resolved)) return "拒绝执行工作区内的可执行文件。";

            var timeoutMs = Math.Clamp(_options.WorkToolTimeoutMs, 1000, 120_000);
            var psi = new ProcessStartInfo
            {
                FileName = resolved,
                WorkingDirectory = _workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in tokens.Skip(1)) psi.ArgumentList.Add(arg);
            foreach (var key in psi.Environment.Keys.ToList())
            {
                if (IsSensitiveEnv(key)) psi.Environment.Remove(key);
            }

            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return "命令启动失败。";
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxOutputChars);
            var stderrTask = ReadBoundedAsync(process.StandardError, MaxOutputChars);

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                return $"命令超时（{timeoutMs} ms）已终止。";
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var sb = new StringBuilder();
            sb.AppendLine($"退出码：{process.ExitCode}");
            if (!string.IsNullOrWhiteSpace(stdout)) sb.AppendLine("stdout：\n" + stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr)) sb.AppendLine("stderr：\n" + stderr.TrimEnd());
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "work_run_command 执行失败：{Command}", command);
            return Fail(ex);
        }
    }

    private string Resolve(string? relative)
    {
        var rel = string.IsNullOrWhiteSpace(relative) ? "." : relative.Trim();
        if (Path.IsPathRooted(rel)) throw new InvalidOperationException("只允许工作区内的相对路径。");
        var full = Path.GetFullPath(Path.Combine(_workspace, rel));
        if (!IsUnderWorkspace(full)) throw new InvalidOperationException("路径越出工作区。");
        EnsureNoReparsePoints(full);
        return full;
    }

    private bool IsUnderWorkspace(string full)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = _workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, root, comparison)) return true;
        return full.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || full.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    /// <summary>拒绝工作区内的符号链接 / 联接点（它们可能把读写带出工作区）。</summary>
    private void EnsureNoReparsePoints(string full)
    {
        var rel = Path.GetRelativePath(_workspace, full);
        if (rel is "." or "") return;
        var current = _workspace;
        foreach (var part in rel.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("工作区不允许符号链接 / 联接点。");
        }
    }

    private string Rel(string full)
    {
        var rel = Path.GetRelativePath(_workspace, full).Replace('\\', '/');
        return rel is "." or "" ? "." : rel;
    }

    private static bool HasShellMeta(string command)
        => command.IndexOfAny(['&', '|', '<', '>', '^', '%', '!', ';', '`', '\r', '\n', '\t']) >= 0
           || command.Contains("$(", StringComparison.Ordinal)
           || command.Contains("${", StringComparison.Ordinal);

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var ch in command)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else current.Append(ch);
                continue;
            }
            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }
        if (quote != '\0') throw new InvalidOperationException("引号未闭合。");
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>只在 PATH 中查找可执行文件（当前目录 / 工作区不参与搜索，防止放置同名恶意程序）。</summary>
    private static string? ResolveExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var hasExtension = Path.HasExtension(name);
        string[] extensions = OperatingSystem.IsWindows()
            ? (hasExtension ? new[] { "" } : new[] { ".exe", ".com" })
            : new[] { "" };
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = dir.Trim().Trim('"');
            if (clean.Length == 0) continue;
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(clean, name + ext);
                try
                {
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch
                {
                    // 非法 PATH 项：跳过
                }
            }
        }
        return null;
    }

    private static bool IsSensitiveEnv(string key)
        => key.Contains("KEY", StringComparison.OrdinalIgnoreCase)
           || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
           || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
           || key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
           || key.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase)
           || key.Contains("AUTH", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int max)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            if (sb.Length < max)
            {
                sb.Append(buffer, 0, Math.Min(read, max - sb.Length));
                if (sb.Length >= max) truncated = true;
            }
            else
            {
                truncated = true;
            }
        }
        if (truncated) sb.Append("\n…（输出已截断）");
        return sb.ToString();
    }

    private static string Fail(Exception ex) => "错误：" + ex.Message;
}
