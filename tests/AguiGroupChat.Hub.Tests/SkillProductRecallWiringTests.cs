using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能产物回档的<b>调用点完整性</b>守卫。
///
/// <para>
/// 背景（真实缺陷）：网关里有两条技能执行路径 —— 模型 function calling 的正常流式路径，
/// 与「编排计划」直接调 <c>_catalog.RunSkillAsync</c> 的路径。后者早先<b>没有</b>接产物回档，
/// 于是「内容负责人在编排计划里调了 docx 技能、文件确实生成了，聊天里却拿不到下载」。
/// 这类缺陷的特点是：技能执行成功、日志无异常，只是产物静默丢失，很难靠跑通发现。
/// </para>
///
/// <para>
/// 这里用源码扫描把「每条执行路径都必须回档」固定下来：新增技能执行路径却忘记接回档时，测试失败。
/// 比构造沉重的集成环境更直接，也不会随重构失效（只要方法名不变）。
/// </para>
/// </summary>
public sealed class SkillProductRecallWiringTests
{
    private static string GatewaySource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AguiGroupChat.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "AguiGroupChat.Agents", "AgentGateway.cs");
        Assert.True(File.Exists(path), "找不到 AgentGateway.cs：" + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void EveryRunSkillAsyncCallSite_IsPairedWithProductRecall()
    {
        var src = GatewaySource();

        // 所有直接执行技能的调用点（编排行 + 正常路径）
        var callSites = CountOccurrences(src, "_catalog.RunSkillAsync(");
        // 所有把技能产物登记为附件的地方
        var recallSites = CountOccurrences(src, "AttachSkillProducedFilesAsync(");
        // 其中一个 recallSites 是方法定义本身
        var recallInvocationSites = CountOccurrences(src, "await AttachSkillProducedFilesAsync(");

        Assert.True(callSites > 0, "未找到任何技能执行调用点，测试可能失效了");
        Assert.True(recallInvocationSites >= callSites,
            $"技能执行调用点 {callSites} 个，但产物回档调用只有 {recallInvocationSites} 处 —— "
            + "新增了执行路径却忘记回档，产物会静默丢失（这正是编排计划路径曾犯的错）。");
        Assert.Contains("private async Task<int> AttachSkillProducedFilesAsync", src);
    }

    [Fact]
    public void OrchestrationSkillStep_RecallsProducts()
    {
        // 编排计划执行服务端技能的那一步，必须紧跟一次产物回档
        var src = GatewaySource();
        var step = src.IndexOf("编排计划激活技能", StringComparison.Ordinal);
        Assert.True(step >= 0, "未找到编排计划技能执行日志点");

        // 该日志点之后的合理窗口内应有回档调用（同一分支内）
        var window = src.Substring(step, Math.Min(900, src.Length - step));
        Assert.Contains("AttachSkillProducedFilesAsync(", window);
    }

    [Fact]
    public void RecallPath_ChecksExtensionWhitelist()
    {
        // 回档路径必须自带白名单校验（Save 本身不校验，上传端点的闸门到不了这里）
        var src = GatewaySource();
        var at = src.IndexOf("private async Task<int> AttachSkillProducedFilesAsync", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var body = src.Substring(at, Math.Min(3000, src.Length - at));
        Assert.Contains("IsAllowedUploadExtension", body);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
    }
}
