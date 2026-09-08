using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>服务端 C#（dotnet）技能宿主编译/运行回归：常用 BCL API（加密随机、数学、XML 等）无需 #r 也应可用。</summary>
public sealed class DotnetSkillHostTests
{
    private static DotnetSkillHost NewHost()
        => new DotnetSkillHost(
            NullLoggerFactory.Instance.CreateLogger<DotnetSkillHost>(),
            Path.Combine(Path.GetTempPath(), "agui-dotnet-host-test-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void CryptoRandomGenerator_CompilesAndRuns()
    {
        // 复现用户技能「加密安全随机密码生成器」用到的 BCL API（System.Security.Cryptography.RandomNumberGenerator）
        var src = """
            using System;
            using System.Security.Cryptography;

            public class Program
            {
                public static string Run(string input)
                {
                    var bytes = new byte[16];
                    RandomNumberGenerator.Fill(bytes);
                    return "ok-" + bytes.Length;
                }
            }
            """;
        var host = NewHost();
        var output = host.Run(src, "", CancellationToken.None);
        Assert.DoesNotContain("编译失败", output);
        Assert.StartsWith("ok-16", output);
    }

    [Fact]
    public void CommonBclApis_CompileAndRun()
    {
        // BigInteger / XML / 编码等常用 BCL：同样属框架自带，不应编译失败
        var src = """
            using System;
            using System.Numerics;
            using System.Xml.Linq;
            using System.Text;

            public class Program
            {
                public static string Run(string input)
                {
                    var big = BigInteger.Pow(2, 32);
                    var xml = new XElement("r", "v").ToString();
                    var enc = Encoding.UTF8.GetString(new byte[] { 104, 105 });
                    return big + "|" + xml + "|" + enc;
                }
            }
            """;
        var host = NewHost();
        var output = host.Run(src, "", CancellationToken.None);
        Assert.DoesNotContain("编译失败", output);
        Assert.Contains("4294967296|<r>v</r>|hi", output);
    }

    [Fact]
    public void CompileOnly_ValidSource_ReturnsEmpty()
    {
        var host = NewHost();
        var err = host.CompileOnly("public class P { public static string Run(string i) { return i; } }", CancellationToken.None);
        Assert.Equal("", err);
    }

    [Fact]
    public void CompileOnly_SyntaxError_ReportsCompileFailure()
    {
        var host = NewHost();
        var err = host.CompileOnly("public class P { public static string Run(string i) { return i } }", CancellationToken.None);
        Assert.Contains("编译失败", err);
    }

    [Fact]
    public void CompileOnly_MissingEntry_ReportsMissingRun()
    {
        var host = NewHost();
        // 编译能过但没有 public static string Run(string) 入口 → 应明确提示缺入口（生成后自测据此要求修复）
        var err = host.CompileOnly("public class P { public static string NoEntry() { return \"x\"; } }", CancellationToken.None);
        Assert.Contains("缺少入口", err);
    }

    [Fact]
    public void TopLevelMethods_AreAutoWrappedAndRun()
    {
        // 用户/模型常把方法直接写在文件顶层（未包 class）→ 宿主编译前自动包类，不再报 CS0106/CS8805
        var src = """
            using System;

            public static string Run(string input)
            {
                return "wrapped-" + input;
            }

            public static string Helper(string s) { return s + "!"; }
            """;
        var host = NewHost();
        var output = host.Run(src, "hi", CancellationToken.None);
        Assert.DoesNotContain("编译失败", output);
        Assert.StartsWith("wrapped-hi", output);
    }

    [Fact]
    public void CompileOnly_WindowsOnlyApi_OnWindowsHostSucceeds()
    {
        if (!OperatingSystem.IsWindows()) return; // 仅 Windows 运行时含 Microsoft.Win32.Registry 元数据
        var host = NewHost();
        var err = host.CompileOnly(
            "using Microsoft.Win32;\npublic class P { public static string Run(string i) { return Registry.CurrentUser?.Name ?? i; } }",
            CancellationToken.None);
        Assert.Equal("", err);
    }
}
