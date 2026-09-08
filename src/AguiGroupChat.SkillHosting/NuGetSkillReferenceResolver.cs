using System.Collections.Concurrent;
using System.IO.Compression;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace AguiGroupChat.SkillHosting;

/// <summary>一次 NuGet 引用还原的结果。</summary>
public sealed class NuGetResolveResult
{
    /// <summary>是否成功（Error 为空即成功；引用可能全由宿主自带而为空列表，属正常）。</summary>
    public bool Ok => Error is null;

    /// <summary>还原后应作为 Roslyn MetadataReference 的 .dll（含包依赖传递），已剔除框架自带程序集。</summary>
    public List<string> ReferencePaths { get; } = [];

    /// <summary>失败原因（非 null 表示失败）。</summary>
    public string? Error { get; set; }

    /// <summary>还原过程的简要跟踪（包选择 / 已跳过项），供诊断。</summary>
    public List<string> Log { get; } = [];
}

/// <summary>
/// dotnet 技能 <c>#r "nuget: 包名, 版本"</c> 的程序化还原器：
/// 用 NuGet.Protocol 在运行宿主（服务端容器 / 本机桥所在机器）下载 nupkg 到本地缓存，
/// 做依赖闭包解析（BFS + 版本区间求值 + 就近 TFM 选择），输出编译可用的程序集路径列表。
/// 缓存按 <c>缓存根/包名/版本/package/</c> 落盘，跨技能、跨次执行复用。
/// </summary>
public sealed class NuGetSkillReferenceResolver
{
    private static readonly NuGetFramework DefaultTarget = NuGetFramework.ParseFolder("net10.0");

    private readonly string _cacheRoot;
    private readonly string _sourceUrl;
    private readonly NuGetFramework _target;
    private readonly ILogger _nugetLogger = NullLogger.Instance;
    private readonly FrameworkReducer _reducer = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _tpaNames;
    private readonly SourceCacheContext _cacheContext;

