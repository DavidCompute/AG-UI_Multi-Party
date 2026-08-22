using System.Text.Json;

namespace AguiGroupChat.Hub.Persistence;

/// <summary>
/// 扩展区（如 Web 层注册的智能体定义目录）的持久化抽象。
/// memory 模式由 <see cref="PersistenceService"/> 的 JSON 快照承担；
/// postgres 模式由 <see cref="Postgres.PostgresSectionStore"/> 落库到 agui_sections 表。
/// </summary>
public interface ISectionStore
{
    /// <summary>注册扩展区读写回调（须在 LoadSections 之前调用）。</summary>
    void AddSection(string name, Func<object?> snapshot, Action<JsonElement> restore);

    /// <summary>从存储恢复全部已注册扩展区（启动时调用，读库 → restore）。</summary>
    void LoadSections();

    /// <summary>把全部已注册扩展区的当前快照写入存储（变更后 / 关闭时调用）。</summary>
    void Flush();
}
