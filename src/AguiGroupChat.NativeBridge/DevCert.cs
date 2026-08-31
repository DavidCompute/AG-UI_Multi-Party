using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AguiGroupChat.NativeBridge;

/// <summary>
/// 为回环 HTTPS 发现服务生成自签证书（ECDSA P-256，短期有效期）。
/// 说明：浏览器对自签证书默认不信任——若想用 HTTPS 回环读取，需把该证书加入系统/浏览器信任库；
/// 默认回环用 HTTP（127.0.0.1 本机专用、不经过网络），已足够本机读取非敏感的机器标识。
/// </summary>
internal static class DevCert
{
    public static X509Certificate2 CreateSelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=AguiGroupChat.NativeBridge (loopback)", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        // 合法名/合法 IP：回环地址仅为 127.0.0.1 / localhost
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow;
        var cert = req.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
        // 导出再导入，确保带上私钥且可作服务端证书
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }
}