    public NuGetSkillReferenceResolver(string cacheRoot, string sourceUrl = "https://api.nuget.org/v3/index.json", NuGetFramework? targetFramework = null)
    {
        _cacheRoot = cacheRoot;
        _sourceUrl = sourceUrl;
        _target = targetFramework ?? DefaultTarget;
        Directory.CreateDirectory(_cacheRoot);
        _tpaNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var n = Path.GetFileNameWithoutExtension(p);
            if (!string.IsNullOrWhiteSpace(n)) _tpaNames.Add(n);
        }
        _cacheContext = new SourceCacheContext { DirectDownload = true };
    }

    /// <summary>还原一组引用（进程内同缓存串行执行，并发安全）。</summary>
    public async Task<NuGetResolveResult> ResolveAsync(IEnumerable<SkillNuGetParser.NuGetDirective> directives, CancellationToken ct)
    {
        var result = new NuGetResolveResult();
        var items = directives.Where(d => !string.IsNullOrWhiteSpace(d.PackageId)).ToList();
        if (items.Count == 0)
        {
            result.Error = "没有可还原的 NuGet 引用。";
            return result;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var repo = Repository.Factory.GetCoreV3(_sourceUrl);
            var finder = await repo.GetResourceAsync<FindPackageByIdResource>(ct);

            // BFS 解析依赖闭包：id → 选定版本（先到先得，冲突不覆盖，与 NuGet 最低可用近似）
            var chosen = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<(string Id, string? VersionHint)>();
            foreach (var d in items)
            {
                if (chosen.ContainsKey(d.PackageId))
                {
                    result.Log.Add($"跳过重复顶层引用：{d.PackageId}（第 {d.Line} 行）");
                    continue;
                }
                pending.Enqueue((d.PackageId, d.Version));
            }

            while (pending.Count > 0 && !ct.IsCancellationRequested)
            {
                var (id, hint) = pending.Dequeue();
                if (chosen.ContainsKey(id)) continue;

                var version = await PickVersionAsync(finder, id, hint, result, ct);
                if (version is null) return result; // PickVersionAsync 已写 Error
                chosen[id] = version;
                result.Log.Add($"选定 {id} {version.ToNormalizedString()}" + (hint is null ? "（最新稳定版）" : ""));

                try
                {
                    await EnsurePackageAsync(finder, id, version, ct); // 下载 + 解压到本地缓存
                }
                catch (Exception ex)
                {
                    result.Error = $"下载/解压包 {id} {version.ToNormalizedString()} 失败：{ex.Message}";
                    return result;
                }

                var deps = FindDependencyHints(id, version);
                foreach (var (depId, range) in deps)
                {
                    if (chosen.ContainsKey(depId)
                        || pending.Any(q => string.Equals(q.Id, depId, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    pending.Enqueue((depId, range));
                }
            }

            // 汇总所有选定包的可用程序集（过滤框架自带，避免双身份；跨包同名单取第一个）
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var totalManaged = 0;     // 包内出现的托管程序集总数
            var providedByHost = 0;   // 其中由宿主环境（框架/进程自带）已提供而无需额外加载的
            foreach (var (id, version) in chosen)
            {
                var extract = Path.Combine(PackageDir(id, version), "package");
                if (!Directory.Exists(extract))
                {
                    result.Error = $"包 {id} {version.ToNormalizedString()} 未成功解压（目录缺失）";
                    return result;
                }
                foreach (var dll in SelectPackageDlls(extract))
                {
                    totalManaged++;
                    var name = Path.GetFileNameWithoutExtension(dll);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (_tpaNames.Contains(name)) { providedByHost++; continue; } // 宿主已带（如 Newtonsoft 是 NuGet 客户端依赖）→ 无需重复加载
                    if (!usedNames.Add(name)) continue;
                    result.ReferencePaths.Add(dll);
                }
            }

            // 包里找不到任何托管程序集 → 才判失败；若包内程序集全部由宿主环境自带（如 Newtonsoft.Json 恰为
            // NuGet 客户端依赖）则视为成功——编译时宿主本来就能引用到这些程序集，运行时也从宿主进程解析。
            if (result.ReferencePaths.Count == 0)
            {
                if (totalManaged == 0)
                    result.Error = "已还原引用，但未在这些包中找到可引用的托管程序集（可能只含原生资源，或包目录为空）。";
                else
                    result.Log.Add($"包内 {totalManaged} 个托管程序集全部由宿主环境提供（无需额外加载，已跳过）");
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>把待选版本解析成版本区间求值的“提示字符串”：精确/下界 → 该版本；无界/浮动 → null（取最新稳定）。</summary>
    private static string? VersionHintFromRange(VersionRange? range)
    {
        if (range is null) return null;
        if (range.MinVersion is { } mn && range.IsMinInclusive && !range.IsFloating)
            return mn.ToNormalizedString();
        return null;
    }

    private async Task<NuGetVersion?> PickVersionAsync(FindPackageByIdResource finder, string id, string? hint, NuGetResolveResult result, CancellationToken ct)
    {
        var versions = (await finder.GetAllVersionsAsync(id, _cacheContext, _nugetLogger, ct) ?? []).ToArray();
        if (versions.Length == 0)
        {
            result.Error = $"找不到 NuGet 包：{id}（源 {_sourceUrl} 无任何版本）";
            return null;
        }

        if (string.IsNullOrWhiteSpace(hint))
        {
            // 未写版本 → 最新稳定版；全为预发布才用最高预发布
            var stable = versions.Where(v => !v.IsPrerelease).ToArray();
            if (stable.Length == 0)
            {
                var highest = versions.OrderByDescending(v => v).First();
                return versions.First(v => v == highest);
            }
            var latest = stable.OrderByDescending(v => v).First();
            return versions.First(v => v == latest);
        }

        var text = hint.Trim();
        if (!VersionRange.TryParse(text, out var range) || range is null)
        {
            result.Error = $"包 {id} 的版本写法无法解析：\"{text}\"（示例：#r \"nuget: {id}, 1.2.3\"）";
            return null;
        }
        var allowPre = text.IndexOf('-') >= 0;
        var inRange = versions.Where(v => range.Satisfies(v)).OrderBy(v => v).ToArray();
        if (inRange.Length == 0)
        {
            var sample = string.Join(", ", versions.OrderByDescending(v => v).Where(v => !v.IsPrerelease).Take(6).Select(v => v.ToNormalizedString()));
            result.Error = $"包 {id} 没有满足版本范围 \"{text}\" 的版本（可用稳定版本示例：{sample}）";
            return null;
        }
        var pick = inRange.FirstOrDefault(v => !v.IsPrerelease);
        if (pick is null)
        {
            if (!allowPre)
            {
                result.Error = $"包 {id} 在版本范围 \"{text}\" 内只有预发布版本（如需预发布请写全版本号，如含 -beta 后缀）";
                return null;
            }
            pick = inRange[0]; // 范围内最低（含预发布）
        }
        return pick;
    }

    /// <summary>从已解压 nuspec 的“就近 TFM 依赖组”读取依赖（比注册表扁平聚合更贴近实际编译目标）。</summary>
    private List<(string Id, string? VersionHint)> FindDependencyHints(string id, NuGetVersion version)
    {
        var list = new List<(string, string?)>();
        var extract = Path.Combine(PackageDir(id, version), "package");
        if (!Directory.Exists(extract)) return list;
        var nuspec = Directory.EnumerateFiles(extract, "*.nuspec", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (nuspec is null) return list;

        NuGet.Packaging.NuspecReader reader;
        try { reader = new NuGet.Packaging.NuspecReader(nuspec); }
        catch { return list; }
        var groups = reader.GetDependencyGroups().Where(g => g is not null).ToList();
        if (groups.Count == 0) return list;

        // 就近框架组：无依赖组集合时优先“无 TFM 约束”组；否则取与目标框架最近的一组
        NuGet.Packaging.PackageDependencyGroup? chosen = null;
        var generic = groups.Where(g => g.TargetFramework is null).FirstOrDefault();
        if (generic is not null)
        {
            chosen = generic;
        }
        else
        {
            var frameworks = groups.Where(g => g.TargetFramework is not null).Select(g => g.TargetFramework!).ToList();
            var nearest = _reducer.GetNearest(_target, frameworks);
            chosen = groups.FirstOrDefault(g => Equals(g.TargetFramework, nearest)) ?? groups[0];
        }
        if (chosen?.Packages is null) return list;
        foreach (var dep in chosen.Packages)
        {
            if (string.IsNullOrWhiteSpace(dep.Id)) continue;
            list.Add((dep.Id.Trim(), VersionHintFromRange(dep.VersionRange)));
        }
        return list;
    }

    private string PackageDir(string id, NuGetVersion version)
        => Path.Combine(_cacheRoot, SanitizeSegment(id), version.ToNormalizedString());

    /// <summary>下载 nupkg 并解压到缓存（幂等：已解压过直接跳过）；防 Zip 路径穿越。
    /// 外层已持有 <see cref="_gate"/>，同缓存不会并发写。</summary>
    private async Task EnsurePackageAsync(FindPackageByIdResource finder, string id, NuGetVersion version, CancellationToken ct)
    {
        var pkgDir = PackageDir(id, version);
        var extract = Path.Combine(pkgDir, "package");
        if (File.Exists(Path.Combine(pkgDir, ".ok"))) return;

        Directory.CreateDirectory(extract);
        using var ms = new MemoryStream();
        var copied = await finder.CopyNupkgToStreamAsync(id, version, ms, _cacheContext, _nugetLogger, ct);
        if (!copied)
            throw new InvalidOperationException($"包不存在于源：{id} {version.ToNormalizedString()}");
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // 目录项
            var full = Path.GetFullPath(Path.Combine(extract, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(Path.GetFullPath(extract) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"包 {id} 含越界路径项：{entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var src = entry.Open();
            using var dst = File.Create(full);
            await src.CopyToAsync(dst, ct);
        }
        await File.WriteAllTextAsync(Path.Combine(pkgDir, ".ok"), "ok", ct);
    }

    private static string SanitizeSegment(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(id.Length);
        foreach (var c in id) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }

    /// <summary>从解压目录选出<b>本平台可用</b>的程序集，按优先级：runtimes/&lt;os&gt;/lib/&lt;tfm&gt; → lib/&lt;tfm&gt; → 根目录。
    /// 许多 Windows 专属包（System.ServiceProcess.ServiceController、Microsoft.Win32.Registry 等）在 lib/ 里放的是
    /// “非 Windows 抛 PlatformNotSupported”的平台中性桩，真实现放在 runtimes/win/lib/ 下——若只加载 lib 桩，
    /// 即使在 Windows 上也会误报“不适用于其它操作系统”。故须优先取与当前 OS 匹配的 runtimes 实现。</summary>
    private IEnumerable<string> SelectPackageDlls(string extractDir)
    {
        // 1) runtimes/<rid>/lib/<tfm>/*.dll：仅匹配当前操作系统（Windows / Linux / macOS），避免取到“其它系统桩”
        var ridRoot = Path.Combine(extractDir, "runtimes");
        if (Directory.Exists(ridRoot))
        {
            foreach (var d in Directory.GetDirectories(ridRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var rid = Path.GetFileName(d);
                if (string.IsNullOrWhiteSpace(rid)) continue;
                if (!RidMatchesThisOs(rid)) continue;
                var dlls = LibDlls(Path.Combine(d, "lib"));
                if (dlls.Any()) return dlls; // 匹配到本 OS 的运行时实现即返回（优先于 lib 桩）
            }
        }

        // 2) 常规 lib/<tfm>/*.dll
        var libRoot = Path.Combine(extractDir, "lib");
        if (Directory.Exists(libRoot))
        {
            var dlls = LibDlls(libRoot);
            if (dlls.Any()) return dlls;
        }

        // 3) 旧式包：程序集直接放根目录
        return Directory.Exists(extractDir) ? Directory.EnumerateFiles(extractDir, "*.dll", SearchOption.TopDirectoryOnly) : [];
    }

    private IEnumerable<string> LibDlls(string libRoot)
    {
        if (!Directory.Exists(libRoot)) return [];
        var folders = new List<(string Dir, NuGetFramework Tfm)>();
        foreach (var d in Directory.GetDirectories(libRoot))
        {
            NuGetFramework? tfm = null;
            try { tfm = NuGetFramework.ParseFolder(Path.GetFileName(d)); } catch { /* 无法解析的目录名忽略 */ }
            if (tfm is not null && tfm.Framework != NuGetFramework.UnsupportedFramework.Framework)
                folders.Add((d, tfm));
        }
        if (folders.Count == 0) return [];
        var nearest = _reducer.GetNearest(_target, folders.Select(f => f.Tfm));
        var chosen = folders.FirstOrDefault(f => Equals(f.Tfm, nearest)).Dir ?? folders[0].Dir;
        return Directory.Exists(chosen) ? Directory.EnumerateFiles(chosen, "*.dll", SearchOption.TopDirectoryOnly) : [];
    }

    /// <summary>粗略判断 RID（runtimes 下目录名）是否属于当前操作系统。</summary>
    private static bool RidMatchesThisOs(string rid)
    {
        if (OperatingSystem.IsWindows())
            return rid.Contains("win", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsLinux())
            return rid.Contains("linux", StringComparison.OrdinalIgnoreCase)
                || rid.Equals("unix", StringComparison.OrdinalIgnoreCase)
                || rid.Equals("any", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsMacOS())
            return rid.Contains("osx", StringComparison.OrdinalIgnoreCase)
                || rid.Contains("macos", StringComparison.OrdinalIgnoreCase)
                || rid.Equals("unix", StringComparison.OrdinalIgnoreCase)
                || rid.Equals("any", StringComparison.OrdinalIgnoreCase);
        return rid.Equals("any", StringComparison.OrdinalIgnoreCase);
    }
}
