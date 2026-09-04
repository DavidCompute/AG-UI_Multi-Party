using System.Reflection;
using System.Text.Json;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 递归补查综合答复 JSON 的<b>容错解码</b>回归锁定：模型在 <c>answer</c> 里放真实换行/未转义引号导致
/// 整包 <c>JsonDocument.Parse</c> 失败时，应回退到从原文剥离 needsMore + answer 正文，避免把
/// {"needsMore":…,"gather":…,"answer":…} 这段内部决策 JSON 原样泄漏给群聊用户。
/// 仅通过反射触碰 AgentGateway 私有静态方法，不联网、不启桥。
/// </summary>
public sealed class AgentGatewayRecursiveParseFallbackTests
{
    private static readonly Assembly AgentsAsm = typeof(AguiGroupChat.Agents.AgentGateway).Assembly;
    private static readonly Type Gateway = AgentsAsm.GetType("AguiGroupChat.Agents.AgentGateway")!;

    private static MethodInfo Static(string name)
    {
        var m = Gateway.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(m);
        return m;
    }

    private static object? Invoke(string method, object? arg)
        => Static(method).Invoke(null, [arg]);

    private static object? Parse(string text) => Invoke("ParseRecursiveResponse", text);
    private static object? Fallback(string text) => Invoke("ExtractRecursiveAnswerFallback", text);
    private static string Unwrap(string text) => (string)Invoke("UnwrapCoordinationAnswer", text)!;

    private static (bool NeedsMore, string? Answer, int Gather) Read(object? result)
    {
        Assert.NotNull(result);
        var t = result!.GetType();
        var needsMore = (bool)t.GetProperty("NeedsMore")!.GetValue(result)!;
        var answer = (string?)t.GetProperty("Answer")!.GetValue(result);
        var gatherCount = t.GetProperty("Gather")!.GetValue(result) is System.Collections.ICollection c ? c.Count : 0;
        return (needsMore, answer, gatherCount);
    }

    [Fact]
    public void ParseRecursiveResponse_WellFormed_Parses()
    {
        var text = "{\"needsMore\":false,\"gather\":[{\"kind\":\"skill\",\"target\":\"t1\"}],\"answer\":\"ok\"}";
        var (nm, answer, gather) = Read(Parse(text));
        Assert.False(nm);
        Assert.Equal("ok", answer);
        Assert.Equal(1, gather);
    }

    /// <summary>真实泄漏场景：answer 里夹了真实换行 + 中文引号 → 整包 Parse 失败 → 走到容错回退，
    /// 必须返回剥壳后的 answer 正文。<b>不会</b>把决策 JSON 原样漏给群聊。
    /// 高度还原线上「①首诊小助」那条把决策 JSON 原样回给用户的长答复。</summary>
    [Fact]
    public void ParseRecursiveResponse_MultilineAnswer_WholeJsonFails_ButAnswerExtracted()
    {
        var answerLeads = "您好，我是①首诊小助。您说“看看电脑有没有问题”，但目前我这边收不到您电脑的运行数据。";
        var answerTrails = "处理完请回我一声“已搞定/还不行”，我好确认是否关闭工单。";
        var answer = answerLeads + "\n1️⃣ 现象：具体哪里不对劲？\n2️⃣ 发生时间：从什么时候开始的？\n" + answerTrails;
        // 真实换行塞进字符串值 → 该段「不是」合法 JSON，JsonDocument.Parse 必然抛错；Parse 只能经回退把正文取出。
        var text = "{\"needsMore\":false,\"gather\":[],\"answer\":\"" + answer + "\"}";
        var (nm, got, gather) = Read(Parse(text));
        Assert.False(nm);
        Assert.Equal(0, gather);
        Assert.NotNull(got);
        Assert.Contains(answerLeads, got);
        Assert.Contains(answerTrails, got);
        Assert.DoesNotContain("needsMore", got);   // 决策 JSON 外壳被剥干净
        Assert.DoesNotContain("gather", got);
        // 真实换行应保留成正文排版
        Assert.Contains("\n", got);
    }

