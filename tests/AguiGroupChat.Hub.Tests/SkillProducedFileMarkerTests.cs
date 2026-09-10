using AguiGroupChat.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能产物（produce_file 标记）→ 附件回档：让「技能生成的文件」能在前端直接下载。
/// 直接测生产实现 <see cref="AgentGateway.ExtractProduceFileObjects"/>（不复制一份逻辑，避免两处漂移）。
/// </summary>
public sealed class SkillProducedFileMarkerTests
{
    private static List<string> Paths(string content)
    {
        var found = new List<string>();
        foreach (var json in AgentGateway.ExtractProduceFileObjects(content))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("path", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String)
                    found.Add(p.GetString()!);
            }
            catch { /* 两份候选里解析不出来的是正常现象（原样/还原各一份） */ }
        }
        return found.Distinct().ToList();
    }

    [Fact]
    public void ParsesPlainMarker()
    {
        var content = """{"ok":true,"produce_file":{"path":"C:\\out\\a.docx","name":"a.docx","bytes":12}}""";
        Assert.Contains("C:\\out\\a.docx", Paths(content));
    }

    [Fact]
    public void ParsesEscapedMarker()
    {
        // 真实场景：技能结果常作为 JSON 字符串嵌在助手正文里，引号被转义
        var content = """技能返回：{\"ok\":true,\"produce_file\":{\"path\":\"D:/x/报告.docx\",\"name\":\"报告.docx\",\"bytes\":999}}""";
        Assert.Contains("D:/x/报告.docx", Paths(content));
    }

    [Fact]
    public void ParsesMultipleMarkers()
    {
        var content = """{"produce_file":{"path":"/a/一.docx"}}{"produce_file":{"path":"/a/二.docx"}}""";
        var got = Paths(content);
        Assert.Contains("/a/一.docx", got);
        Assert.Contains("/a/二.docx", got);
    }

    [Fact]
    public void IgnoresUnrelatedPathsInText()
    {
        // 正文里偶然提到的路径不应被当产物（只有显式 produce_file 标记才算）
        Assert.Empty(Paths("我已经把文件保存到 /app/docs/随便写的.docx，请查收。"));
    }

    [Fact]
    public void ToleratesWhitespaceAndMissingFields()
    {
        Assert.Contains("/a/b.docx", Paths("""{"produce_file" : {"path" : "/a/b.docx" , "bytes":1}}"""));
        // 没有 path 字段 → 不提路径，而不是抛错
        Assert.Empty(Paths("""{"produce_file":{"name":"x.docx"}}"""));
    }

    [Fact]
    public void HandlesNestedBracesInValue()
    {
        // 值里带花括号/转义引号时，必须靠括号配对截取，而不是贪心/非贪心正则
        var content = """{"produce_file":{"path":"/a/x.docx","meta":{"note":"含 } 和 { 的值"}}}""";
        Assert.Contains("/a/x.docx", Paths(content));
    }

    [Fact]
    public void BuiltinSkillEmitsMarker()
    {
        // 内置技能正文里必须带 produce_file 标记，否则回档逻辑拿不到产物
        var c = new AgentSkillCatalog(NullLoggerFactory.Instance, new AgentOptions());
        foreach (var id in new[] { "docx_gongwen", "docx_notice", "docx_report" })
            Assert.Contains("produce_file", c.Get(id)!.Body);
    }
}
