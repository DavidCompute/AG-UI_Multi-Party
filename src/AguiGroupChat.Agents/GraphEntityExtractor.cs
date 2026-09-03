using System.Text.Json;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>一次文本抽取得到的图元素（实体 + 关系边），由 <see cref="GraphEntityExtractor"/> 产出。</summary>
public sealed record GraphExtraction(
    IReadOnlyList<GraphExtractedEntity> Entities,
    IReadOnlyList<GraphExtractedEdge> Edges);

public sealed record GraphExtractedEntity(string Name, string Type, string? Description);

public sealed record GraphExtractedEdge(string Source, string Relation, string Target);

/// <summary>
/// 图谱 RAG 的实体/关系抽取器：从一条群消息抽取「实体-关系-实体」三元组。
/// 优先调用 LLM（复用 <see cref="AgentCatalog.BuildOpenAIChatClient"/> 的 OpenAI 兼容配置，
/// 与分身人设生成同源）；Provider=mock 或 LLM 调用失败 / 输出无法解析时，回退到
/// <b>规则抽取</b>（引号/书名号内名词、英文专有名词、数值+单位等），保证离线 / 无密钥也可用。
/// 缓存结果为 LLM JSON 结构对象。
/// </summary>
public sealed class GraphEntityExtractor
{
    private readonly AgentOptions _options;
    private readonly ILogger<GraphEntityExtractor> _logger;

