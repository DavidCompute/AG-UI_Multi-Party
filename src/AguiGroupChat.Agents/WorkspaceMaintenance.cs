using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>工作区清理结果（用于日志与测试断言）。</summary>
public sealed record WorkspaceSweepResult(int Workspaces, int DeletedFiles, long DeletedBytes, long TotalBytes);

/// <summary>
/// 工作区容量治理（1.0.311）：<c>{WorkSpaceRoot}/{agentId}/</c> 会随工作型数字员工长期运行不断堆积，
/// 需要有「按时间清理 + 按容量清理」的兜底，否则迟早把宿主磁盘写满。
///
/// <para>规则（两步，顺序固定）：</para>
/// <list type="number">
/// <item>保留期：删除修改时间早于 <c>now - WorkSpaceRetentionDays</c> 的文件（0 = 不按时间清理）。</item>
/// <item>总容量：若整根目录仍超过 <c>WorkSpaceQuotaMb</c>，按「修改时间最旧优先」继续删除，直到回到配额内。</item>
/// </list>
/// <para>安全边界：只删 <paramref name="root"/> 之下的普通文件；遇到符号链接 / 联接点直接跳过（不跟进、不删除），
/// 空目录自叶向根回收。所有文件系统异常都被吞掉并计入日志，不影响调用方。</para>
/// </summary>
public static class WorkspaceMaintenance
{
    public static WorkspaceSweepResult Sweep(
        string root,
        int quotaMb,
        int retentionDays,
        ILogger? logger = null,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(root)) return new WorkspaceSweepResult(0, 0, 0, 0);
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot)) return new WorkspaceSweepResult(0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "工作区清理跳过：根目录不可解析 {Root}", root);
            return new WorkspaceSweepResult(0, 0, 0, 0);
        }

        var workspaces = 0;
        var deletedFiles = 0;
        long deletedBytes = 0;
        var files = new List<(string Path, long Size, DateTime ModifiedUtc)>();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(fullRoot))
            {
                if (IsReparsePoint(dir)) continue; // 工作区本身是链接：不碰
                workspaces++;
                Collect(dir, files, logger);
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "工作区枚举失败（按已收集文件继续）：{Root}", fullRoot);
        }

        long total = 0;
        foreach (var f in files) total += f.Size;

        var survivors = new List<(string Path, long Size, DateTime ModifiedUtc)>(files.Count);
        var cutoff = retentionDays > 0
            ? (now ?? DateTimeOffset.UtcNow).UtcDateTime.AddDays(-retentionDays)
            : DateTime.MinValue;

        foreach (var f in files)
        {
            if (retentionDays > 0 && f.ModifiedUtc < cutoff)
            {
                if (TryDelete(f.Path, logger))
                {
                    deletedFiles++;
                    deletedBytes += f.Size;
                    total -= f.Size;
                }
                continue;
            }
            survivors.Add(f);
        }

        var quotaBytes = quotaMb <= 0 ? 0 : quotaMb * 1024L * 1024L;
        if (quotaBytes > 0 && total > quotaBytes)
        {
            // 最久未修改的先删：优先牺牲长期没人动的中间产物
            survivors.Sort((a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc));
            foreach (var f in survivors)
            {
                if (total <= quotaBytes) break;
                if (!TryDelete(f.Path, logger)) continue;
                deletedFiles++;
                deletedBytes += f.Size;
                total -= f.Size;
            }
        }

        // 回收空目录（自叶向根；不跟进链接；保留工作区根目录本身，下次使用时直接可用）
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(fullRoot))
            {
                if (IsReparsePoint(dir)) continue;
                PruneEmptyDirs(dir, fullRoot, keepSelf: true, logger);
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "工作区空目录回收失败（已忽略）：{Root}", fullRoot);
        }

        return new WorkspaceSweepResult(workspaces, deletedFiles, deletedBytes, Math.Max(0, total));
    }

    /// <summary>统计单个工作区已用字节（写文件时的配额判断复用同一口径）。</summary>
    public static long DirectorySize(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    total += f.Length;
                }
                catch { /* 枚举与读取之间被删：忽略 */ }
            }
        }
        catch { /* 目录不可读：按 0 计 */ }
        return total;
    }

    /// <summary>递归收集普通文件；跳过符号链接 / 联接点与不可读目录。</summary>
    private static void Collect(string dir, List<(string Path, long Size, DateTime ModifiedUtc)> sink, ILogger? logger)
    {
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "工作区子目录不可读（跳过）：{Dir}", dir);
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (entry is DirectoryInfo sub)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    Collect(sub.FullName, sink, logger);
                }
                else if (entry is FileInfo f && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    sink.Add((f.FullName, f.Length, f.LastWriteTimeUtc));
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "工作区条目读取失败（跳过）：{Path}", entry.FullName);
            }
        }
    }

    private static void PruneEmptyDirs(string dir, string root, bool keepSelf, ILogger? logger)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (IsReparsePoint(sub)) continue;
                PruneEmptyDirs(sub, root, keepSelf: false, logger);
            }
            if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
            if (string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return;
            if (keepSelf) return;
            Directory.Delete(dir, recursive: false);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "空目录回收失败（已忽略）：{Dir}", dir);
        }
    }

    private static bool TryDelete(string path, ILogger? logger)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "工作区文件删除失败（跳过）：{Path}", path);
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }
}

/// <summary>
/// 工作区维护服务（1.0.311）：宿主启动后 10 分钟跑第一次，此后每 6 小时一次，
/// 按 <see cref="AgentOptions.WorkSpaceRetentionDays"/> / <see cref="AgentOptions.WorkSpaceQuotaMb"/> 清理。
/// 两个配置都为 0（不限制）时完全跳过，不占定时器。
/// <para>只处理 <see cref="AgentOptions.WorkSpaceRoot"/>（默认 <c>data/workspaces</c>），
/// 不触碰技能运行目录 <c>data/skillruns</c>。</para>
/// </summary>
public sealed class WorkspaceMaintenanceService : IHostedService, IDisposable
{
    private readonly AgentOptions _options;
    private readonly IHostEnvironment _env;
    private readonly ILogger<WorkspaceMaintenanceService> _logger;
    private Timer? _timer;

    public WorkspaceMaintenanceService(AgentOptions options, IHostEnvironment env, ILogger<WorkspaceMaintenanceService> logger)
    {
        _options = options;
        _env = env;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.WorkSpaceQuotaMb <= 0 && _options.WorkSpaceRetentionDays <= 0) return Task.CompletedTask;
        _timer = new Timer(_ => RunOnce(), null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(6));
        _logger.LogDebug("工作区维护已启动（保留 {Days} 天 / 配额 {QuotaMb} MB）",
            _options.WorkSpaceRetentionDays, _options.WorkSpaceQuotaMb);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    private void RunOnce()
    {
        try
        {
            var root = Path.GetFullPath(Path.IsPathRooted(_options.WorkSpaceRoot)
                ? _options.WorkSpaceRoot
                : Path.Combine(_env.ContentRootPath, _options.WorkSpaceRoot));
            var result = WorkspaceMaintenance.Sweep(root, _options.WorkSpaceQuotaMb, _options.WorkSpaceRetentionDays, _logger);
            if (result.DeletedFiles > 0)
                _logger.LogInformation("工作区清理：删除 {Files} 个文件（{Mb} MB），当前占用 {TotalMb} MB（{Workspaces} 个工作区）",
                    result.DeletedFiles, Math.Round(result.DeletedBytes / 1024d / 1024d, 1),
                    Math.Round(result.TotalBytes / 1024d / 1024d, 1), result.Workspaces);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "工作区定时清理异常");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
