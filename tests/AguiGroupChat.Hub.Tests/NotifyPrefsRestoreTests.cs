using AguiGroupChat.Web;
using System.Text.Json;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>通知偏好恢复（1.0.332 修复）：快照形如 {users:{…}}，不能被当成 userId 逐项解析。</summary>
public sealed class NotifyPrefsRestoreTests
{
    [Fact]
    public void Restore_UnwrapsUsersWrapper()
    {
        const string json = """
            {"users":{"u1":{"toastTypes":["approval"],"muteKeywords":["  Secret  ","secret"],"muteGroups":["g1"]}}}
            """;
        using var doc = JsonDocument.Parse(json);
        var state = new NotifyPrefsState();

        NotifyPrefsApi.Restore(state, doc.RootElement);

        Assert.Single(state.Users);
        var prefs = state.Users["u1"];
        Assert.Equal(["approval"], prefs.ToastTypes!);
        Assert.Equal(["g1"], prefs.MuteGroups!);
        // 关键词：去空白 + 去重
        Assert.Equal(["Secret", "secret"], prefs.MuteKeywords!);
    }

    [Fact]
    public void Restore_AcceptsLegacyBareMap()
    {
        const string json = """{"u1":["approval","error"]}""";
        using var doc = JsonDocument.Parse(json);
        var state = new NotifyPrefsState();

        NotifyPrefsApi.Restore(state, doc.RootElement);

        Assert.Equal(["approval", "error"], state.Users["u1"].ToastTypes!);
    }
}
