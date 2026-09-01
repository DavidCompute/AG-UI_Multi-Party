using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>静态加固保险箱单元测试：加密/解密往返、明文向后兼容、损坏密文不抛异常。</summary>
public sealed class SecretVaultTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), $"agui-vault-{Guid.NewGuid():N}");

    private SecretVault CreateVault(IConfiguration config)
    {
        var env = new FakeHostEnv(_tmpDir);
        return new SecretVault(config, env, NullLogger<SecretVault>.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public void EncryptDecrypt_RoundTrips()
    {
        var vault = CreateVault(new ConfigurationBuilder().AddInMemoryCollection().Build());
        const string secret = "JBSWY3DPEHPK3PXP-abcd-1234"; // 示例 base32
        var enc = vault.Encrypt(secret);
        Assert.NotNull(enc);
        Assert.NotEqual(secret, enc);
        Assert.StartsWith("ENC_v1:", enc);
        Assert.Equal(secret, vault.Decrypt(enc));
    }

    [Fact]
    public void Encrypt_IsNonDeterministic()
    {
        var vault = CreateVault(new ConfigurationBuilder().AddInMemoryCollection().Build());
        var a = vault.Encrypt("same-value");
        var b = vault.Encrypt("same-value");
        Assert.NotEqual(a, b); // 每次随机 nonce
    }

    [Fact]
    public void Decrypt_Plaintext_PassesThrough()
    {
        var vault = CreateVault(new ConfigurationBuilder().AddInMemoryCollection().Build());
        // 旧版未加密数据：不带前缀直接透传
        Assert.Equal("legacy-plaintext", vault.Decrypt("legacy-plaintext"));
    }

    [Fact]
    public void Decrypt_Garbage_ReturnsNull_NoThrow()
    {
        var vault = CreateVault(new ConfigurationBuilder().AddInMemoryCollection().Build());
        Assert.Null(vault.Decrypt("ENC_v1:!!not-base64@@"));
        Assert.Null(vault.Decrypt("ENC_v1:AQIDBA==")); // nonce 不足
    }

    [Fact]
    public void ConfiguredKey_OverridesGeneratedFile()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:DataProtectionKey"] = "explicit-key",
        }).Build();
        var vault = CreateVault(config);
        var enc = vault.Encrypt("value");
        Assert.StartsWith("ENC_v1:", enc!);
        Assert.Equal("value", vault.Decrypt(enc));
        // 显式配置密钥时不写自生成密钥文件
        Assert.False(File.Exists(Path.Combine(_tmpDir, "data", "secret-vault.key")));
    }

    [Fact]
    public void GeneratedKey_PersistsAcrossInstances()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var v1 = CreateVault(config);
        var enc = v1.Encrypt("persist-me");

        // 第二个实例（模拟重启）读同一生成密钥文件，应能解密
        var v2 = CreateVault(config);
        Assert.Equal("persist-me", v2.Decrypt(enc));
    }

    private sealed class FakeHostEnv : IHostEnvironment
    {
        public FakeHostEnv(string contentRoot) => ContentRootPath = contentRoot;
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
