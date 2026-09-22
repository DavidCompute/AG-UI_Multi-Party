using System.Text.Json;
using AguiGroupChat.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能工具的<b>入参形状</b>：查询参数与“摊平传参”两种都要收。
///
/// <para>
/// 回归背景（用户实际遇到）：工具只声明一个必填 <c>query</c> 时，模型把技能参数
/// <b>摊平</b>直接传进来（<c>{title, slides}</c> 而不是 <c>{query:"{...}"}</c>）绑定就失败，
/// 模型收到“缺少 query”，下一轮再包进 query 重发 —— 用户看到的就是
/// 「参数需要放在 <c>query</c> 里，我重新提交：」，白跑一轮还多花一次 token。
/// </para>
/// </summary>
public sealed class SkillToolArgumentTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static AIFunctionArguments Args(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return new AIFunctionArguments(d);
    }

    /// <summary>老写法（query 里放 JSON 字符串）必须保持原样——既有模型行为不能变。</summary>
    [Fact]
    public void QueryString_IsPassedThroughUnchanged()
    {
        var payload = """{"title":"年度总结","slides":[{"type":"cover"}]}""";
        Assert.Equal(payload, AgentCatalog.SkillToolFunction.NormalizeSkillInput(Args(("query", payload))));
    }

    /// <summary>摊平传参：整个参数对象就是技能入参（紧凑 JSON）。</summary>
    [Fact]
    public void FlatArguments_AreSerializedAsSkillInput()
    {
        var input = AgentCatalog.SkillToolFunction.NormalizeSkillInput(Args(
            ("title", Json("\"年度总结\"")),
            ("slides", Json("""[{"type":"cover","title":"封面"}]"""))));
        using var doc = JsonDocument.Parse(input);
        Assert.Equal("年度总结", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("cover", doc.RootElement.GetProperty("slides")[0].GetProperty("type").GetString());
        // 不应出现把参数再包一层 query 的情况
        Assert.False(doc.RootElement.TryGetProperty("query", out _));
    }

    /// <summary>有些模型会把 query 直接写成 JSON 对象（而不是字符串）：也要认。</summary>
    [Fact]
    public void QueryAsJsonObject_IsAccepted()
    {
        var input = AgentCatalog.SkillToolFunction.NormalizeSkillInput(
            Args(("query", Json("""{"title":"x"}"""))));
        Assert.Equal("""{"title":"x"}""", input);
    }

    [Fact]
    public void NoArguments_BecomesEmptyObject()
    {
        Assert.Equal("{}", AgentCatalog.SkillToolFunction.NormalizeSkillInput(null));
        Assert.Equal("{}", AgentCatalog.SkillToolFunction.NormalizeSkillInput(Args()));
    }

    /// <summary>
    /// schema 必须**不再要求** query，并允许额外字段——否则严格校验的提供方会把摊平传参直接拒掉，
    /// 重试照样发生（修了归一化也没用）。
    /// </summary>
    [Fact]
    public void Schema_DoesNotRequireQuery_AndAllowsExtraProperties()
    {
        var tool = new AgentCatalog.SkillToolFunction("t", "d", (_, _) => Task.FromResult<object?>("ok"));
        var schema = tool.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("additionalProperties").GetBoolean(), "必须允许额外字段（摊平传参）");
        Assert.False(schema.TryGetProperty("required", out var required) && required.GetArrayLength() > 0,
            "query 不能是必填项");
        // 仍然把 query 写进 schema：愿意按老写法调的模型照旧
        Assert.True(schema.GetProperty("properties").TryGetProperty("query", out _));
    }

    /// <summary>整条链路：用摊平参数调用工具，委托收到的就是“技能入参 JSON”。</summary>
    [Fact]
    public async Task InvokingWithFlatArguments_HandsTheSkillJsonAsOneInput()
    {
        string? seen = null;
        var tool = new AgentCatalog.SkillToolFunction("t", "d", (input, _) =>
        {
            seen = input;
            return Task.FromResult<object?>("done");
        });
        var result = await tool.InvokeAsync(Args(("title", Json("\"封面\"")), ("author", Json("\"产品部\""))));
        Assert.Equal("done", result);
        Assert.NotNull(seen);
        using var doc = JsonDocument.Parse(seen!);
        Assert.Equal("封面", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("产品部", doc.RootElement.GetProperty("author").GetString());
    }

    /// <summary>
    /// 接线护栏（源码扫描，与其它组合根护栏同一手法）：技能工具必须走 <c>SkillToolFunction</c>，
    /// 不能退回“必填 query 的 AIFunctionFactory.Create” —— 那会把摊平传参又变成一次无效重试。
    /// </summary>
    [Fact]
    public void Catalog_WiresSkillToolsThroughSkillToolFunction()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AguiGroupChat.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "AguiGroupChat.Agents", "AgentCatalog.cs"));
        Assert.Contains("new SkillToolFunction(", src);
        Assert.DoesNotContain("AIFunctionFactory.Create((string query", src);
    }
}
