using System.Text;

namespace AguiGroupChat.Agents;

/// <summary>
/// 单次智能体运行期间的工具返回收集器。
///
/// <para>
/// 为什么需要它：技能产物（如内置 docx 技能）通过返回体里的 <c>produce_file</c> 标记声明自己落盘的文件，
/// 网关据此把文件登记为可下载附件。但模型在生成最终回答时，<b>经常把技能返回的 JSON 改写成自然语言</b>
/// （“已生成文档，位置 /tmp/x.docx”），标记就此丢失 —— 表现为“技能确实生成了文件，用户却看不到下载入口”。
/// 工具返回是权威、未经模型改写的，因此单独收集，作为产物回档的主来源。
/// </para>
///
/// <para>
/// 用 <see cref="AsyncLocal{T}"/> 而非实例字段：同一次运行内的工具调用与结果处理共享同一异步流，
/// 而网关是单例、可能并发服务多个运行，实例字段会串台。
/// </para>
/// </summary>
public sealed class ToolResultCollector
{
    /// <summary>单个运行累计保留的最大字符数：防长跑运行把工具输出无限堆在内存里（只用于扫标记，无需全文）。</summary>
    private const int MaxTotalChars = 256 * 1024;

    private readonly StringBuilder _buffer = new();

    /// <summary>当前异步流上的收集器（未开启收集时为 null）。</summary>
    public static readonly AsyncLocal<ToolResultCollector?> Ambient = new();

    /// <summary>累计文本（供产物扫描）。</summary>
    public string Text => _buffer.ToString();

    /// <summary>记录一条工具返回；超限后丢弃后续内容（已有内容仍可扫描）。</summary>
    public void Add(string? result)
    {
        if (string.IsNullOrEmpty(result)) return;
        if (_buffer.Length >= MaxTotalChars) return;
        var remaining = MaxTotalChars - _buffer.Length;
        _buffer.Append(result.Length > remaining ? result[..remaining] : result);
        _buffer.Append('\n');
    }
}
