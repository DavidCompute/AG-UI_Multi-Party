namespace AguiGroupChat.Agents;

/// <summary>
/// 把 <see cref="AguiGroupChat.Hub.Models.AgentTriggerMode"/> 在<b>线上（HTTP DTO / 前端 select）</b>的名字
/// 统一为<b>小驼峰</b>（与前端 <c>&lt;option value&gt;</c> / 协议写法一致）：
/// <c>Mentioned→mentioned</c>、<c>AllMessages→allMessages</c>、<c>Keyword→keyword</c>、<c>Contextual→contextual</c>。
/// 不要用 <c>enum.ToString().ToLowerInvariant()</c>——那会把 <c>AllMessages</c> 变成 <c>allmessages</c>，
/// 与前端 value <c>allMessages</c> 不匹配，导致下拉回显空白。
/// 入向解析不必走本类：<c>Enum.TryParse(..., ignoreCase:true)</c> 对两种写法都稳健。
/// </summary>
public static class TriggerModeWire
{
    public static string ToWire(AguiGroupChat.Hub.Models.AgentTriggerMode mode)
    {
        var name = mode.ToString();
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }
}