    [Fact]
    public void Fallback_MultilineAnswer_StripsShellAndReturnsAnswerText()
    {
        var answerLeads = "您好，我是①首诊小助。您说“看看电脑有没有问题”，但目前我这边收不到您电脑的运行数据。";
        var answerTrails = "处理完请回我一声“已搞定/还不行”，我好确认是否关闭工单。";
        var answer = answerLeads + "\n1️⃣ 现象：具体哪里不对劲？\n2️⃣ 发生时间：从什么时候开始的？\n" + answerTrails;
        var text = "{\"needsMore\":false,\"gather\":[],\"answer\":\"" + answer + "\"}";
        var (nm, got, gather) = Read(Fallback(text));
        Assert.False(nm);
        Assert.Equal(0, gather);
        Assert.NotNull(got);
        Assert.Contains(answerLeads, got);
        Assert.Contains(answerTrails, got);
        Assert.DoesNotContain("needsMore", got);   // 决策 JSON 外壳被剥干净
        Assert.DoesNotContain("gather", got);
        // 真实换行应保留成正文排版
        Assert.Contains("\n", got);
    }

    [Fact]
    public void Fallback_AnswerWithRealTrailingBrace_DoesNotSwallowContent()
    {
        // 内容里带具体机器描述 + 结尾的 "）} " 干扰：仍应取到整个 answer 正文
        var answer = "建议按以下做：\n- 打开【此电脑】右键管理 → 磁盘碎片整理。\n- 不行再联系管理员。";
        var text = "{\"needsMore\":true,\"gather\":[],\"answer\":\"" + answer + "\"}";
        var (nm, got, _) = Read(Fallback(text));
        Assert.True(nm);
        Assert.EndsWith("管理员。", got!.Trim());
    }

    [Fact]
    public void Fallback_NoAnswerKey_ReturnsNull()
    {
        Assert.Null(Fallback("{\"needsMore\":false}"));
        Assert.Null(Fallback("garbage no braces"));
    }

    // ---- UnwrapCoordinationAnswer（普通 run / 编排计划的“最后一道防线”，同样要容错真实换行） ----

    [Fact]
    public void Unwrap_MultilineAnswer_ShowsOnlyAnswerBody()
    {
        // 高度还原用户贴的泄漏：answer 里大量真实换行 + 中文引号 + emoji → 整包 JsonDocument.Parse 必失败。
        var body =
            "您好，我已经帮您对电脑做了一次基础体检（开机启动项、系统服务与计划任务、本机基本状态），总体结论是：电脑没有严重问题，主要是一些启动项偏多，建议做优化。具体如下：\n"
            + "\n1️⃣ 开机启动项偏多（这是最值得注意的点）\n"
            + "- 第三方软件随开机自启（建议精简）：百度网盘、迅雷、Docker Desktop 等。\n"
            + "👉 建议：日常不常用的软件可以关闭开机自启。\n"
            + "\n📌 小结：电脑整体健康，无需报修；另外也提醒您留意磁盘剩余空间，建议定期清理。";
        var text = "{\"needsMore\":false,\"gather\":[],\"answer\":\"" + body + "\"}";
        var got = Unwrap(text);
        Assert.DoesNotContain("needsMore", got);
        Assert.DoesNotContain("gather", got);
        Assert.Contains("开机启动项偏多", got);
        Assert.Contains("建议定期清理", got);   // 不应因长度/换行丢失尾部
        // 应保留正文里的真实换行成排版，而不该把整段 JSON 原样回给用户
        Assert.StartsWith("您好", got.Trim());
        Assert.DoesNotContain("}", got);
    }

    [Fact]
    public void Unwrap_PlainChineseText_Unchanged()
    {
        const string plain = "好的，我来看看有没有明显问题的迹象。您的电脑开机还算正常。";
        Assert.Equal(plain, Unwrap(plain));
    }

    [Fact]
    public void Unwrap_ValidCoordJson_ReturnsAnswer()
    {
        var text = "{\"needsMore\":false,\"gather\":[],\"answer\":\"hello\"}";
        Assert.Equal("hello", Unwrap(text));
    }
}
