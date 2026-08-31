namespace AguiGroupChat.NativeBridge;

/// <summary>
/// 本机唯一客户端标识（client id）：首次生成一个随机 UUID 并持久化到用户本地数据目录，
/// 重启复用、跨机器唯一（避免用机器名导致的同机重名/冲突）。`--client` 显式指定时优先。
/// </summary>
internal static class ClientIdStore
{
    private static string FilePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AguiGroupChat");
        return Path.Combine(dir, "bridge.id");
    }

    public static string LoadOrCreate()
    {
        var path = FilePath();
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 16) return existing;
            }
            var created = "cl_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, created);
            return created;
        }
        catch
        {
            // 本地目录不可写：回落为一次性随机 UUID（每次启动变化；本机回环发现仍可用，但跨重启不稳定）
            return "cl_" + Guid.NewGuid().ToString("N");
        }
    }
}
