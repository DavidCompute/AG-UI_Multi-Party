using System.Text.Json;
using System.Text.Json.Serialization;

namespace AguiGroupChat.Agents;

/// <summary>
/// 智能体技能调用链节点（链路可视化）：记录一次技能调用从宿主智能体到目标智能体的映射，
/// 可嵌套（目标智能体内部又调用下游技能 → children）。根节点 = 用户直接提问的宿主智能体。
/// </summary>
public sealed class ChainNode
{
    /// <summary>当前智能体 ID（根 = 用户提问的宿主；子节点 = 被技能调用的目标）。</summary>
    public string AgentId { get; set; } = "";

    /// <summary>当前智能体昵称（用于展示）。</summary>
    public string AgentNickname { get; set; } = "";

    /// <summary>触发进入本节点的技能名（根节点为空串）。</summary>
    public string SkillId { get; set; } = "";

    /// <summary>进入本节点时传入的请求文本（截断）。</summary>
    public string Query { get; set; } = "";

    /// <summary>本智能体的最终答复文本（截断，供前端悬浮/展开查看）。</summary>
    public string Result { get; set; } = "";

    /// <summary>下游技能调用（嵌套层级）。</summary>
    public List<ChainNode> Children { get; set; } = [];

    public bool HasChildren => Children.Count > 0;
}

/// <summary>
/// 技能调用链收集器：经 <c>AsyncLocal</c> 与技能调用同步嵌套，构建从宿主到最深层目标的多跳树。
/// <see cref="AgentSkillCall"/> 进入时 push 子节点；返回时回填 Result 并回退到父节点。
/// </summary>
public sealed class SkillChainBuilder
{
    /// <summary>当前正在执行的「作用域节点」栈底（栈顶 = 当前技能调用所在节点）。</summary>
    private readonly List<ChainNode> _shadow = [];

    /// <summary>根节点（宿主智能体）；null 表示尚未构建。</summary>
    public ChainNode? Root { get; private set; }

    public ChainNode EnsureRoot(string agentId, string nickname)
    {
        if (Root is null)
        {
            Root = new ChainNode { AgentId = agentId, AgentNickname = nickname };
            _shadow.Add(Root);
        }
        else if (_shadow.Count == 0)
        {
            _shadow.Add(Root);
        }
        return Root;
    }

    /// <summary>当前作用域节点（无则根）。</summary>
    public ChainNode Current => _shadow.Count > 0 ? _shadow[^1] : Root!;

    /// <summary>在当前节点下新建一个下游技能调用子节点，并把它设为当前作用域；返回子节点供回填。</summary>
    public ChainNode Push(ChainNode child)
    {
        Current.Children.Add(child);
        _shadow.Add(child);
        return child;
    }

    /// <summary>从当前作用域回退到父节点（技能调用返回时调用）。</summary>
    public void Pop() { if (_shadow.Count > 1) _shadow.RemoveAt(_shadow.Count - 1); }

    /// <summary>导出根节点 JSON（无根返回 null），供消息持久化 / 前端渲染。</summary>
    public string? ToJson()
    {
        if (Root is null) return null;
        return JsonSerializer.Serialize(Root, ChainJsonOptions);
    }

    private static readonly JsonSerializerOptions ChainJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 保留中文（默认会转义为 \uXXXX，前端与测试都不友好）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>当前线程/异步流上的链构造器（技能调用与网关按同一异步流传递）。</summary>
    public static readonly AsyncLocal<SkillChainBuilder?> Ambient = new();
}
