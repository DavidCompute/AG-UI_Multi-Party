using AguiGroupChat.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 启动自检 <see cref="EmbeddingDimensionProbe"/>：HTTP embedding 端点返回的维度必须与
/// <c>Agents:Memory:EmbeddingDimensions</c> 一致，否则「RAG 静默失效」。
///
/// <para>
/// 背景：进程内 llama 提供方会按模型实际维度建表，配置不符只影响日志；而 HTTP 提供方只能信配置，
/// 维度不一致时向量列（vector(1024)）与模型输出（如 768 维）错配 → 写入失败、检索恒为空，
/// 用户只看到「数字员工记不住事」。这里钉住三条边界：
/// ① 维度一致 → 保持启用；
/// ② 维度不一致 → <b>禁用语义记忆</b>（显式关闭，不静默错）；
/// ③ 端点不可用 / 未返回向量 / 本地 llama / 记忆本来就关着 → 都不动启用状态、也不打端点。
/// </para>
/// </summary>
public sealed class EmbeddingDimensionProbeTests
{
    private sealed class FakeProvider : IEmbeddingProvider
    {
        private readonly float[]? _vector;
        private readonly Exception? _fault;

        public int Calls { get; private set; }

        public FakeProvider(float[]? vector) => _vector = vector;

        public FakeProvider(Exception fault) => _fault = fault;

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        {
            Calls++;
            if (_fault is not null) throw _fault;
            return Task.FromResult(_vector);
        }

        public void Dispose() { }
    }

    private static AgentOptions Options(int dimensions, bool enabled = true, string provider = "http") => new()
    {
        Memory = new MemoryOptions
        {
            Enabled = enabled,
            Provider = provider,
            EmbeddingDimensions = dimensions,
            EmbeddingModel = "bge-m3:latest",
        },
    };

    private static EmbeddingDimensionProbe Probe(IEmbeddingProvider provider, AgentOptions options)
        => new(provider, options, NullLogger<EmbeddingDimensionProbe>.Instance);

    [Fact]
    public async Task MatchingDimension_KeepsMemoryEnabled()
    {
        var options = Options(1024);
        var provider = new FakeProvider(new float[1024]);

        await Probe(provider, options).StartAsync(CancellationToken.None);

        Assert.True(options.Memory.Enabled);
        Assert.Equal(1, provider.Calls); // 只探测一次，不是每次启动都反复打端点
    }

    [Fact]
    public async Task MismatchedDimension_DisablesMemory()
    {
        // 正是「把 130MB/768 维的 nomic 当成 bge-m3」的现场：配置 1024，端点数 768
        var options = Options(1024);
        var provider = new FakeProvider(new float[768]);

        await Probe(provider, options).StartAsync(CancellationToken.None);

        Assert.False(options.Memory.Enabled);
    }

    [Fact]
    public async Task OtherConfiguredDimension_IsAccepted()
    {
        // 换了模型但同步改了配置（如 qwen3-embedding=2560）：一致就放行，不能写死 1024
        var options = Options(2560);
        var provider = new FakeProvider(new float[2560]);

        await Probe(provider, options).StartAsync(CancellationToken.None);

        Assert.True(options.Memory.Enabled);
    }

    [Fact]
    public async Task EndpointUnavailable_KeepsMemoryEnabled()
    {
        // 端点暂时起不来不该变成「永久禁用记忆」：原来就是「连不上就当检索为空」，保持可恢复
        var options = Options(1024);
        var provider = new FakeProvider(new HttpRequestException("connection refused"));

        await Probe(provider, options).StartAsync(CancellationToken.None);

        Assert.True(options.Memory.Enabled);
    }

    [Fact]
    public async Task NoVectorReturned_KeepsMemoryEnabled()
    {
        var options = Options(1024);
        var provider = new FakeProvider((float[]?)null);

        await Probe(provider, options).StartAsync(CancellationToken.None);

        Assert.True(options.Memory.Enabled);
    }

    [Fact]
    public async Task LocalLlamaProvider_IsNotProbed()
    {
        // llama 分支按实际维度建表，不需要（也不应该）用配置维度去判它
        var options = Options(1024, provider: "llama");
        var provider = new FakeProvider(new InvalidOperationException("不应被调用"));

        await Probe(provider, options).StartAsync(CancellationToken.None);

        Assert.True(options.Memory.Enabled);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task DisabledMemory_IsNotProbed()
    {
        var options = Options(1024, enabled: false);
        var provider = new FakeProvider(new InvalidOperationException("不应被调用"));

        await Probe(provider, options).StartAsync(CancellationToken.None);

        Assert.False(options.Memory.Enabled);
        Assert.Equal(0, provider.Calls);
    }
}
