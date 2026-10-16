namespace AguiGroupChat.Agents;

/// <summary>
/// 本次回答运行的 token 用量累加器（AsyncLocal）：模型调用处（<see cref="UsageCaptureChatClient"/>）累加，
/// 运行结束时由网关回档到消息，用于「本次回答的用量 / 成本」展示。
/// 最外层运行创建实例；嵌套（子代理 / 交付兜底）沿用同一实例，故总量含全部子调用。
/// </summary>
internal sealed class UsageAccumulator
{
    /// <summary>当前异步作用域内的累加器（最外层运行创建；无运行上下文时为 null）。</summary>
    public static readonly AsyncLocal<UsageAccumulator?> Ambient = new();

    public long InputTokens;
    public long OutputTokens;
    public long ReasoningTokens;

    public void Add(long input, long output, long reasoning)
    {
        if (input > 0) InputTokens += input;
        if (output > 0) OutputTokens += output;
        if (reasoning > 0) ReasoningTokens += reasoning;
    }

    public bool IsEmpty => InputTokens <= 0 && OutputTokens <= 0 && ReasoningTokens <= 0;
}
