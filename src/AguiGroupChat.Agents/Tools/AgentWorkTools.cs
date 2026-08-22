using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// 工作型智能体的文件 / 命令工具（AgentDefinition.EnableWorkTools 开启时挂载）：
///   - list_dir：列出工作区目录（只读，免审批）
///   - read_file：读取工作区内文本文件（只读，免审批）
///   - write_file：写 / 追加工作区内文件（写操作，需 HITL 审批，由 ApprovalRequiredAIFunction 包装）
///   - shell：在工作区内执行白名单命令（只读命令免审批；写 / 删除 / 网络等命令需审批）
/// 所有路径经 <see cref="AgentWorkSpace"/> 规约到工作区内（越界拒绝）；命令执行有长度 / 时间 / 输出上限。
/// 任一错误返回文本，不影响智能体主流程。
/// </summary>
public sealed class AgentWorkTools : IDisposable
{
    private const int MaxOutputChars = 8000;          // 单次工具返回的最大输出（防撑爆上下文）
    private const int MaxCommandLength = 500;          // 单条命令最长
    private const int MaxPathLength = 300;             // 文件 / 目录路径最长
    private const int ProcessTimeoutMs = 30_000;       // 单条命令最长执行时间
    private readonly SemaphoreSlim _shellLock = new(1, 1); // 串行化命令执行（防并发写文件竞态）

    private readonly AgentWorkSpace _space;
    private readonly Lazy<AguiGroupChat.Hub.Storage.AttachmentStore?> _attachments; // 产物发布：把工作文件存为群可下载附件
    private readonly ILogger _logger;

    public AgentWorkTools(AgentWorkSpace space, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _space = space;
        _attachments = new(() => services.GetService(typeof(AguiGroupChat.Hub.Storage.AttachmentStore)) as AguiGroupChat.Hub.Storage.AttachmentStore);
        _logger = loggerFactory.CreateLogger<AgentWorkTools>();
    }

    public void Dispose() => _shellLock.Dispose();

