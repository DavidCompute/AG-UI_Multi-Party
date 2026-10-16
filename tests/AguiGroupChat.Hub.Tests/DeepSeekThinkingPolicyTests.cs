using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using AguiGroupChat.Agents;
using OpenAI;
using OpenAI.Chat;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// DeepSeek 的思考开关必须在<b>请求体</b>里落地。
///
/// <para>
/// 回归背景：现网 DeepSeek 只有一个模型名（<c>deepseek-flash</c>），官方文档（api-docs.deepseek.com）
/// 明确“模型名请使用 deepseek-flash”，旧的 <c>deepseek-chat</c> / <c>reasoner</c> 等都被路由到同一个
/// DeepSeek-V4.1-Flash；“是否思考”改由请求参数 <c>thinking.type</c> 表达。
/// </para>
///
/// <para>
/// 为何必须钉住：实测 <c>deepseek-flash</c> <b>默认开思考</b>；判定 / 路由这类输出预算只有 8 / 64 token
/// 的调用，思考会把预算全花在思维链上、<b>正文为空</b>，而且<b>不抛任何异常</b>——
/// 表现为“语境的数字员工永不发言 / 指派路由解析出 0 个下游”。所以“关闭思考”这个参数一旦没真正发出去，
/// 就会静默退化为那个老缺陷。这里用离线抓包（自建 Transport）确认它确实进了请求体。
/// </para>
/// </summary>
public sealed class DeepSeekThinkingPolicyTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string Body = "";
        public string? ContentType;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(ct);
                ContentType = request.Content.Headers.ContentType?.MediaType;
            }
            const string json =
                "{\"id\":\"x\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"deepseek-flash\"," +
                "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]," +
                "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static ChatClient Build(string thinkingType, CapturingHandler handler)
    {
        var options = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            Endpoint = new Uri("https://api.deepseek.com"),
        };
        options.AddPolicy(new AgentCatalog.DeepSeekThinkingPolicy(thinkingType), PipelinePosition.BeforeTransport);
        return new ChatClient("deepseek-flash", new ApiKeyCredential("test"), options);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("enabled")]
    [InlineData("adaptive")]
    public async Task Inject_thinking_flag_into_request_body(string type)
    {
        var handler = new CapturingHandler();
        var client = Build(type, handler);

        await client.CompleteChatAsync([new UserChatMessage("hi")]);

        Assert.Contains("\"thinking\":{\"type\":\"" + type + "\"}", handler.Body);
        // 注入是追加：原有字段不丢，媒体类型仍是 JSON。
        Assert.Contains("\"messages\"", handler.Body);
        Assert.Contains("deepseek-flash", handler.Body);
        Assert.Equal("application/json", handler.ContentType);
    }

    [Fact]
    public async Task Inject_exactly_once_and_keeps_body_valid_json()
    {
        var handler = new CapturingHandler();
        var client = Build("disabled", handler);

        await client.CompleteChatAsync([new UserChatMessage("hi")]);

        // 只出现一次 thinking 字段（不会重复追加而变成非法 JSON），且整个 body 仍是合法 JSON。
        var first = handler.Body.IndexOf("\"thinking\"", StringComparison.Ordinal);
        var last = handler.Body.LastIndexOf("\"thinking\"", StringComparison.Ordinal);
        Assert.True(first >= 0, "应至少注入一次 thinking");
        Assert.Equal(first, last);
        using var doc = System.Text.Json.JsonDocument.Parse(handler.Body);
        Assert.True(doc.RootElement.TryGetProperty("thinking", out var thinking));
        Assert.Equal("disabled", thinking.GetProperty("type").GetString());
    }
}
