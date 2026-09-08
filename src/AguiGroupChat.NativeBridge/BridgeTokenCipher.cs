using System.Security.Cryptography;

namespace AguiGroupChat.NativeBridge;

/// <summary>
/// 桥连接令牌的加密存储：网页下载的安装包中 <c>bridge-config.txt</c> 的 <c>TOKEN</c> 不以明文落盘，
/// 而是 <c>enc:v1:&lt;base64&gt;</c> 密文（AES-256-GCM，12B nonce + 16B tag + 密文）。
/// 解密密钥=同目录 <c>bridge.key</c>（32 字节随机，base64）或 <c>--token-key &lt;base64&gt;</c> 显式传入。
///
/// 安全边界说明：密钥随安装包一起交付，本层加密的目的是“避免令牌以明文出现在配置/文档/截图里，
/// 以及包被随手转发时令牌不直接裸露”，属于凭据静态加密（obfuscation-level）；能逆向拿到 bridge.key
/// 的调用者仍可还原令牌。若要求“包被复制到别的机器也无法使用”，需服务器侧按下载/机器签发一次性令牌
/// （可吊销），本模块不替代该能力。
/// </summary>
public static class BridgeTokenCipher
{
    private const string Prefix = "enc:v1:";

    /// <summary>本地随机密钥加密（供网页在线 setup 落盘、安装包生成通用）：返回 enc:v1: 密文与 base64 密钥。</summary>
    public static string EncryptToken(string token, out string keyBase64)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        keyBase64 = Convert.ToBase64String(key);
        return Prefix + Convert.ToBase64String(Seal(key, token));
    }

    /// <summary>解密 enc:v1: 密文。密钥优先取显式 base64 key，其次读 <paramref name="keyFile"/>（取首行 base64）。</summary>
    public static string? TryDecrypt(string value, string? keyBase64, string? keyFile, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return value; // 非密文：原样（历史明文 / 空）
        byte[] key;
        if (!string.IsNullOrWhiteSpace(keyBase64))
        {
            if (!TryBase64(keyBase64.Trim(), out key)) { error = "bridge.key / --token-key 不是合法 base64"; return null; }
        }
        else if (!string.IsNullOrWhiteSpace(keyFile) && File.Exists(keyFile))
        {
            var line = File.ReadLines(keyFile).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Trim()))?.Trim();
            if (line is null || !TryBase64(line, out key)) { error = $"密钥文件 {keyFile} 为空或不是合法 base64"; return null; }
        }
        else
        {
            error = "TOKEN 为加密格式但缺少解密密钥（同目录 bridge.key 或 --token-key）";
            return null;
        }
        try
        {
            var raw = Convert.FromBase64String(value[Prefix.Length..]);
            if (raw.Length < 12 + 16 + 1) { error = "令牌密文长度不合法"; return null; }
            var nonce = raw.AsSpan(0, 12);
            var tag = raw.AsSpan(12, 16);
            var cipher = raw.AsSpan(28);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            error = "令牌解密失败：" + ex.Message;
            return null;
        }
    }

    private static byte[] Seal(byte[] key, string token)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = System.Text.Encoding.UTF8.GetBytes(token);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var outBytes = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(outBytes, 0);
        tag.CopyTo(outBytes, 12);
        cipher.CopyTo(outBytes, 28);
        return outBytes;
    }

    private static bool TryBase64(string s, out byte[] bytes)
    {
        try { bytes = Convert.FromBase64String(s); return bytes.Length == 32; }
        catch { bytes = []; return false; }
    }
}
