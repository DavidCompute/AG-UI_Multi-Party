using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 工具返回收集器：技能产物（produce_file 标记）以工具真实返回为准，不依赖模型在正文里复述 JSON。
/// 回归背景：模型常把技能返回的 JSON 改写成自然语言（“已生成文档，位置 /tmp/x.docx”），
/// 导致标记丢失、技能产物不再出现在附件里。
/// </summary>
public sealed class ToolResultCollectorTests
{
    [Fact]
    public void CollectsMarkerThatModelWouldParaphraseAway()
    {
        var c = new ToolResultCollector();
        c.Add("""{"ok":true,"produce_file":{"path":"/app/docs/报告.docx","name":"报告.docx","bytes":2640}}""");

        // 收集器保留原始返回，网关据此扫描到产物（模型正文里可能一个字都没有）
        Assert.Contains("produce_file", c.Text);
        Assert.Contains("/app/docs/报告.docx", ExtractPaths(c.Text));
    }

    [Fact]
    public void AccumulatesAcrossMultipleToolCalls()
    {
        var c = new ToolResultCollector();
        c.Add("""{"produce_file":{"path":"/a/一.docx"}}""");
        c.Add(null);
        c.Add("");
        c.Add("""{"produce_file":{"path":"/a/二.docx"}}""");

        var text = c.Text;
        Assert.Contains("/a/一.docx", text);
        Assert.Contains("/a/二.docx", text);
    }

    [Fact]
    public void CapsTotalSizeToBoundMemory()
    {
        // 长跑运行可能产生大量工具输出；收集器只用于扫标记，无需全文，必须有上限
        var c = new ToolResultCollector();
        var chunk = new string('x', 64 * 1024);
        for (var i = 0; i < 8; i++) c.Add(chunk);

        Assert.True(c.Text.Length <= 256 * 1024 + 64 * 1024, "收集器未限制总量：" + c.Text.Length);
    }

    [Fact]
    public void MarkerSurvivesWithinDescribeToolResultTruncation()
    {
        // 网关经 DescribeToolResult 截断后写入收集器（上限 5000 字符）；
        // 真实 docx 技能返回约 350 字符，标记必须在截断之内，否则回档失效。
        var result = """{"ok":true,"path":"/app/docs/报告.docx","scene":"report","blocks":3,"produce_file":{"path":"/app/docs/报告.docx","name":"报告.docx","bytes":2640},"message":"已生成 Word 文档"}""";
        var described = AgentGatewayHelpers.DescribeToolResult(result);
        Assert.Contains("produce_file", described);
    }

    [Fact]
    public void ExtractHandlesUnicodeEscapedQuotesFromJsonSerializer()
    {
        // 回归：工具返回是「持 JSON 字符串的 JsonElement」时，DescribeToolResult 的
        // JsonSerializer.Serialize 会把内层引号写成 \u0022（不是 \"）—— .NET 默认写转义器的行为。
        // 早期 ExtractProduceFileObjects 只处理 \"，导致花括号能配对但 JsonDocument 解析失败，
        // 标记丢失、技能产物不生成附件。
        var content = "{" + U0022 + "ok" + U0022 + ":true," + U0022 + "produce_file" + U0022 + ":{"
            + U0022 + "path" + U0022 + ":" + U0022 + "/app/docs/report.docx" + U0022 + ","
            + U0022 + "bytes" + U0022 + ":2640}}";
        Assert.Contains(U0022, content); // 前提：确实是 \u0022 形态

        Assert.Contains("/app/docs/report.docx", ExtractPaths(content));
    }

    [Fact]
    public void ExtractHandlesUnicodeEscapesEndToEndThroughDescribeToolResult()
    {
        // 真实链路形状（容器实测确认）：工具返回是「持 JSON 字符串的 JsonElement」，
        // DescribeToolResult → JsonSerializer.Serialize 后引号变 \u0022。
        // 这正是技能产物丢失的实际形态 —— 必须经这一路径构造用例，否则测不到。
        using var doc = System.Text.Json.JsonDocument.Parse(
            "\"{\\\"ok\\\":true,\\\"produce_file\\\":{\\\"path\\\":\\\"/app/docs/输出.docx\\\",\\\"bytes\\\":12}}\"");
        var described = AgentGatewayHelpers.DescribeToolResult(doc.RootElement);
        Assert.Contains(U0022, described); // 关键前提：确实是 \u0022 形态

        var c = new ToolResultCollector();
        c.Add(described);
        Assert.Contains("/app/docs/输出.docx", ExtractPaths(c.Text));
    }

    /// <summary>字面量 \u0022（六个字符），避开 C# 字符串转义。</summary>
    private const string U0022 = "\\u0022";

    /// <summary>按生产语义提取路径：逐个候选试解析，无法解析的跳过（与网关一致）。</summary>
    private static List<string> ExtractPaths(string content)
    {
        var found = new List<string>();
        foreach (var json in AgentGateway.ExtractProduceFileObjects(content))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("path", out var p)
                    && p.ValueKind == System.Text.Json.JsonValueKind.String)
                    found.Add(p.GetString()!);
            }
            catch { /* 原样/还原两份候选，解析不出的属正常 */ }
        }
        return found.Distinct().ToList();
    }
}