    public GraphEntityExtractor(AgentOptions options, ILogger<GraphEntityExtractor> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>从文本抽取三元组；任何失败返回空（图写入侧静默跳过，不影响主流程）。maxTriples 每轮上限。</summary>
    public async Task<GraphExtraction> ExtractAsync(string text, CancellationToken ct)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return new GraphExtraction([], []);

        // 尝试 LLM 抽取；失败 / mock 走规则回退
        try
        {
            if (!string.Equals(_options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            {
                var llm = await TryExtractWithLlmAsync(trimmed, ct);
                if (llm is not null && (llm.Entities.Count > 0 || llm.Edges.Count > 0))
                    return llm;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "图谱 LLM 抽取失败，回退规则抽取");
        }
        return RuleExtract(trimmed);
    }

    /// <summary>LLM 抽取：要求输出 JSON 三元组，解析为 <see cref="GraphExtraction"/>。不可靠输入包边界；解析失败返回 null。</summary>
    private async Task<GraphExtraction?> TryExtractWithLlmAsync(string text, CancellationToken ct)
    {
        var isDeepSeek = string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
        // 实体/关系抽取要求“只输出严格 JSON”，属格式任务：即便全局思考模式开也不进 reasoner（慢+易空/超时，拖延图谱索引）
        var ov = AgentCatalog.StructuredFastModel(isDeepSeek);
        using var client = AgentCatalog.BuildOpenAIChatClient(
            _options, new AgentDefinition { AgentId = "graph_extractor", Nickname = "图谱抽取器" }, isDeepSeek, ov).AsIChatClient();

        var prompt =
            "你是知识图谱抽取器。从给定文本中抽取「实体-关系-实体」三元组，抽取原则：\n" +
            "1. 实体限人物 / 组织 / 产品 / 技术 / 地点 / 项目 / 概念等有意义名词，忽略语气词、无实义的请求词。\n" +
            "2. 关系用简短动词短语（如 负责、推荐、依赖、属于、位于、使用），无法确定时用 related_to。\n" +
            "3. 只输出严格 JSON，不要任何解释或前缀：\n" +
            "{\"entities\":[{\"name\":\"实体名\",\"type\":\"Person|Organization|Product|Technology|Concept|Location|Project\"}],\n" +
            " \"edges\":[{\"source\":\"实体A\",\"relation\":\"关系\",\"target\":\"实体B\"}]}\n\n" +
            "【文本】\n" + UntrustedBoundary.Wrap(text);

        var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
        var raw = (resp.Text ?? "").Trim();
        return TryParseJson(raw);
    }

    /// <summary>解析 LLM 返回的 JSON（容错：剥离可能的前缀 / 代码围栏）。</summary>
    private GraphExtraction? TryParseJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            var entities = new List<GraphExtractedEntity>();
            if (root.TryGetProperty("entities", out var en) && en.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in en.EnumerateArray())
                {
                    var name = (e.TryGetProperty("name", out var nm) ? nm.GetString() : null)?.Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var type = (e.TryGetProperty("type", out var tp) ? tp.GetString() : null)?.Trim() ?? "Concept";
                    var desc = e.TryGetProperty("description", out var de) ? de.GetString() : null;
                    entities.Add(new GraphExtractedEntity(name, type, desc));
                }
            }
            var edges = new List<GraphExtractedEdge>();
            if (root.TryGetProperty("edges", out var ed) && ed.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in ed.EnumerateArray())
                {
                    var src = (e.TryGetProperty("source", out var s) ? s.GetString() : null)?.Trim();
                    var rel = (e.TryGetProperty("relation", out var r) ? r.GetString() : null)?.Trim();
                    var dst = (e.TryGetProperty("target", out var t) ? t.GetString() : null)?.Trim();
                    if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst)) continue;
                    if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) continue;
                    edges.Add(new GraphExtractedEdge(src, string.IsNullOrWhiteSpace(rel) ? GraphMemoryScope.RelatedTo : rel, dst));
                }
            }
            return new GraphExtraction(entities, edges);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "图谱 LLM JSON 解析失败：{Raw}", raw[..Math.Min(raw.Length, 200)]);
            return null;
        }
    }

    /// <summary>规则回退抽取：中文书名号 / 引号内的名词视为实体；英文专有名词（首字母大写连续词）视为实体；
    /// 「X 动词 Y」/「中文冒号」句式抽关系。保证离线可用、测试稳定。</summary>
    private static GraphExtraction RuleExtract(string text)
    {
        var entities = new List<GraphExtractedEntity>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string type)
        {
            var n = name.Trim();
            if (n.Length < 2 || n.Length > 40) return;
            if (n.All(ch => char.IsPunctuation(ch) || char.IsWhiteSpace(ch))) return;
            if (names.Add(n)) entities.Add(new GraphExtractedEntity(n, type, null));
        }

        // 中文书名号《…》与成对引号「…」「…」
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "[《「“]([^》」”]{2,40})[》」”]"))
            Add(m.Groups[1].Value, "Concept");
        // 中文冒号「A：B」→ A 是概念，B 是内容（两者都作为实体候选，关系 related_to）
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "([\\p{L}\\p{N}]{2,20})[：:]([\\p{L}\\p{N}]{2,40})"))
        {
            var a = m.Groups[1].Value.Trim();
            var b = m.Groups[2].Value.Trim();
            if (a.Length >= 2 && b.Length >= 2 && !names.Contains(a))
                Add(a, "Concept");
            Add(b, "Concept");
        }
        // 英文专有名词（连续大写开头词，如「PostgreSQL」「Apache AGE」）
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "[A-Z][A-Za-z0-9]+(?:\\s+[A-Z][A-Za-z0-9]+){0,2}"))
        {
            var name = m.Value.Trim();
            if (name.Length >= 3 && name.Length <= 40 && !name.Split(' ').Any(w => w.Length <= 1)) Add(name, "Concept");
        }
        // 关系抽取：命中「A 负责 B」「A 使用 B」「A 基于 B」等动词句式
        var patterns = new (string Verby, string Rel)[]
        {
            ("负责", "负责"), ("基于", "基于"), ("使用", "使用"), ("依赖", "依赖"),
            ("属于", "属于"), ("属于", "属于"), ("位于", "位于"), ("支持", "支持"),
        };
        var edges = new List<GraphExtractedEdge>();
        foreach (var (verb, rel) in patterns)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                text, $"([\\p{{L}}\\p{{N}}]{{2,20}})\\s*{verb}\\s*([\\p{{L}}\\p{{N}}]{{2,40}})"))
            {
                var a = m.Groups[1].Value.Trim();
                var b = m.Groups[2].Value.Trim();
                if (a.Length < 2 || b.Length < 2 || string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) continue;
                Add(a, "Concept");
                Add(b, "Concept");
                edges.Add(new GraphExtractedEdge(a, rel, b));
            }
        }
        // 无实体时不产边
        if (entities.Count == 0) return new GraphExtraction([], []);
        return new GraphExtraction(entities, edges);
    }
}
