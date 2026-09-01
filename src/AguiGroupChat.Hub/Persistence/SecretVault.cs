using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Hub.Persistence;

/// <summary>
/// 静态密钥保险箱：用服务端对称密钥（AES-256-GCM）对<b>少量</b>敏感性字段做静态加密，
/// 用于快照 / 数据库扩展区落盘前的字段级加密（如模型 API Key、TOTP 密钥）。
///
/// 密钥来源（按优先级）：
///   1. 配置项 <c>Secrets:DataProtectionKey</c>（或环境变量 <c>SECRETS__DATAPROTECTIONKEY</c>）；
///   2. 未配置时自动生成一份并持久化到数据目录的 <c>data/secret-vault.key</c>，保证升级既有部署
///      无需人工改配置即可加密落盘。生成的文件权限依赖运行账号，部署方应确保数据目录受控。
///
/// 格式：加密值以 <c>ENC_v1:</c> 前缀 + Base64(nonce ‖ ciphertext ‖ tag)。
/// 兼容旧值：输入不带 <c>ENC_v1:</c> 前缀时按明文返回（旧版本数据、未启用加密），
/// 实现「开箱即用 + 渐进加固」，不会让既有快照失效。
/// </summary>
public sealed class SecretVault
{
    private readonly byte[] _key;
    private readonly ILogger<SecretVault> _logger;

    private const string Prefix = "ENC_v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public SecretVault(IConfiguration configuration, IHostEnvironment environment, ILogger<SecretVault> logger)
    {
        _logger = logger;
        _key = LoadOrCreateKey(configuration, environment);
    }

    // 进程级写锁：同一进程内多个宿主（如测试并行启动多个 app）共用同一数据目录密钥文件，串行化「生成 + 写盘」防竞争
    private static readonly object KeyFileLock = new();

    private byte[] LoadOrCreateKey(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Secrets:DataProtectionKey"] ?? configuration["SECRETS__DATAPROTECTIONKEY"];
        if (!string.IsNullOrWhiteSpace(configured))
            return DeriveKey(configured);

        var keyFile = Path.Combine(environment.ContentRootPath, "data", "secret-vault.key");
        var dir = Path.GetDirectoryName(keyFile)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        lock (KeyFileLock)
        {
            // 并发场景下返回磁盘上实际生效的密钥（保证各角色读到的与落盘一致，重启后可解密）
            if (File.Exists(keyFile))
            {
                try { return File.ReadAllBytes(keyFile); }
                catch (Exception ex) { _logger.LogWarning(ex, "读取密钥文件失败，将重新生成：{File}", keyFile); }
            }

            var generated = RandomNumberGenerator.GetBytes(32);
            try
            {
                // CreateNew：仅当不由其它实例创建时写入，单次句柄原子完成；竞争时抛 IOException 走下方读回已存在文件
                using var fs = new FileStream(keyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                fs.Write(generated, 0, generated.Length);
                _logger.LogInformation("已生成并持久化静态加固密钥：{File}", keyFile);
            }
            catch (IOException)
            {
                // 另一个实例抢先创建：读回它的密钥，保证一致
                try { return File.ReadAllBytes(keyFile); }
                catch (Exception ex) { _logger.LogWarning(ex, "密钥文件已被占用且回读失败：{File}", keyFile); }
            }
            catch (Exception ex)
            {
                // 写失败不阻断启动：退化为「每次进程内随机」，静态加密仍生效但重启后无法解密已落盘密文（等同未持久化）。
                _logger.LogWarning(ex, "无法持久化静态加固密钥，后续落盘密文重启后不可恢复：{File}", keyFile);
            }
            return generated;
        }
    }

    private static byte[] DeriveKey(string secret)
    {
        // 用 SHA-256 把任意长度口令收敛为 32 字节密钥
        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>加密字符串。null/空直接返回；编码失败（理论不发生）时返回原值，避免阻断写盘。</summary>
    public string? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || plaintext.StartsWith(Prefix, StringComparison.Ordinal))
            return plaintext;

        var payload = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[payload.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, payload, cipher, tag);

        var blob = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, blob, NonceSize + cipher.Length, TagSize);

        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>解密字符串。不带 <c>ENC_v1:</c> 前缀按明文原样返回（兼容旧版未加密数据）。解密失败返回 null 并告警。</summary>
    public string? Decrypt(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return ciphertext;
        if (!ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
            return ciphertext; // 旧版明文

        try
        {
            var blob = Convert.FromBase64String(ciphertext[Prefix.Length..]);
            if (blob.Length < NonceSize + TagSize) return null;
            var nonce = blob[..NonceSize];
            var tag = blob[^TagSize..];
            var cipher = blob[NonceSize..^TagSize];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "敏感字段解密失败（密钥不匹配或数据损坏）");
            return null;
        }
    }
}
