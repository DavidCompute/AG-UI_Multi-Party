using System.Text.RegularExpressions;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 技能产物标记（<c>produce_file</c>）的统一处理：从结果文本里解析出产物文件并入库为附件。
///
/// <para>
/// 为什么收口成一处：有**两条路**都要做这件事 —— 聊天路径（网关把产物登记为附件挂到消息上，用户才有下载卡片）
/// 与**技能库试运行**路径（产物直接回给界面预览）。以前只有聊天路径有这套（扩展名白名单 / 空文件 /
/// 产物上限 / 入库），于是界面上手动试运行一份内置文档技能只能看到一段文本，
/// 产出的稿子既看不到也拿不到。共用同一实现，才不会一边修好另一边漂移。
/// </para>
/// </summary>
public static class ProducedFileMarker
{
    /// <summary>产物标记的键名。</summary>
    public const string Key = "produce_file";

    /// <summary>
    /// 从正文里抽出全部 <c>produce_file</c> 对象 JSON（可能给出多个候选写法，由调用方按解析结果去重）。
    ///
    /// <para>
    /// 不用单一正则搞定：正文里的引号常被 JSON 转义（<c>\"produce_file\"</c>）且对象内部还有嵌套花括号，
    /// 正则很容易误判。这里分两步：先定位标记，再做花括号配对截取，最后交 JsonDocument 严格解析。
    /// </para>
    /// </summary>
    public static IEnumerable<string> ExtractObjects(string content)
    {
        if (string.IsNullOrEmpty(content)) yield break;
        var key = Key;
        var idx = 0;
        while (true)
        {
            var at = content.IndexOf(key, idx, StringComparison.Ordinal);
            if (at < 0) yield break;
            idx = at + key.Length;

            // 从标记往后找第一个 '{'（中间可能隔着 ": 、转义反斜杠等）
            var open = content.IndexOf('{', idx);
            if (open < 0) yield break;
            // 标记与 '{' 之间不该出现另一个 key（防跨对象误接）
            if (content.IndexOf(key, idx, StringComparison.Ordinal) is var nk && nk >= 0 && nk < open) continue;

            // 花括号配对。关键：转义的引号也算引号边界 —— 否则同一份内容里
            // 真实引号（如开头的 {"ok"）会开启字符串态、后续 \u0022 又无法关闭它，
            // 使结尾的 } 被当成字符串内容而配不出对象（本仓库真实踩到）。
            var depth = 0;
            var inStr = false;
            var end = -1;
            for (var i = open; i < content.Length; i++)
            {
                var c = content[i];
                if (c == '\\')
                {
                    // \" 与 \u0022 都代表一个引号字符：成对翻转字符串态
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        inStr = !inStr;
                        i++;
                        continue;
                    }
                    if (i + 5 < content.Length && (content[i + 1] is 'u' or 'U')
                        && content.Substring(i + 2, 4) == "0022")
                    {
                        inStr = !inStr;
                        i += 5;
                        continue;
                    }
                    i++; // 其他转义（\n、\\ 等）成对跳过，不参与结构判定
                    continue;
                }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }
            if (end < 0) yield break;

            var obj = content.Substring(open, end - open + 1);
            idx = end + 1;
            // 正文里的对象常被整体转义，两种写法都要试，由调用方去重：
            //   - 反斜杠引号（"）：JSON 字符串里内嵌 JSON 的常见形式；
            //   - \u0022 等 Unicode 转义：.NET JsonSerializer 序列化字符串时的默认输出
            //     （写转义器把引号转成 \u0022），技能工具返回即属此类。
            // 不做这一步时，花括号能配对但内部引号不是真引号，JsonDocument 解析必失败 —— 标记就丢了。
            yield return obj;
            var unescaped = obj.Replace("\\\"", "\"");
            if (!string.Equals(unescaped, obj, StringComparison.Ordinal)) yield return unescaped;
            var decoded = Regex.Replace(obj, @"\\u([0-9a-fA-F]{4})",
                m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            if (!string.Equals(decoded, obj, StringComparison.Ordinal)
                && !string.Equals(decoded, unescaped, StringComparison.Ordinal)) yield return decoded;
        }
    }

    /// <summary>
    /// 扫描 <paramref name="content"/> 里的产物标记，把**真实存在且合规**的文件入库为附件（同一路径只挂一次）。
    ///
    /// <para>单个文件失败不影响其余，也不抛异常：产物回档是增强，不该拖垮主流程。</para>
    /// </summary>
    public static IReadOnlyList<AttachmentInfo> SaveAll(string? content, AttachmentStore store, ILogger logger)
    {
        if (string.IsNullOrEmpty(content)) return [];
        var saved = new List<AttachmentInfo>();
        var done = new HashSet<string>(StringComparer.Ordinal); // 同一路径只挂一次（原样/还原多份候选会重复命中）
        foreach (var json in ExtractObjects(content))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("path", out var pEl) || pEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var path = pEl.GetString();
                if (string.IsNullOrWhiteSpace(path) || !done.Add(path)) continue;

                // 读文件 → 白名单 → 尺寸校验 → 入库（AttachmentStore 内部会按白名单净化文件名）
                if (!File.Exists(path)) { logger.LogDebug("produce_file 路径不存在：{Path}", path); continue; }
                // 扩展名白名单：技能（尤其 LLM 生成的）可能写出 .html/.svg 等可内联渲染文件，
                // 直接入库会成为存储型 XSS 载体。上传端点有这道闸，回档路径同样必须有。
                if (!AttachmentStore.IsAllowedUploadExtension(path))
                {
                    logger.LogWarning("produce_file 扩展名不在允许下载白名单，已跳过：{Path}", path);
                    continue;
                }
                var fi = new FileInfo(path);
                if (fi.Length <= 0)
                {
                    logger.LogWarning("produce_file 是空文件，未回档：{Path}", path);
                    continue;
                }
                // 产物**不是**用户上传件，所以用更宽的上限（带插图的稿子天然大）。
                // 超限必须**报出来**：以前这里是 Debug 级 + 静默 continue，
                // 于是“回复里说有文件、对话里却没有下载卡片”，线上完全查不到原因（实测踩到 21MB / 31MB 两份 PPT）。
                if (fi.Length > AttachmentStore.MaxProducedFileBytes)
                {
                    logger.LogWarning("produce_file 超过产物上限（{Bytes} 字节 > {Max} MB），未回档：{Path}",
                        fi.Length, AttachmentStore.MaxProducedFileBytes / 1024 / 1024, path);
                    continue;
                }
                var name = fi.Name;
                AttachmentInfo info;
                using (var fs = File.OpenRead(path))
                    info = store.Save(name, GuessContentType(name), fs, fi.Length, AttachmentStore.MaxProducedFileBytes);
                logger.LogInformation("技能产物入库为附件：{Att}（{Name}，{Bytes} 字节）", info.AttachmentId, name, fi.Length);
                saved.Add(info);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "produce_file 处理失败（已忽略）");
            }
        }
        return saved;
    }

    /// <summary>按扩展名猜测 MIME（仅覆盖常见可下载类型，其余走默认）。</summary>
    public static string GuessContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".pdf" => "application/pdf",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }
}
