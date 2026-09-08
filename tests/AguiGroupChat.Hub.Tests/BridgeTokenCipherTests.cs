using System.Security.Cryptography;
using System.Text;
using AguiGroupChat.NativeBridge;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 本机桥令牌加密（BridgeTokenCipher）与服务器下载端加密格式（AES-256-GCM：12B nonce + 16B tag + 密文，
/// base64，enc:v1: 前缀）的兼容性：同一密钥下服务器侧 Seal → 桥侧 TryDecrypt 应还原明文。
/// </summary>
public class BridgeTokenCipherTests
{
    private const string Prefix = "enc:v1:";

    /// <summary>模拟服务器侧加密（与 Web NativeBridgeDownloadApi.EncryptTokenForServer 同布局）。</summary>
    private static (string CipherText, string KeyB64) Seal(string token)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(token);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var outBytes = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(outBytes, 0);
        tag.CopyTo(outBytes, 12);
        cipher.CopyTo(outBytes, 28);
        return (Prefix + Convert.ToBase64String(outBytes), Convert.ToBase64String(key));
    }

    [Fact]
    public void DecryptsServerSealedToken_WithExplicitKey()
    {
        var (cipher, key) = Seal("560183e47b855ee19b3d69a600c624a2");

        var plain = BridgeTokenCipher.TryDecrypt(cipher, key, keyFile: null, out var error);

        Assert.Null(error);
        Assert.Equal("560183e47b855ee19b3d69a600c624a2", plain);
    }

    [Fact]
    public void DecryptsServerSealedToken_FromKeyFile()
    {
        var (cipher, key) = Seal("tok-from-file-abc");
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, key + "\n");
            var plain = BridgeTokenCipher.TryDecrypt(cipher, keyBase64: "", keyFile: tmp, out var error);
            Assert.Null(error);
            Assert.Equal("tok-from-file-abc", plain);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void PassesThroughPlainTextToken_WhenNotEncrypted()
    {
        var plain = BridgeTokenCipher.TryDecrypt("legacy-plain-token", keyBase64: null, keyFile: null, out var error);
        Assert.Null(error);
        Assert.Equal("legacy-plain-token", plain);
    }

    [Fact]
    public void EmptyToken_PassesThrough()
    {
        var plain = BridgeTokenCipher.TryDecrypt("", keyBase64: null, keyFile: null, out var error);
        Assert.Null(error);
        Assert.Equal("", plain);
    }

    [Fact]
    public void MissingKey_ReportsError()
    {
        var plain = BridgeTokenCipher.TryDecrypt(Prefix + "AAAA", keyBase64: null, keyFile: null, out var error);
        Assert.Null(plain);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
