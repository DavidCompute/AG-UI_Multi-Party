using AguiGroupChat.Hub.Agents;

namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// 工作型智能体的工作区沙箱：每个启用 <see cref="AgentDefinition.EnableWorkTools"/> 的智能体
/// 拥有独立工作目录 <c>data/workspaces/&lt;agentId&gt;/</c>（由装配层基于内容根解析）。
/// 所有文件 / 命令工具把路径规约到该目录（解析 <c>..</c> / 软链 / 绝对路径越界一律拒绝），
/// 保证智能体只能读写自己的工作区，碰不到宿主机其他文件。
/// </summary>
public sealed class AgentWorkSpace
{
    private readonly string _root; // 规范化后的工作区根绝对路径（末尾带分隔符）

    public AgentWorkSpace(string rootPath)
    {
        // 规范化根路径：解析 .. / 重复分隔符，末尾统一加目录分隔符便于前缀比较
        _root = Path.GetFullPath(Path.TrimEndingDirectorySeparator(rootPath)) + Path.DirectorySeparatorChar;
    }

    /// <summary>工作区根目录完整路径（绝对）。</summary>
    public string Root => _root;

    /// <summary>生成 / 确保工作区根目录存在，返回根路径；创建失败抛异常（调用方转错误文本）。</summary>
    public string EnsureRoot()
    {
        Directory.CreateDirectory(_root);
        return _root;
    }

    /// <summary>把用户给的文件路径规约到工作区内：返回规范化的绝对路径，且必须位于根目录内；
    /// 越界（含 <c>..</c> / 绝对路径 / 符号链接逃逸）返回 null。</summary>
    public string? ContainsResolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var abs = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
            // 大小写不敏感的 Windows 也要拦，但 Linux 需保持区分：统一用 Ordinal 前缀判断（Normalize 已解析 ..）
            if (!abs.StartsWith(_root, StringComparison.Ordinal) && !abs.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                return null;
            return abs;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>仅供测试：根路径末尾分隔符。</summary>
    public string DirSeparator => Path.DirectorySeparatorChar.ToString();
}
