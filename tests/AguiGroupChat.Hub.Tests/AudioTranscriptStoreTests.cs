using AguiGroupChat.Hub.Storage;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 语音转写文本缓存（多模态进阶 · 语音问答）：把 ASR 结果按 attachmentId 存起来供前端展示。
/// 进程内便利信息，覆盖读写 / 忽略空白 / 覆盖旧值 / 清空。
/// </summary>
public sealed class AudioTranscriptStoreTests
{
    [Fact]
    public void SetThenTryGet_RoundTrips()
    {
        var store = new AudioTranscriptStore();
        store.Set("att_1", "你好世界");
        Assert.True(store.TryGet("att_1", out var text));
        Assert.Equal("你好世界", text);
    }

    [Fact]
    public void Set_TrimsAndOverwrites()
    {
        var store = new AudioTranscriptStore();
        store.Set("att_1", "  第一版  ");
        store.Set("att_1", "第二版");
        Assert.True(store.TryGet("att_1", out var text));
        Assert.Equal("第二版", text);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("att_1", "")]
    [InlineData("att_1", "   ")]
    public void Set_IgnoresBlankArgs(string id, string text)
    {
        var store = new AudioTranscriptStore();
        store.Set(id, text);
        Assert.False(store.TryGet(id, out _));
    }

    [Fact]
    public void TryGet_Missing_ReturnsFalse()
    {
        var store = new AudioTranscriptStore();
        Assert.False(store.TryGet("att_nope", out _));
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var store = new AudioTranscriptStore();
        store.Set("att_1", "a");
        store.Set("att_2", "b");
        store.Clear();
        Assert.False(store.TryGet("att_1", out _));
        Assert.False(store.TryGet("att_2", out _));
    }

    [Fact]
    public void SnapshotAndRestore_RoundTrips()
    {
        var store = new AudioTranscriptStore();
        store.Set("att_1", "你好世界");
        store.Set("att_2", "hello");

        var json = System.Text.Json.JsonSerializer.Serialize(store.Snapshot());
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var restored = new AudioTranscriptStore();
        restored.Restore(doc.RootElement.Clone());
        Assert.True(restored.TryGet("att_1", out var a));
        Assert.Equal("你好世界", a);
        Assert.True(restored.TryGet("att_2", out var b));
        Assert.Equal("hello", b);
    }
}
