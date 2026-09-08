namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// 单次语义检索的「按记忆拟人类型」覆盖参数（读取/抽取侧）。任一成员为 <c>null</c> 时该维度沿用全局
/// <see cref="AguiGroupChat.Agents.MemoryOptions"/> 默认值；所有成员为空 = 等价于不覆盖。
/// 由 <c>MemoryContextProvider</c> 按数字员工的记忆类型解析后传给检索实现
/// （<c>AgentMessageMemory.SearchAsync / SearchPersonAsync</c> → store.Search / SearchPerson），
/// 使「广记型多取、深记型宁缺毋滥、提示才想得起临时放宽」等拟人差异真正作用到 store 层的 topK / 阈值。
/// </summary>
public sealed class MemoryRetrievalTuning
{
    /// <summary>群记忆检索条数上限（store.Search 的 topK）。</summary>
    public int? TopK { get; set; }

    /// <summary>群记忆检索最小相似度阈值（0..1，低于则不入返回集）。</summary>
    public double? MinScore { get; set; }

    /// <summary>个人记忆检索条数上限（store.SearchPerson 的 topK）。</summary>
    public int? PersonalTopK { get; set; }

    /// <summary>个人记忆检索最小相似度阈值（0..1）。</summary>
    public double? PersonalMinScore { get; set; }
}
