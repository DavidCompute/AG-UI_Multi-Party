using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 为「路由器」数字员工生成/优化其<b>下一层任务指派指引</b>（组织架构里“只看下一层”的指派人设）。
/// 输入：本角色自身的职责 + 其直接下级（<c>AssignmentIds</c>）的昵称/职责；
/// 输出：一段可追加到 <c>Instructions</c> 的指派指引，告诉它在收到不属于自己的请求时，如何依据
/// <b>直接下级</b>判断该派给谁（不向上钻、不引入更深层叶子）——让「多下级指派」选得更准。
/// Provider=mock 时输出确定性模板（无模型调用），便于本地演示与测试。
/// </summary>
public static class AgentAssignmentPromptOptimizer
{
    /// <summary>生成下一层指派指引文本。真实模型走 OpenAI 兼容接口；mock 走模板。</summary>
    public static async Task<string> GenerateAsync(
        AgentOptions options,
        AgentDefinition self,
        IReadOnlyList<AgentDefinition> subordinates,
        ILogger logger,
        CancellationToken ct)
    {
        var subs = (subordinates ?? []).Where(s => s is not null).ToList();
        if (subs.Count == 0)
            throw new InvalidOperationException("该数字员工没有配置下一层下属（AssignmentIds），无法生成指派指引");

        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            return BuildTemplate(self, subs);

        IChatClient client;
        try
        {
            var isDeepSeek = string.Equals(options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            client = AgentCatalog.BuildOpenAIChatClient(
                options, new AgentDefinition { AgentId = "assign_opt", Nickname = "任务指派优化器" }, isDeepSeek).AsIChatClient();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("指派指引生成需要可用的模型配置（" + ex.Message + "）", ex);
        }

        try
        {
            var subLines = string.Join("\n", subs.Select((s, i) =>
                $"{i + 1}. {s.Nickname ?? s.AgentId}（ID: {s.AgentId}）——{s.Description ?? "（无简介）"}"));
            var selfScope = (self.Instructions ?? "").Length > 200 ? self.Instructions![..200] + "…" : self.Instructions;
            var prompt =
                "你是任务指派优化器。为下列「路由器」数字员工生成一段「下一层任务指派指引」，供追加到其系统提示词（Instructions）中。\n" +
                "规则：\n" +
                "- 该角色只依据<b>自己直接下一层</b>的下属判断是否指派与派给谁（不向上钻、不向下看更深层）；\n" +
                "- 收到不属于自己职责的请求时，若能从直接下级匹配到合适对象 → 指派给该下级；有多名下属应说明各自适用情形；\n" +
                "- 若无合适下级 → 不指派（NONE），由上层/本角色继续处理；\n" +
                "- 输出为一段可追加的固定指引文案，用「你」第二人称，200 字以内，只输出正文，不含标题与解释。\n\n" +
                $"本角色：{self.Nickname ?? self.AgentId}（ID: {self.AgentId}）——{self.Description ?? "（无简介）"}；职责概述：{selfScope}\n\n" +
                $"其直接下一层下属：\n{subLines}";

            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var text = resp.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("指派指引返回为空，请重试");
            logger.LogInformation("已为智能体 {AgentId} 生成下一层指派指引（{Chars} 字，{Subs} 名下属）",
                self.AgentId, text.Length, subs.Count);
            return text;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>mock 模式的确定性模板：逐一下级列明适用场景。</summary>
    private static string BuildTemplate(AgentDefinition self, IReadOnlyList<AgentDefinition> subs)
    {
        var lines = subs.Select((s, i) =>
            $"{i + 1}. 涉及「{s.Nickname ?? s.AgentId}」职责（{s.Description ?? "相关领域"}）的请求 → 指派给 {s.Nickname ?? s.AgentId}；").ToList();
        return "## 下一层任务指派指引\n" +
               "你是本组织的一个路由节点。收到不属于你职责的请求时，请<b>只依据你的直接下一层下属</b>判断该派给谁（不向上钻、不向下看更深层）：\n" +
               string.Join("\n", lines) +
               "\n若请求不匹配任何直接下属 → 不指派（交给上层或说明无法处理）。";
    }
}
