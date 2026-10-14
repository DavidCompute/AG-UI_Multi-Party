using System.Text.Json;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AguiGroupChat.Hub;

/// <summary>
/// 语音转写文本的持久化注册：经扩展区 <c>audioTranscripts</c> 跨重启保持
/// （memory 模式写 JSON 快照，数据库模式落 agui_sections 表；共享存储下多副本亦可查）。
/// 须在应用构建后、状态恢复（InitializePersistence）之前调用（组合根）。
/// </summary>
public static class AudioTranscriptPersistence
{
    public static void RegisterAudioTranscriptPersistence(this IServiceProvider services)
    {
        var store = services.GetService<AudioTranscriptStore>();
        if (store is null) return;
        Func<object?> snapshot = store.Snapshot;
        Action<JsonElement> restore = store.Restore;

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
        {
            persistence.AddSection("audioTranscripts", snapshot, restore);
        }
        else
        {
            services.GetService<ISectionStore>()?.AddSection("audioTranscripts", snapshot, restore);
        }
    }
}