    /// <summary>列出工作区目录（相对路径留空 = 根目录）。只读，免审批。</summary>
    public string ListDir(string? relPath = null)
    {
        // 空 = 根目录；否则解析到工作区内（越界拒绝）
        string dir;
        if (string.IsNullOrWhiteSpace(relPath))
        {
            dir = _space.EnsureRoot();
        }
        else
        {
            var resolved = ResolveDir(relPath);
            if (resolved is null) return "路径越界：只能访问你的工作区目录。";
            dir = resolved;
        }
        try
        {
            var sb = new StringBuilder();
            foreach (var sub in Directory.GetDirectories(dir))
                sb.AppendLine($"[目录] {Path.GetFileName(sub)}/");
            foreach (var file in Directory.GetFiles(dir))
            {
                var info = new FileInfo(file);
                sb.AppendLine($"{pathDisplay(relPath, file)}  ({info.Length} B)");
            }
            return sb.Length == 0 ? "（空目录）" : sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "list_dir 工具执行失败：{Path}", relPath);
            return "列目录失败：" + ex.Message;
        }
    }

    /// <summary>读取工作区内文本文件（相对路径）。只读，免审批。</summary>
    public string ReadFile(string path)
    {
        var file = ResolveFile(path, mustExist: true);
        if (file is null) return "路径越界或文件不存在：只能访问你工作区内的文件。";
        try
        {
            var content = File.ReadAllText(file, Encoding.UTF8);
            return string.IsNullOrEmpty(content) ? "（空文件）" : Truncate(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "read_file 工具执行失败：{Path}", path);
            return "读取失败：" + ex.Message;
        }
    }

    /// <summary>写 / 追加工作区内文件（相对路径）。写操作，经审批后执行。</summary>
    public string WriteFile(string path, string content, bool append = false)
    {
        var file = ResolveFile(path, mustExist: false);
        if (file is null) return "路径越界：只能写你工作区内的文件。";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); // 创建缺失父目录
            if (append && File.Exists(file))
            {
                File.AppendAllText(file, content, Encoding.UTF8);
                return $"已追加到 {path}（新增 {Encoding.UTF8.GetByteCount(content)} 字节）。";
            }
            File.WriteAllText(file, content, Encoding.UTF8);
            return $"已写入 {path}（{Encoding.UTF8.GetByteCount(content)} 字节）。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "write_file 工具执行失败：{Path}", path);
            return "写入失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 把工作区内文件发布为群可下载附件（产物回传）。返回 JSON 标记，网关据此把附件追加到智能体消息并广播
    /// TEXT_MESSAGE_ATTACHMENTS（前端渲染可下载附件卡片）；需用户审批（把工作产物分享到群 = 内容对外可见）。
    /// </summary>
    public string PublishFile(string path)
    {
        var file = ResolveFile(path, mustExist: true);
        if (file is null) return "文件不存在或路径越界：只能发布你工作区内的文件。";
        var attachments = _attachments.Value;
        if (attachments is null) return "附件服务未启用，无法发布产物。";
        try
        {
            using var stream = File.OpenRead(file);
            var size = new FileInfo(file).Length;
            if (size > AttachmentStore.MaxFileBytes)
                return $"文件过大（{size} 字节），超过单附件 {AttachmentStore.MaxFileBytes / 1024 / 1024}MB 上限。";
            // 附件扩展名白名单校验：产物若不是可分享类型（脚本/可执行等被上传白名单排除），提示导出受限
            var fileName = Path.GetFileName(file);
            if (!AttachmentStore.IsAllowedUploadExtension(fileName))
                return $"文件类型「{Path.GetExtension(fileName)}」不在可分享白名单内（防 XSS / 可执行文件）。你可以先重命名或转成 .md/.txt/.pdf 等再发布。";
            var meta = attachments.Save(fileName, GuessContentType(fileName), stream, size);
            // JSON 标记：网关在 FunctionResultContent 中识别并追加附件到智能体消息（带 PUBLISH_FILE 前缀避免误判）
            return $"PUBLISH_FILE:{System.Text.Json.JsonSerializer.Serialize(meta)}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "publish_file 工具执行失败：{Path}", path);
            return "发布失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 抓取 URL 网页正文并保存为工作区内 Markdown 文件（采集 → 落盘 → 后续可 read/publish 处理），
    /// 供「把这篇文档抓下来整理」类任务。含 SSRF 防护（拒绝本机 / 内网地址、手动逐跳重定向校验）。
    /// </summary>
    public async Task<string> FetchUrl(string url, string saveAs)
    {
        if (string.IsNullOrWhiteSpace(saveAs)) return "请指定保存文件名（saveAs，工作区内相对路径，建议 .md / .txt）。";
        var target = ResolveFile(saveAs, mustExist: false);
        if (target is null) return "保存路径越界：只能写你工作区内的文件。";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "仅支持 http/https 链接。";
        if (IsPrivateOrLoopback(uri)) return "出于安全考虑，拒绝访问本机 / 内网地址。";
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AguiGroupChat-Agent/1.0");
            // 手动逐跳重定向（最多 5 跳），每跳重新做 scheme + 私网校验（防 302 绕过 SSRF）
            var current = uri;
            string raw;
            for (var hop = 0; ; hop++)
            {
                using var resp = await http.GetAsync(current);
                var location = resp.Headers.Location;
                if ((int)resp.StatusCode is >= 300 and < 400 && location is not null)
                {
                    if (hop >= 5) return "重定向过多（超过 5 跳），已放弃。";
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) return "仅支持 http/https。";
                    if (IsPrivateOrLoopback(next)) return "重定向目标为本机 / 内网地址，已拒绝。";
                    current = next;
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                raw = await resp.Content.ReadAsStringAsync();
                break;
            }
            // 提取标题 + 正文文本（HTML→文本，忽略脚本/样式）
            var text = HtmlToText(raw);
            if (string.IsNullOrWhiteSpace(text)) return "未能从页面提取到正文内容。";
            text = Truncate(text, 40_000); // 单文件最大 40KB，避免撑爆
            var md = $"# {uri.Host}\n\n> 来源：{uri}\n> 抓取时间：{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC\n\n{text}\n";
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, md, Encoding.UTF8);
            return $"已抓取并保存到 {saveAs}（{Encoding.UTF8.GetByteCount(md)} 字节）。内容已按纯文本整理，可用 read_file 查看或进一步编辑。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fetch_url 工具执行失败：{Url}", url);
            return "抓取失败：" + ex.Message;
        }
    }

    /// <summary>工作区内复制文件（安全封装，免 shell 转义）。需审批（写操作）。</summary>
    public string CopyFile(string source, string target)
    {
        var src = ResolveFile(source, mustExist: true);
        var dst = ResolveFile(target, mustExist: false);
        if (src is null) return "源文件不存在或越界。";
        if (dst is null) return "目标路径越界。";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            return $"已复制 {source} → {target}。";
        }
        catch (Exception ex) { _logger.LogWarning(ex, "copy_file 失败"); return "复制失败：" + ex.Message; }
    }

    /// <summary>工作区内重命名 / 移动文件（安全封装）。需审批（写操作）。</summary>
    public string RenameFile(string source, string target)
    {
        var src = ResolveFile(source, mustExist: true);
        var dst = ResolveFile(target, mustExist: false);
        if (src is null) return "源文件不存在或越界。";
        if (dst is null) return "目标路径越界。";
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) return "源和目标相同，无需移动。";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Move(src, dst, overwrite: true);
            return $"已重命名/移动 {source} → {target}。";
        }
        catch (Exception ex) { _logger.LogWarning(ex, "rename_file 失败"); return "移动失败：" + ex.Message; }
    }

    /// <summary>把一条备忘写入工作区 NOTES.md（跨对话延续：中间结论 / 待办 / 进度）。需审批（写操作）。</summary>
    public string Remember(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return "备忘内容为空。";
        var notes = _space.ContainsResolve("NOTES.md")!;
        try
        {
            Directory.CreateDirectory(_space.EnsureRoot());
            var line = $"- {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC：{note.Trim()}";
            File.AppendAllText(notes, line + "\n", Encoding.UTF8);
            return $"已记入 NOTES.md（第 {File.ReadAllLines(notes).Length} 行）。跨对话可随时用 read_file 读取 NOTES.md 回忆。";
        }
        catch (Exception ex) { _logger.LogWarning(ex, "remember 失败"); return "备忘失败：" + ex.Message; }
    }

    /// <summary>读取工作区 NOTES.md（跨对话记忆延续）。只读，免审批。</summary>
    public string ReadNotes()
    {
        var notes = _space.ContainsResolve("NOTES.md");
        if (notes is null || !File.Exists(notes)) return "还没有备忘（NOTES.md 不存在）。可用 remember 工具记录工作进度 / 待办。";
        try
        {
            var content = File.ReadAllText(notes, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(content) ? "NOTES.md 为空。" : content;
        }
        catch (Exception ex) { return "读取备忘失败：" + ex.Message; }
    }

    // ================= 批量 / 编排工具（复杂任务用） =================

    /// <summary>递归列出工作区全部文件（含子目录），带相对路径与大小。只读，免审批。</summary>
    public string ListTree(string? relDir = null)
    {
        var dir = string.IsNullOrWhiteSpace(relDir) ? _space.EnsureRoot() : ResolveDir(relDir);
        if (dir is null) return "路径越界：只能查看你工作区内的目录。";
        try
        {
            var rows = new List<string>();
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(_space.Root, file).Replace('\\', '/');
                var info = new FileInfo(file);
                rows.Add($"{rel}  ({info.Length} B)");
            }
            return rows.Count == 0 ? "（工作区暂无文件）" : string.Join("\n", rows);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "list_tree 失败"); return "列目录失败：" + ex.Message; }
    }

    /// <summary>一次读取多个工作区文件（逗号分隔的相对路径），便于模型一次获取若干产物的内容。只读，免审批。</summary>
    public string ReadBatch(string paths)
    {
        var parts = (paths ?? "").Split([',', '，', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0).Take(20).ToList();
        if (parts.Count == 0) return "请用逗号分隔给出要读取的文件相对路径。";
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            var file = ResolveFile(p, mustExist: true);
            if (file is null) { sb.AppendLine($"== {p} =="); sb.AppendLine("（路径越界或不存在）"); continue; }
            sb.AppendLine($"== {p} ==");
            try
            {
                var c = File.ReadAllText(file, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(c)) sb.AppendLine("（空文件）");
                else sb.AppendLine(Truncate(c, Math.Min(MaxOutputChars, 3000)));
            }
            catch (Exception ex) { sb.AppendLine("读取失败：" + ex.Message); }
        }
        return Truncate(sb.ToString().TrimEnd());
    }

    /// <summary>批量重命名 / 迁移：把符合条件的文件（按扩展名 / 前缀 / 包含串）移动到目标目录并可选加后缀。需审批（写）。</summary>
    public string BatchRename(string extension, string sourceDir, string targetDir, string suffix = "")
    {
        var srcDir = string.IsNullOrWhiteSpace(sourceDir) ? _space.EnsureRoot() : ResolveDir(sourceDir);
        var dstResolved = string.IsNullOrWhiteSpace(targetDir) ? _space.EnsureRoot() : ResolveDir(targetDir);
        if (srcDir is null || dstResolved is null) return "源 / 目标目录越界：只能在你的工作区内操作。";
        var ext = (extension ?? "").TrimStart('.');
        if (string.IsNullOrWhiteSpace(ext)) return "请指定扩展名（如 md / txt / json，不含点）。";
        try
        {
            Directory.CreateDirectory(dstResolved);
            var files = Directory.GetFiles(srcDir, "*." + ext, SearchOption.AllDirectories);
            if (files.Length == 0) return $"没有找到 *.{ext} 文件。";
            var moved = 0;
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var target = Path.Combine(dstResolved, name + suffix + "." + ext);
                // 目标在工作区内（ResolveDir 已保证 dstResolved 在根内，Path.Combine 结果仍在根内）
                if (string.Equals(f, target, StringComparison.OrdinalIgnoreCase)) continue;
                File.Move(f, target, overwrite: false);
                moved++;
            }
            return $"已迁移 {moved} 个 *.{ext} 文件到 {targetDir ?? "工作区根"}。";
        }
        catch (Exception ex) { _logger.LogWarning(ex, "batch_rename 失败"); return "批量迁移失败：" + ex.Message; }
    }

    /// <summary>把工作区内的单文件或目录打包为 zip（便于整批归档 / 发布）。需审批（写）。</summary>
    public string Archive(string path, string archiveName)
    {
        var target = ResolveFile(archiveName, mustExist: false);
        if (target is null) return "归档文件名越界：只能写你工作区内的文件。";
        if (string.IsNullOrWhiteSpace(path) || path is "." or "./" or "\\" or "/")
            return "不能归档整个工作区根目录，请指定子目录或文件。";
        var src = _space.ContainsResolve(path.TrimEnd('/')) ?? _space.ContainsResolve(path);
        if (src is null) return "源路径越界或不存在。";
        try
        {
            if (!File.Exists(src) && !Directory.Exists(src)) return "源路径不存在。";
            if (string.Equals(src, _space.Root, StringComparison.Ordinal)
                || string.Equals(src, Path.TrimEndingDirectorySeparator(_space.Root), StringComparison.Ordinal))
                return "不能归档整个工作区根目录，请指定子目录或文件。";
            if (File.Exists(target)) File.Delete(target);
            if (File.Exists(src))
            {
                // 单个文件：写入与文件同名的 zip
                using (var stream = File.Create(target))
                using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(src, Path.GetFileName(src));
                }
            }
            else
            {
                // 目录：含基目录名打包
                System.IO.Compression.ZipFile.CreateFromDirectory(
                    Path.TrimEndingDirectorySeparator(src), target,
                    System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: true);
            }
            var size = new FileInfo(target).Length;
            return $"已打包 {path} → {archiveName}（{size} 字节）。可用 publish_file 发布。";
        }
        catch (Exception ex) { _logger.LogWarning(ex, "archive 失败"); return "打包失败：" + ex.Message; }
    }

    /// <summary>安全删除文件或目录（相对路径）。防根保护：拒绝删除工作区根 / 匹配根。需审批（写）。</summary>
    public string Remove(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "." or "./" or "\\" or "/")
            return "出于安全考虑，不能删除整个工作区根目录。";
        var abs = _space.ContainsResolve(path);
        if (abs is null) return "路径越界：只能删除你工作区内的文件。";
        if (string.Equals(abs, _space.Root, StringComparison.Ordinal) || string.Equals(abs, Path.TrimEndingDirectorySeparator(_space.Root), StringComparison.Ordinal))
            return "出于安全考虑，不能删除整个工作区根目录。";
        try
        {
            if (File.Exists(abs)) { File.Delete(abs); return $"已删除文件 {path}。"; }
            if (Directory.Exists(abs)) { Directory.Delete(abs, recursive: true); return $"已删除目录 {path}（含子内容）。"; }
            return "目标不存在。";
        }
        catch (Exception ex) { _logger.LogWarning(ex, "remove 失败"); return "删除失败：" + ex.Message; }
    }

    /// <summary>写入 / 刷新任务的步骤计划（PLAN.md）：把用换行或逗号分隔的步骤写成带验收勾选的清单。需审批（写）。
    /// 复杂任务先规划再逐步骤执行：每完成一步用 plan_mark 打勾，跨对话用 plan_read 接着干。</summary>
    public string PlanWrite(string title, string steps)
    {
        var plan = _space.ContainsResolve("PLAN.md");
        if (plan is null) return "路径异常：无法写入 PLAN.md。";
        var stepList = (steps ?? "").Split(['\n', '\r', '，', ',', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).Take(30).ToList();
        if (stepList.Count == 0) return "请给出至少一个步骤（用换行或逗号分隔）。";
        try
        {
            Directory.CreateDirectory(_space.EnsureRoot());
            var sb = new StringBuilder();
            sb.AppendLine($"# 任务计划：{title}");
            sb.AppendLine();
            for (var i = 0; i < stepList.Count; i++)
                sb.AppendLine($"- [ ] {i + 1}. {stepList[i]}");
            File.WriteAllText(plan, sb.ToString(), Encoding.UTF8);
            return $"已规划 {stepList.Count} 步，写入 PLAN.md。完成一步用 plan_mark 打勾。";
        }
        catch (Exception ex) { _logger.LogWarning(ex, "plan_write 失败"); return "写入计划失败：" + ex.Message; }
    }

    /// <summary>读取 PLAN.md 步骤计划（含各步完成状态）。只读，免审批。</summary>
    public string PlanRead()
    {
        var plan = _space.ContainsResolve("PLAN.md");
        if (plan is null || !File.Exists(plan)) return "还没有计划（PLAN.md 不存在）。可用 plan_write 为复杂任务先生成步骤计划。";
        try
        {
            var content = File.ReadAllText(plan, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(content) ? "PLAN.md 为空。" : Truncate(content);
        }
        catch (Exception ex) { return "读取计划失败：" + ex.Message; }
    }

    /// <summary>把 PLAN.md 中的某一步标记为完成（打勾）。需审批（写）。参数 step=步骤序号（从 1 开始）。</summary>
    public string PlanMarkDone(int step)
    {
        var plan = _space.ContainsResolve("PLAN.md");
        if (plan is null || !File.Exists(plan)) return "还没有计划（PLAN.md 不存在）。";
        try
        {
            var lines = File.ReadAllLines(plan, Encoding.UTF8).ToList();
            var changed = false;
            for (var i = 0; i < lines.Count; i++)
            {
                if (!lines[i].TrimStart().StartsWith("- [ ] ", StringComparison.Ordinal)) continue;
                var numStr = lines[i].TrimStart()["- [ ] ".Length..];
                var dot = numStr.IndexOf('.');
                if (dot <= 0) continue;
                if (!int.TryParse(numStr[..dot], out var n) || n != step) continue;
                lines[i] = lines[i].Replace("- [ ] ", "- [x] ", StringComparison.Ordinal);
                changed = true;
                break;
            }
            if (!changed) return $"找不到第 {step} 步计划项。";
            File.WriteAllLines(plan, lines, Encoding.UTF8);
            var done = lines.Count(l => l.Contains("- [x]", StringComparison.Ordinal));
            var total = lines.Count(l => l.Contains("- [", StringComparison.Ordinal));
            return $"第 {step} 步已完成（{done}/{total}）。";
        }
        catch (Exception ex) { _logger.LogWarning(ex, "plan_mark 失败"); return "更新计划失败：" + ex.Message; }
    }

    /// <summary>简化 HTML→文本（复用本地实现）：剥脚本/样式注释，标签转为换行，解码实体。</summary>
    private static string HtmlToText(string html)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(html, "(?is)<(script|style|head|title)[^>]*>.*?</\\1>", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, "(?is)<br[^>]*>", "\n");
        s = System.Text.RegularExpressions.Regex.Replace(s, "(?is)</(p|div|h[1-6]|li|tr|blockquote)>", "\n");
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = System.Text.RegularExpressions.Regex.Replace(s, "[ \t]+\n", "\n");
        s = System.Text.RegularExpressions.Regex.Replace(s, "\n{3,}", "\n\n");
        return s.Trim();
    }

    private static bool IsPrivateOrLoopback(Uri uri)
    {
        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var ip = System.Net.IPAddress.TryParse(host, out var parsed)
                ? parsed
                : System.Net.Dns.GetHostAddresses(host).FirstOrDefault();
            if (ip is null) return true;
            if (System.Net.IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes();
            if (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || ip.Equals(System.Net.IPAddress.Any)) return true;
            return false;
        }
        catch { return true; }
    }

    /// <summary>截断到指定长度。</summary>
    private static string Truncate(string s, int limit) => s.Length <= limit ? s : s[..limit] + "\n…（已截断）";

    private static string GuessContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".md" or ".txt" or ".log" or ".csv" or ".json" or ".xml" or ".yml" or ".yaml" => "text/plain; charset=utf-8",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };

    /// <summary>在工作区根目录执行白名单命令。返回 stdout + stderr（截断）。</summary>
    public async Task<string> ShellAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "命令为空。";
        if (command.Length > MaxCommandLength) return $"命令过长（超过 {MaxCommandLength} 字符）。";
        if (!BeShellReady()) return "shell 工具未就绪。";

        // 命令白名单解析：取首词作为执行器命令名，校验在白名单内
        var (exec, args) = SplitCommand(command);
        if (exec is null || !IsAllowedExecutable(exec))
            return $"命令「{exec}」不在白名单内，拒绝执行。允许的命令：{string.Join("、", AllowedCommands)}。";
        // 是否写/危险而需要审批：白名单内分「只读」(可自主) 与「需审批」(写/删除/覆盖)。由调用方(ApprovalRequired 包装)决定审批策略；
        // 此处在工具内再兜底拦截禁用的危险命令（如删除整个工作区 / 伪装系统命令）。
        if (IsForbiddenShell(exec, args)) return "该命令超出工作型智能体安全边界，已拒绝。";

        await _shellLock.WaitAsync();
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exec,
                    Arguments = args,
                    WorkingDirectory = _space.EnsureRoot(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                }
            };
            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var completed = proc.WaitForExit(ProcessTimeoutMs);
            proc.Kill(entireProcessTree: true);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (!completed) return $"命令超时（超过 {ProcessTimeoutMs / 1000} 秒），已终止。";
            var exitCode = proc.ExitCode;
            var sb = new StringBuilder();
            if (stdout.Length > 0) sb.AppendLine(stdout.TrimEnd());
            if (stderr.Length > 0) sb.AppendLine("stderr: " + stderr.TrimEnd());
            sb.AppendLine($"（退出码 {exitCode}）");
            return Truncate(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "shell 工具执行失败：{Command}", command);
            return "命令执行失败：" + ex.Message;
        }
        finally
        {
            _shellLock.Release();
        }
    }

    // ================= 只读 / 需审批命令白名单 =================

    /// <summary>可直接自主执行的只读命令（免审批；stdout 回传供模型决策）。</summary>
    private static readonly string[] ReadOnlyCommands =
        ["ls", "find", "grep", "cat", "head", "tail", "wc", "echo", "pwd", "stat", "du", "df", "tree", "sha256sum", "md5sum", "cksum", "file"];

    /// <summary>写 / 删除等影响命令（需 HITL 审批，由 ApprovalRequiredAIFunction 包装）。</summary>
    private static readonly string[] WriteCommands =
        ["mkdir", "touch", "cp", "mv", "rm", "rmdir", "chmod", "tee", "tar", "zip", "unzip", "sort", "sed", "awk"];

    /// <summary>全部允许的命令（白名单联合）。</summary>
    internal static IEnumerable<string> AllowedCommands
    {
        get
        {
            foreach (var c in ReadOnlyCommands) yield return c;
            foreach (var c in WriteCommands) yield return c;
        }
    }

    /// <summary>命令是否需要审批（写 / 删除等影响性命令，或带管道/重定向/参数注入的组合命令）。</summary>
    internal static bool RequiresApproval(string command)
    {
        var (exec, args) = SplitCommand(command);
        if (exec is null) return true;
        return !IsAllowedExecutable(exec) // 不在白名单 → 一律需审批（但仍会被工具内拒）
            || !ReadOnlyCommands.Contains(exec, StringComparer.Ordinal)
            || ContainsDangerousMeta(args);
    }

    /// <summary>只读命令带上了 shell 元字符（`;` &amp;&amp; `|&gt;` `$()` 等）→ 组合命令，按需审批。</summary>
    private static bool ContainsDangerousMeta(string args)
        => args.Contains(';') || args.Contains('|') || args.Contains('>') || args.Contains('<') || args.Contains('$') || args.Contains('`') || args.Contains('&');

    private bool BeShellReady()
    {
        _space.EnsureRoot(); // 确保工作区存在（命令都在其内执行）
        return true;
    }

    private static (string? Exec, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return (null, "");
        int end;
        if (command[0] == '"' || command[0] == '\'')
        {
            var q = command[0];
            end = command.IndexOf(q, 1);
            if (end < 0) return (null, "");
            return (command[1..end], command[(end + 1)..].TrimStart());
        }
        end = command.IndexOfAny([' ', '\t']);
        return end < 0 ? (command, "") : (command[..end], command[(end + 1)..].TrimStart());
    }

    private static bool IsAllowedExecutable(string exec) => AllowedCommands.Contains(exec, StringComparer.Ordinal);

    private static bool IsForbiddenShell(string exec, string args)
    {
        // 禁止删除 / 移动整个工作区根、递归清空等极端破坏
        if (exec is "rm" or "rmdir")
        {
            var a = args.Trim();
            if (a == "" || a == "-r" || a == "-rf" || a == "-fr" || a == "-R" || a == "-Rf")
                return true; // 对根目录通配的操作
            if (a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(t => t is "." or ".." or "./" or "../"))
                return true;
        }
        return false;
    }

    private string? ResolveDir(string? relPath)
    {
        if (!string.IsNullOrWhiteSpace(relPath) && relPath.Length > MaxPathLength) return null;
        return _space.ContainsResolve(string.IsNullOrWhiteSpace(relPath) ? null : relPath) ?? null;
    }

    private string? ResolveFile(string path, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength) return null;
        var abs = _space.ContainsResolve(path);
        if (abs is null) return null;
        if (mustExist && !File.Exists(abs)) return null;
        return abs;
    }

    private static string pathDisplay(string? relPath, string fullPath) => Path.GetFileName(fullPath);

    private static string Truncate(string s) => s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + $"\n…（已截断，原 {s.Length} 字符）";
}
