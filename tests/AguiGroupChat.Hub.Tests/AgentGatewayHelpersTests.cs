using System.Net.Http;
using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 智能体网关纯静态工具（<see cref="AgentGatewayHelpers"/>）单元测试。
/// 这部分逻辑从 AgentGateway 剥离后应保持行为不变，这里独立锁定其语义。
/// </summary>
public sealed class AgentGatewayHelpersTests
{
    // ---- IsWebSocketEndpoint ----
    [Theory]
    [InlineData("ws://host:8080", true)]
    [InlineData("wss://host", true)]
    [InlineData("http://host", false)]
    [InlineData("https://host", false)]
    public void IsWebSocketEndpoint_Scheme(string endpoint, bool expected)
        => Assert.Equal(expected, AgentGatewayHelpers.IsWebSocketEndpoint(endpoint));

    // ---- BuildExternalThreadId ----
    [Theory]
    [InlineData(null, "thread_1")]        // 无话题 → 沿用群级 threadId
    [InlineData("main", "thread_1")]      // main 话题 → 沿用群级 threadId
    [InlineData("t1", "thread_1:t1")]     // 非 main 话题 → 追加后缀
    public void BuildExternalThreadId_SeparatesTopics(string? topicId, string expected)
        => Assert.Equal(expected, AgentGatewayHelpers.BuildExternalThreadId("thread_1", topicId));

    // ---- FormatBytes ----
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void FormatBytes_HumanReadable(long bytes, string expected)
        => Assert.Equal(expected, AgentGatewayHelpers.FormatBytes(bytes));

    // ---- TruncateForChain ----
    [Fact]
    public void TruncateForChain_NullOrShort_Unchanged()
    {
        Assert.Equal("", AgentGatewayHelpers.TruncateForChain(null));
        Assert.Equal("abc", AgentGatewayHelpers.TruncateForChain("abc"));
    }

    [Fact]
    public void TruncateForChain_Long_TruncatesTo200WithEllipsis()
    {
        var text = new string('x', 500);
        var result = AgentGatewayHelpers.TruncateForChain(text);
        Assert.Equal(201, result.Length); // 200 字符 + 省略号
        Assert.EndsWith("…", result);
    }

    // ---- ChunkReply ----
    [Fact]
    public void ChunkReply_SplitsByFixedSize()
    {
        var chunks = AgentGatewayHelpers.ChunkReply(new string('a', 7), 3).ToList();
        Assert.Equal(3, chunks.Count);
        Assert.Equal(3, chunks[0].Length);
        Assert.Equal(3, chunks[1].Length);
        Assert.Equal(1, chunks[2].Length);
    }

    [Fact]
    public void ChunkReply_ShortText_SingleChunk()
    {
        Assert.Equal(["hi"], AgentGatewayHelpers.ChunkReply("hi", 3).ToArray());
    }

    [Fact]
    public void ChunkReply_EmptyText_SingleEmptyChunk()
    {
        Assert.Equal([""], AgentGatewayHelpers.ChunkReply("", 3).ToArray());
    }

    // ---- IsAllowedAttachmentUrl（SSRF scheme 白名单） ----
    [Theory]
    [InlineData("https://cdn.example.com/a.png", true)]
    [InlineData("http://host/f", true)]
    [InlineData("data:image/png;base64,AAA", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedAttachmentUrl_OnlySafeSchemes(string? url, bool expected)
        => Assert.Equal(expected, AgentGatewayHelpers.IsAllowedAttachmentUrl(url));

    // ---- TruncateAttachmentName ----
    [Fact]
    public void TruncateAttachmentName_NullOrShort_FallbackOrUnchanged()
    {
        Assert.Equal("attachment", AgentGatewayHelpers.TruncateAttachmentName(null));
        Assert.Equal("a.png", AgentGatewayHelpers.TruncateAttachmentName("a.png"));
    }

    [Fact]
    public void TruncateAttachmentName_Long_TruncatesTo255()
    {
        var name = new string('n', 400) + ".png";
        var result = AgentGatewayHelpers.TruncateAttachmentName(name);
        Assert.Equal(255, result.Length);
    }

    // ---- IsRetryableModelError ----
    [Fact]
    public void IsRetryableModelError_429And5xx_Retryable()
    {
        var tooMany = new HttpRequestException("", null, System.Net.HttpStatusCode.TooManyRequests);
        var serverErr = new HttpRequestException("", null, System.Net.HttpStatusCode.BadGateway);
        Assert.True(AgentGatewayHelpers.IsRetryableModelError(tooMany));
        Assert.True(AgentGatewayHelpers.IsRetryableModelError(serverErr));
    }

    [Fact]
    public void IsRetryableModelError_4xxAndCancel_NotRetryable()
    {
        var notFound = new HttpRequestException("", null, System.Net.HttpStatusCode.NotFound);
        Assert.False(AgentGatewayHelpers.IsRetryableModelError(notFound));
        Assert.False(AgentGatewayHelpers.IsRetryableModelError(new OperationCanceledException()));
    }

    [Fact]
    public void IsRetryableModelError_Timeout_Retryable()
    {
        Assert.True(AgentGatewayHelpers.IsRetryableModelError(new TimeoutException()));
        Assert.True(AgentGatewayHelpers.IsRetryableModelError(new IOException()));
    }

    // ---- DescribeToolResult ----
    [Fact]
    public void DescribeToolResult_NullOrString_Passthrough()
    {
        Assert.Equal("", AgentGatewayHelpers.DescribeToolResult(null));
        Assert.Equal("ok", AgentGatewayHelpers.DescribeToolResult("ok"));
    }

    [Fact]
    public void DescribeToolResult_TruncatesLongString()
    {
        var longText = new string('x', 6000);
        var result = AgentGatewayHelpers.DescribeToolResult(longText);
        Assert.Equal(AgentGatewayHelpers.MaxToolResultChars + 1, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void DescribeToolResult_Object_SerializesJson()
    {
        var result = AgentGatewayHelpers.DescribeToolResult(new { count = 3 });
        Assert.Contains("\"count\":3", result);
    }

    // ---- AguiJsonOrDefault ----
    [Fact]
    public void AguiJsonOrDefault_ValidJson_ReturnsType()
    {
        Assert.NotNull(AgentGatewayHelpers.AguiJsonOrDefault<TestDto>("{\"x\":1}"));
        // 非法 JSON → null
        Assert.Null(AgentGatewayHelpers.AguiJsonOrDefault<TestDto>("not-json"));
        // 类型不匹配（JSON 标量 → 类）→ 反序列化抛错 → null
        Assert.Null(AgentGatewayHelpers.AguiJsonOrDefault<TestDto>("\"str\""));
    }

    // ---- DescribeModelError ----
    [Fact]
    public void DescribeModelError_NonClientException_UsesModelErrorCode()
    {
        Assert.Contains("MODEL_ERROR", AgentGatewayHelpers.DescribeModelError(new InvalidOperationException("boom")));
        Assert.DoesNotContain("boom", AgentGatewayHelpers.DescribeModelError(new InvalidOperationException("boom"))); // 脱敏：不透出详情
    }

    /// <summary>AguiJsonOrDefault 用到的简单 DTO（JsonIgnoreCondition 空值忽略下仅序列化非空成员）。</summary>
    private sealed class TestDto
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}
