namespace AguiGroupChat.Hub.Infra;

public static class IdGenerator
{
    /// <summary>生成短随机 ID（16 位十六进制），配合 group_ / msg_ / user_ 等前缀使用。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..16];
}
