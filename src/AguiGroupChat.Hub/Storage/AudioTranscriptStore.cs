using System.Collections.Concurrent;
using System.Text.Json;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Hub.Storage;

/// <summary>
/// 语音转写文本存储（多模态进阶 · 语音问答）：把 ASR 转写结果按 <c>attachmentId</c> 存起来，
/// 供前端在语音消息下方展示「数字员工听到的是什么」——否则用户只看到一段录音与一条回复，无法核对。
///
/// <para>
/// 定位：<b>便利信息，非权威数据</b>（丢了只是少一个展示入口，不影响转写注入模型上下文）。
/// 经持久化扩展区 <c>audioTranscripts</c> 跨重启保持（memory 模式写 JSON 快照，数据库模式落 agui_sections），
/// 因此重启 / 多副本（共享存储）下仍可查。上限 <see cref="Max"/> 条，超出清空重建，避免无界增长。
/// </para>
/// </summary>
public sealed class AudioTranscriptStore
{
    private const int Max = 2000;
    private readonly ConcurrentDictionary<string, string> _map = new(StringComparer.Ordinal);
    private readonly ChangeHub? _changes;

    /// <summary><paramref name="changes"/> 可为 null（未注册变更中心时仍可用，只是不即时触发落盘）。</summary>
    public AudioTranscriptStore(ChangeHub? changes = null) => _changes = changes;

    /// <summary>记录一次转写结果（空白 attachmentId / 文本忽略）。</summary>
    public void Set(string attachmentId, string text)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) || string.IsNullOrWhiteSpace(text)) return;
        if (_map.Count >= Max) _map.Clear();
        _map[attachmentId] = text.Trim();
        _changes?.Notify(); // 驱动持久化落盘（与 bridgeCursors 同机制）
    }

    /// <summary>取某附件的转写文本（不存在返回 false）。</summary>
    public bool TryGet(string attachmentId, out string text) => _map.TryGetValue(attachmentId, out text!);

    /// <summary>清空（测试 / 系统初始化 / 账号擦除用）。</summary>
    public void Clear()
    {
        _map.Clear();
        _changes?.Notify();
    }

    /// <summary>持久化扩展区 snapshot 回调（落盘内容）。</summary>
    internal object Snapshot() => new Dictionary<string, string>(_map);

    /// <summary>持久化扩展区 restore 回调（启动时恢复）。</summary>
    internal void Restore(JsonElement element)
    {
        _map.Clear();
        var restored = element.Deserialize<Dictionary<string, string>>(AguiJson.Options) ?? [];
        foreach (var kv in restored)
            if (_map.Count < Max)
                _map[kv.Key] = kv.Value;
    }
}
