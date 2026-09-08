using AguiGroupChat.NativeBridge;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>本机桥 C#（client dotnet）技能宿主编译回归：与用户「加密安全随机密码生成器」同类 BCL API 应可用。</summary>
public sealed class NativeBridgeDotnetTests
{
    [Fact]
    public async Task CryptoRandomGenerator_CompilesAndRuns_OnBridge()
    {
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
        var runner = new DotnetRunner();
        var output = await runner.RunAsync(src, "", CancellationToken.None);
        Assert.DoesNotContain("编译失败", output);
        Assert.StartsWith("ok-16", output);
    }
}
