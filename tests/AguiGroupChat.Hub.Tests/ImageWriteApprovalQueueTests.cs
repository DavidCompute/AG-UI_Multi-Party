using AguiGroupChat.Web;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>图库写入审批队列（1.0.279）：入队 / 列表 / 取出，以及条数与每人上限。</summary>
public sealed class ImageWriteApprovalQueueTests
{
    private static PendingImageWrite? Add(ImageWriteApprovalQueue q, string owner, out string? error,
        string lib = "img_1", int bytes = 4)
        => q.Add(owner, "skill_x", "agent_x", lib, "a.png", "image/png", new byte[bytes], "cap", null, out error);

    [Fact]
    public void AddListTake_Works()
    {
        var q = new ImageWriteApprovalQueue();
        var a = Add(q, "user_1", out var e1);
        var b = Add(q, "user_2", out var e2);
        Assert.Null(e1);
        Assert.Null(e2);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(2, q.Count);
        Assert.Equal(2, q.List().Count);

        Assert.True(q.TryTake(a!.Id, out var taken));
        Assert.Equal(a.Id, taken!.Id);
        Assert.Equal(1, q.Count);
        Assert.False(q.TryTake("iwp_bogus", out _));
    }

    [Fact]
    public void PerOwnerCap_IsEnforced()
    {
        var q = new ImageWriteApprovalQueue();
        for (var i = 0; i < ImageWriteApprovalQueue.MaxPerOwner; i++)
            Assert.NotNull(Add(q, "user_spam", out _));

        var rejected = Add(q, "user_spam", out var error);
        Assert.Null(rejected);
        Assert.Contains("上限", error);

        // 其他用户不受影响
        Assert.NotNull(Add(q, "user_other", out _));
    }

    [Fact]
    public void GlobalCap_IsEnforced()
    {
        var q = new ImageWriteApprovalQueue();
        // 每个用户最多 20，故用 3 个用户填满 50
        var added = 0;
        for (var u = 0; u < 3 && added < ImageWriteApprovalQueue.MaxItems; u++)
            for (var i = 0; i < ImageWriteApprovalQueue.MaxPerOwner && added < ImageWriteApprovalQueue.MaxItems; i++)
            {
                Assert.NotNull(Add(q, "user_" + u, out _));
                added++;
            }
        Assert.Equal(ImageWriteApprovalQueue.MaxItems, q.Count);
        Assert.Null(Add(q, "user_new", out var error));
        Assert.Contains("上限", error);
    }
}
