using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 工作区容量治理（1.0.311）：保留期清理、配额清理（最旧优先）、根目录文件不被误删、空目录回收。
/// </summary>
public sealed class WorkspaceMaintenanceTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "agui-ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string AddFile(string dir, string name, int bytes, DateTime? modifiedUtc = null)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        if (modifiedUtc is not null) File.SetLastWriteTimeUtc(path, modifiedUtc.Value);
        return path;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void Sweep_DeletesExpiredFiles()
    {
        var root = NewRoot();
        try
        {
            var ws = Path.Combine(root, "agent_a");
            var old = AddFile(ws, "old.txt", 10, DateTime.UtcNow.AddDays(-40));
            var fresh = AddFile(ws, "fresh.txt", 10, DateTime.UtcNow);

            var r = WorkspaceMaintenance.Sweep(root, quotaMb: 0, retentionDays: 30);

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
            Assert.Equal(1, r.DeletedFiles);
            Assert.Equal(1, r.Workspaces);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Sweep_TrimsToQuota_OldestFirst()
    {
        var root = NewRoot();
        try
        {
            var ws = Path.Combine(root, "agent_a");
            var oldest = AddFile(ws, "1.bin", 600 * 1024, DateTime.UtcNow.AddDays(-3));
            var middle = AddFile(ws, "2.bin", 600 * 1024, DateTime.UtcNow.AddDays(-2));
            var newest = AddFile(ws, "3.bin", 600 * 1024, DateTime.UtcNow.AddDays(-1));

            var r = WorkspaceMaintenance.Sweep(root, quotaMb: 1, retentionDays: 0);

            Assert.False(File.Exists(oldest));
            Assert.False(File.Exists(middle));
            Assert.True(File.Exists(newest));
            Assert.Equal(2, r.DeletedFiles);
            Assert.True(r.TotalBytes <= 1024 * 1024, $"剩余 {r.TotalBytes} 字节应不超过 1 MB");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Sweep_LeavesRootLevelFilesAlone()
    {
        // 根目录直属文件不属于任何智能体工作区：即使过期也不删（避免误伤宿主数据）
        var root = NewRoot();
        try
        {
            var stray = AddFile(root, "keep.txt", 10, DateTime.UtcNow.AddDays(-400));
            AddFile(Path.Combine(root, "agent_a"), "old.txt", 10, DateTime.UtcNow.AddDays(-400));

            var r = WorkspaceMaintenance.Sweep(root, quotaMb: 0, retentionDays: 1);

            Assert.True(File.Exists(stray));
            Assert.Equal(1, r.DeletedFiles);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Sweep_RemovesEmptyDirectories()
    {
        var root = NewRoot();
        try
        {
            var nested = Path.Combine(root, "agent_a", "deep", "deeper");
            AddFile(nested, "old.txt", 10, DateTime.UtcNow.AddDays(-40));

            WorkspaceMaintenance.Sweep(root, quotaMb: 0, retentionDays: 30);

            Assert.False(Directory.Exists(Path.Combine(root, "agent_a", "deep")));
            Assert.True(Directory.Exists(Path.Combine(root, "agent_a"))); // 工作区根保留
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Sweep_MissingRoot_ReturnsZero()
    {
        var missing = Path.Combine(Path.GetTempPath(), "agui-missing-" + Guid.NewGuid().ToString("N")[..8]);
        var r = WorkspaceMaintenance.Sweep(missing, quotaMb: 10, retentionDays: 10);
        Assert.Equal(0, r.Workspaces);
        Assert.Equal(0, r.DeletedFiles);
    }

    [Fact]
    public void DirectorySize_SumsRecursively()
    {
        var root = NewRoot();
        try
        {
            AddFile(Path.Combine(root, "a"), "1.bin", 100);
            AddFile(Path.Combine(root, "a", "b"), "2.bin", 200);
            Assert.Equal(300, WorkspaceMaintenance.DirectorySize(root));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ResolveWorkspaceDir_RejectsTraversalAndNesting()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "agent_a"));
            Assert.NotNull(WorkspaceMaintenance.ResolveWorkspaceDir(root, "agent_a"));
            Assert.Null(WorkspaceMaintenance.ResolveWorkspaceDir(root, ".."));
            Assert.Null(WorkspaceMaintenance.ResolveWorkspaceDir(root, "a/b"));
            Assert.Null(WorkspaceMaintenance.ResolveWorkspaceDir(root, @"a\b"));
            Assert.Null(WorkspaceMaintenance.ResolveWorkspaceDir(root, ""));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ResolveFile_RejectsEscapesAndMissing()
    {
        var root = NewRoot();
        try
        {
            var ws = Path.Combine(root, "agent_a");
            var file = AddFile(Path.Combine(ws, "notes"), "a.txt", 10);
            Assert.Equal(file, WorkspaceMaintenance.ResolveFile(ws, "notes/a.txt"));
            Assert.Null(WorkspaceMaintenance.ResolveFile(ws, "../outside.txt"));
            Assert.Null(WorkspaceMaintenance.ResolveFile(ws, Path.Combine(root, "x.txt")));
            Assert.Null(WorkspaceMaintenance.ResolveFile(ws, "notes/missing.txt"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ListFiles_ReturnsRelativePaths()
    {
        var root = NewRoot();
        try
        {
            var ws = Path.Combine(root, "agent_a");
            AddFile(Path.Combine(ws, "notes", "deep"), "b.txt", 5);
            AddFile(ws, "a.txt", 7);
            var files = WorkspaceMaintenance.ListFiles(ws);
            Assert.Equal(2, files.Count);
            Assert.Equal(["a.txt", "notes/deep/b.txt"], files.Select((f) => f.Path).ToArray());
            Assert.Equal(7, files[0].Bytes);
        }
        finally { Cleanup(root); }
    }
}
